using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    public class ReleaseSearchServiceFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;

        [SetUp]
        public void SetUp()
        {
            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(s => s.Enabled(It.IsAny<bool>()))
                .Returns(new List<IIndexer> { _mockIndexer.Object });
        }

        private List<SearchCriteriaBase> WatchForSearchCriteria()
        {
            var result = new List<SearchCriteriaBase>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(s => result.Add(s))
                .ReturnsAsync(new IndexerPageableQueryResult());

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<TvSearchCriteria>()))
                .Callback<TvSearchCriteria>(s => result.Add(s))
                .ReturnsAsync(new IndexerPageableQueryResult());

            return result;
        }

        [TestCase("tt0183790", "0183790")]
        [TestCase("0183790", "0183790")]
        [TestCase("183790", "0183790")]
        [TestCase("tt10001870", "10001870")]
        [TestCase("10001870", "10001870")]
        public void should_normalize_imdbid_movie_search_criteria(string input, string expected)
        {
            var allCriteria = WatchForSearchCriteria();

            var request = new NewznabRequest
            {
                t = "movie",
                imdbid = input
            };

            Subject.Search(request, new List<int> { 1 }, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
            criteria[0].ImdbId.Should().Be(expected);
        }

        [TestCase("tt0183790", "0183790")]
        [TestCase("0183790", "0183790")]
        [TestCase("183790", "0183790")]
        [TestCase("tt10001870", "10001870")]
        [TestCase("10001870", "10001870")]
        public void should_normalize_imdbid_tv_search_criteria(string input, string expected)
        {
            var allCriteria = WatchForSearchCriteria();

            var request = new NewznabRequest
            {
                t = "tvsearch",
                imdbid = input
            };

            Subject.Search(request, new List<int> { 1 }, false);

            var criteria = allCriteria.OfType<TvSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
            criteria[0].ImdbId.Should().Be(expected);
        }

        private Mock<IIndexer> AddIndexer(int id)
        {
            var mock = new Mock<IIndexer>();
            mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = id, Name = $"Indexer {id}" });
            mock.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(s => s.Enabled(It.IsAny<bool>()))
                .Returns(new List<IIndexer> { _mockIndexer.Object, mock.Object });

            return mock;
        }

        private static ReleaseInfo Release(string guid, int indexerId = 1) => new () { Guid = guid, Title = guid, IndexerId = indexerId };

        private static async Task<List<IList<ReleaseInfo>>> ReadBatches(NewznabSearchStream stream)
        {
            var batches = new List<IList<ReleaseInfo>>();

            await foreach (var batch in stream.ReadAllAsync())
            {
                batches.Add(batch);
            }

            return batches;
        }

        [Test]
        public async Task stream_should_publish_first_page_before_search_finishes()
        {
            var gate = new TaskCompletionSource();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>()))
                .Returns<BasicSearchCriteria>(async s =>
                {
                    s.ReleasesAvailable(1, new List<ReleaseInfo> { Release("a"), Release("b") });
                    await gate.Task;
                    s.ReleasesAvailable(1, new List<ReleaseInfo> { Release("b"), Release("c") });

                    return new IndexerPageableQueryResult { Releases = new List<ReleaseInfo> { Release("a"), Release("b"), Release("c") } };
                });

            await using var stream = Subject.SearchStream(new NewznabRequest { t = "search" }, new List<int> { 1 }, false, CancellationToken.None);

            var enumerator = stream.ReadAllAsync().GetAsyncEnumerator();

            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            enumerator.Current.Select(r => r.Guid).Should().Equal("a", "b");

            gate.SetResult();

            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            enumerator.Current.Select(r => r.Guid).Should().Equal("c");

            (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }

        [Test]
        public async Task stream_should_publish_results_from_indexers_that_do_not_stream()
        {
            _mockIndexer.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>()))
                .ReturnsAsync(new IndexerPageableQueryResult { Releases = new List<ReleaseInfo> { Release("a"), Release("a") } });

            await using var stream = Subject.SearchStream(new NewznabRequest { t = "search" }, new List<int> { 1 }, false, CancellationToken.None);

            var batches = await ReadBatches(stream);

            batches.Should().HaveCount(1);
            batches[0].Select(r => r.Guid).Should().Equal("a");
        }

        [Test]
        public async Task stream_should_isolate_failing_indexer()
        {
            var other = AddIndexer(2);

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>())).ThrowsAsync(new InvalidOperationException("boom"));
            other.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>()))
                .ReturnsAsync(new IndexerPageableQueryResult { Releases = new List<ReleaseInfo> { Release("x", 2) } });

            await using var stream = Subject.SearchStream(new NewznabRequest { t = "search" }, new List<int> { 1, 2 }, false, CancellationToken.None);

            var batches = await ReadBatches(stream);

            batches.SelectMany(b => b).Select(r => r.Guid).Should().Equal("x");
        }

        [Test]
        public async Task stream_should_propagate_cancellation_to_indexer_and_not_swallow_it()
        {
            var observedToken = new TaskCompletionSource<CancellationToken>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>()))
                .Returns<BasicSearchCriteria>(async s =>
                {
                    observedToken.SetResult(s.CancellationToken);
                    await Task.Delay(Timeout.Infinite, s.CancellationToken);

                    return new IndexerPageableQueryResult();
                });

            using var cts = new CancellationTokenSource();

            await using var stream = Subject.SearchStream(new NewznabRequest { t = "search" }, new List<int> { 1 }, false, cts.Token);

            await observedToken.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cts.Cancel();

            Func<Task> read = async () => await ReadBatches(stream).WaitAsync(TimeSpan.FromSeconds(5));

            await read.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task disposing_stream_should_cancel_outstanding_search()
        {
            var observedToken = new TaskCompletionSource<CancellationToken>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<BasicSearchCriteria>()))
                .Returns<BasicSearchCriteria>(async s =>
                {
                    observedToken.SetResult(s.CancellationToken);
                    await Task.Delay(Timeout.Infinite, s.CancellationToken);

                    return new IndexerPageableQueryResult();
                });

            var stream = Subject.SearchStream(new NewznabRequest { t = "search" }, new List<int> { 1 }, false, CancellationToken.None);

            var token = await observedToken.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            token.IsCancellationRequested.Should().BeTrue();
        }
    }
}