using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Events;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    public interface IReleaseSearchService
    {
        Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch);
        Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch, CancellationToken cancellationToken);

        /// <summary>
        /// Starts a search that publishes batches of releases as indexer result pages complete.
        /// Request validation errors are thrown synchronously; search failures surface while reading.
        /// Releases are de-duplicated by guid (first seen wins) across the whole stream.
        /// </summary>
        NewznabSearchStream SearchStream(NewznabRequest request, List<int> indexerIds, bool interactiveSearch, CancellationToken cancellationToken);
    }

    public class ReleaseSearchService : IReleaseSearchService
    {
        private readonly IIndexerLimitService _indexerLimitService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;

        public ReleaseSearchService(IEventAggregator eventAggregator,
                                IIndexerFactory indexerFactory,
                                IIndexerLimitService indexerLimitService,
                                Logger logger)
        {
            _eventAggregator = eventAggregator;
            _indexerFactory = indexerFactory;
            _indexerLimitService = indexerLimitService;
            _logger = logger;
        }

        public Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            return Search(request, indexerIds, interactiveSearch, CancellationToken.None);
        }

        public async Task<NewznabResults> Search(NewznabRequest request, List<int> indexerIds, bool interactiveSearch, CancellationToken cancellationToken)
        {
            var (searchSpec, searchAction) = Prepare(request, indexerIds, interactiveSearch);

            searchSpec.CancellationToken = cancellationToken;

            var releases = await Dispatch(searchAction, searchSpec);

            return new NewznabResults { Releases = DeDupeReleases(releases) };
        }

        public NewznabSearchStream SearchStream(NewznabRequest request, List<int> indexerIds, bool interactiveSearch, CancellationToken cancellationToken)
        {
            var (searchSpec, searchAction) = Prepare(request, indexerIds, interactiveSearch);

            return new NewznabSearchStream(
                async (token, publish) =>
                {
                    searchSpec.CancellationToken = token;

                    await Dispatch(searchAction, searchSpec, publish);
                },
                cancellationToken);
        }

        private (SearchCriteriaBase Spec, Func<IIndexer, Task<IndexerPageableQueryResult>> Action) Prepare(NewznabRequest request, List<int> indexerIds, bool interactiveSearch)
        {
            switch (request.t)
            {
                case "movie":
                    {
                        var searchSpec = Get<MovieSearchCriteria>(request, indexerIds, interactiveSearch);

                        var imdbId = ParseUtil.GetImdbId(request.imdbid);

                        searchSpec.ImdbId = imdbId?.ToString("D7");
                        searchSpec.TmdbId = request.tmdbid;
                        searchSpec.TraktId = request.traktid;
                        searchSpec.DoubanId = request.doubanid;
                        searchSpec.Year = request.year;
                        searchSpec.Genre = request.genre;

                        return (searchSpec, indexer => indexer.Fetch(searchSpec));
                    }

                case "music":
                    {
                        var searchSpec = Get<MusicSearchCriteria>(request, indexerIds, interactiveSearch);

                        searchSpec.Artist = request.artist;
                        searchSpec.Album = request.album;
                        searchSpec.Label = request.label;
                        searchSpec.Genre = request.genre;
                        searchSpec.Track = request.track;
                        searchSpec.Year = request.year;

                        return (searchSpec, indexer => indexer.Fetch(searchSpec));
                    }

                case "tvsearch":
                    {
                        var searchSpec = Get<TvSearchCriteria>(request, indexerIds, interactiveSearch);

                        var imdbId = ParseUtil.GetImdbId(request.imdbid);

                        searchSpec.ImdbId = imdbId?.ToString("D7");
                        searchSpec.Season = request.season;
                        searchSpec.Episode = request.ep;
                        searchSpec.TvdbId = request.tvdbid;
                        searchSpec.TraktId = request.traktid;
                        searchSpec.TmdbId = request.tmdbid;
                        searchSpec.DoubanId = request.doubanid;
                        searchSpec.RId = request.rid;
                        searchSpec.TvMazeId = request.tvmazeid;
                        searchSpec.Year = request.year;
                        searchSpec.Genre = request.genre;

                        return (searchSpec, indexer => indexer.Fetch(searchSpec));
                    }

                case "book":
                    {
                        var searchSpec = Get<BookSearchCriteria>(request, indexerIds, interactiveSearch);

                        searchSpec.Author = request.author;
                        searchSpec.Title = request.title;
                        searchSpec.Publisher = request.publisher;
                        searchSpec.Year = request.year;
                        searchSpec.Genre = request.genre;

                        return (searchSpec, indexer => indexer.Fetch(searchSpec));
                    }

                default:
                    {
                        var searchSpec = Get<BasicSearchCriteria>(request, indexerIds, interactiveSearch);

                        return (searchSpec, indexer => indexer.Fetch(searchSpec));
                    }
            }
        }

        private TSpec Get<TSpec>(NewznabRequest query, List<int> indexerIds, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec
            {
                InteractiveSearch = interactiveSearch
            };

            spec.Categories = query.cat != null ? query.cat.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).Select(int.Parse).ToArray() : Array.Empty<int>();

            spec.SearchTerm = query.q?.Trim();
            spec.SearchType = query.t;
            spec.Limit = query.limit;
            spec.Offset = query.offset;
            spec.MinAge = query.minage;
            spec.MaxAge = query.maxage;
            spec.MinSize = query.minsize;
            spec.MaxSize = query.maxsize;
            spec.Source = query.source;
            spec.Host = query.host;

            spec.IndexerIds = indexerIds;

            return spec;
        }

        private async Task<IList<ReleaseInfo>> Dispatch(Func<IIndexer, Task<IndexerPageableQueryResult>> searchAction, SearchCriteriaBase criteriaBase, Action<IList<ReleaseInfo>> publish = null)
        {
            var indexers = _indexerFactory.Enabled();

            if (criteriaBase.IndexerIds is { Count: > 0 })
            {
                indexers = indexers.Where(i => criteriaBase.IndexerIds.Contains(i.Definition.Id) ||
                    (criteriaBase.IndexerIds.Contains(-1) && i.Protocol == DownloadProtocol.Usenet) ||
                    (criteriaBase.IndexerIds.Contains(-2) && i.Protocol == DownloadProtocol.Torrent))
                    .ToList();

                if (criteriaBase.InteractiveSearch && indexers.Count == 0)
                {
                    _logger.Debug("Search failed due to all selected indexers being unavailable: {0}", string.Join(", ", criteriaBase.IndexerIds));

                    throw new SearchFailedException("Search failed due to all selected indexers being unavailable");
                }
            }

            if (criteriaBase.Categories is { Length: > 0 })
            {
                // Only query supported indexers
                indexers = indexers.Where(i => i.GetCapabilities().Categories.SupportedCategories(criteriaBase.Categories).Any()).ToList();

                if (indexers.Count == 0)
                {
                    _logger.Debug("All provided categories are unsupported by selected indexers: {0}", string.Join(", ", criteriaBase.Categories));

                    return Array.Empty<ReleaseInfo>();
                }
            }

            _logger.ProgressInfo("Searching indexer(s): [{0}] for {1}", string.Join(", ", indexers.Select(i => i.Definition.Name).ToList()), criteriaBase.ToString());

            Action<IList<ReleaseInfo>> emit = null;

            if (publish != null)
            {
                var seenGuids = new HashSet<string>();
                var indexersById = indexers.GroupBy(i => i.Definition.Id).ToDictionary(g => g.Key, g => g.First());

                // Serializes concurrent indexer callbacks and de-dupes so each guid is published once.
                emit = releases =>
                {
                    List<ReleaseInfo> fresh;

                    lock (seenGuids)
                    {
                        fresh = releases.Where(r => seenGuids.Add(r.Guid ?? string.Empty)).ToList();

                        if (fresh.Count > 0)
                        {
                            publish(fresh);
                        }
                    }
                };

                criteriaBase.ReleasesAvailable = (indexerId, releases) =>
                {
                    if (indexersById.TryGetValue(indexerId, out var indexer))
                    {
                        emit(ApplyFilters(indexer, releases, criteriaBase));
                    }
                };
            }

            var tasks = indexers.Select(async x =>
            {
                var releases = await DispatchIndexer(searchAction, x, criteriaBase);

                // Publishes anything an indexer returned without progressive callbacks (already-seen guids are skipped).
                emit?.Invoke(releases);

                return releases;
            }).ToList();

            var batch = await Task.WhenAll(tasks);

            var reports = batch.SelectMany(x => x).ToList();

            _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexer(s)", reports.Count, criteriaBase, indexers.Count);

            return reports;
        }

        private async Task<IList<ReleaseInfo>> DispatchIndexer(Func<IIndexer, Task<IndexerPageableQueryResult>> searchAction, IIndexer indexer, SearchCriteriaBase criteriaBase)
        {
            if (_indexerLimitService.AtQueryLimit((IndexerDefinition)indexer.Definition))
            {
                return Array.Empty<ReleaseInfo>();
            }

            try
            {
                var indexerReports = await searchAction(indexer);

                var releases = ApplyFilters(indexer, indexerReports.Releases, criteriaBase);

                foreach (var query in indexerReports.Queries)
                {
                    _eventAggregator.PublishEvent(new IndexerQueryEvent(indexer.Definition.Id, criteriaBase, query));
                }

                return releases;
            }
            catch (OperationCanceledException) when (criteriaBase.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _eventAggregator.PublishEvent(new IndexerQueryEvent(indexer.Definition.Id, criteriaBase, new IndexerQueryResult()));
                _logger.Error(e, "Error while searching for {0}", criteriaBase);
            }

            return Array.Empty<ReleaseInfo>();
        }

        private IList<ReleaseInfo> ApplyFilters(IIndexer indexer, IList<ReleaseInfo> releases, SearchCriteriaBase criteriaBase)
        {
            var original = releases;

            //Filter results to only those in searched categories
            if (criteriaBase.Categories.Length > 0)
            {
                var expandedQueryCats = indexer.GetCapabilities().Categories.ExpandTorznabQueryCategories(criteriaBase.Categories);

                releases = releases.Where(result => result.Categories?.Any() != true || expandedQueryCats.Intersect(result.Categories.Select(c => c.Id)).Any()).ToList();

                if (releases.Count != original.Count)
                {
                    _logger.Trace("{0} releases from {1} ({2}) which didn't contain search categories [{3}] were filtered", original.Count - releases.Count, ((IndexerDefinition)indexer.Definition).Name, indexer.Name, string.Join(", ", expandedQueryCats));
                }
            }

            if (criteriaBase.MinAge is > 0)
            {
                var cutoffDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(criteriaBase.MinAge.Value));

                releases = releases.Where(r => r.PublishDate <= cutoffDate).ToList();
            }

            if (criteriaBase.MaxAge is > 0)
            {
                var cutoffDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(criteriaBase.MaxAge.Value));

                releases = releases.Where(r => r.PublishDate >= cutoffDate).ToList();
            }

            if (criteriaBase.MinSize is > 0)
            {
                var minSize = criteriaBase.MinSize.Value;

                releases = releases.Where(r => r.Size >= minSize).ToList();
            }

            if (criteriaBase.MaxSize is > 0)
            {
                var maxSize = criteriaBase.MaxSize.Value;

                releases = releases.Where(r => r.Size <= maxSize).ToList();
            }

            return releases;
        }

        private List<ReleaseInfo> DeDupeReleases(IList<ReleaseInfo> releases)
        {
            // De-dupe reports by guid so duplicate results aren't returned. Pick the one with the higher indexer priority.
            return releases.GroupBy(r => r.Guid)
                .Select(r => r.OrderBy(v => v.IndexerPriority).First())
                .ToList();
        }
    }
}
