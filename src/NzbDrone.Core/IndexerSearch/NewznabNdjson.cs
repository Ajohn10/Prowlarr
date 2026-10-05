using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    /// <summary>
    /// Opt-in progressive search protocol (JSON lines). Every line is one JSON object with a "type":
    /// start, batch, then exactly one terminal record: complete or error.
    /// </summary>
    public static class NewznabNdjson
    {
        public const string MediaType = "application/x-ndjson";
        public const int Version = 1;

        public static readonly JsonSerializerOptions SerializerOptions = new ()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // True only when the caller explicitly lists application/x-ndjson with a non-zero quality.
        public static bool IsAccepted(IEnumerable<string> acceptHeaderValues)
        {
            if (acceptHeaderValues == null)
            {
                return false;
            }

            foreach (var value in acceptHeaderValues.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                foreach (var part in value.Split(','))
                {
                    var segments = part.Split(';');

                    if (!string.Equals(segments[0].Trim(), MediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rejected = segments.Skip(1)
                        .Select(s => s.Trim())
                        .Any(s => s.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                                  double.TryParse(s.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q) &&
                                  q <= 0);

                    if (!rejected)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static async Task WriteAsync(Stream stream, NdjsonRecord record, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, record.GetType(), SerializerOptions) + "\n");

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static NdjsonRelease ToDto(ReleaseInfo r, DownloadProtocol protocol, bool preferMagnetUrl)
        {
            var t = r as TorrentInfo;
            var downloadUrl = preferMagnetUrl ? t?.MagnetUrl ?? r.DownloadUrl : r.DownloadUrl ?? t?.MagnetUrl;

            return new NdjsonRelease
            {
                Guid = r.Guid,
                Title = r.Title,
                Description = r.Description,
                IndexerId = r.IndexerId,
                Indexer = r.Indexer,
                IndexerPrivacy = r.IndexerPrivacy switch { IndexerPrivacy.Private => "private", IndexerPrivacy.Public => "public", _ => "semi-private" },
                Protocol = protocol == DownloadProtocol.Torrent ? "torrent" : "usenet",
                InfoUrl = r.InfoUrl,
                PublishDate = (r.PublishDate == DateTime.MinValue ? DateTime.Now : r.PublishDate).ToUniversalTime(),
                Size = r.Size,
                DownloadUrl = downloadUrl,
                Categories = r.Categories?.Select(c => c.Id).ToList(),
                Flags = r.IndexerFlags?.Select(f => f.Name).ToList(),
                Languages = r.Languages?.ToList(),
                Subs = r.Subs?.ToList(),
                Genres = r.Genres?.ToList(),
                TvRageId = NonZero(r.TvRageId),
                TvdbId = NonZero(r.TvdbId),
                ImdbId = NonZero(r.ImdbId),
                TmdbId = NonZero(r.TmdbId),
                TraktId = NonZero(r.TraktId),
                TvMazeId = NonZero(r.TvMazeId),
                DoubanId = NonZero(r.DoubanId),
                Year = NonZero(r.Year),
                Seeders = t?.Seeders,
                Peers = t?.Peers,
                Files = r.Files,
                Grabs = r.Grabs,
                Author = r.Author,
                BookTitle = r.BookTitle,
                Artist = r.Artist,
                Album = r.Album,
                Label = r.Label,
                Track = r.Track,
                InfoHash = t?.InfoHash,
                MinimumRatio = t?.MinimumRatio,
                MinimumSeedTime = t?.MinimumSeedTime,
                DownloadVolumeFactor = t?.DownloadVolumeFactor,
                UploadVolumeFactor = t?.UploadVolumeFactor,
                PosterUrl = r.PosterUrl
            };
        }

        private static int? NonZero(int value) => value == 0 ? null : value;
    }

    public abstract class NdjsonRecord
    {
        public abstract string Type { get; }
    }

    // {"type":"start","version":1,"indexerId":5,"protocol":"torrent"}
    public class NdjsonStart : NdjsonRecord
    {
        public override string Type => "start";
        public int Version { get; set; } = NewznabNdjson.Version;
        public int IndexerId { get; set; }
        public string Protocol { get; set; }
    }

    // {"type":"batch","sequence":1,"releases":[...]} - sequence is 1-based and contiguous.
    public class NdjsonBatch : NdjsonRecord
    {
        public override string Type => "batch";
        public int Sequence { get; set; }
        public List<NdjsonRelease> Releases { get; set; }
    }

    // {"type":"complete","batches":2,"releaseCount":120} - search finished normally.
    public class NdjsonComplete : NdjsonRecord
    {
        public override string Type => "complete";
        public int Batches { get; set; }
        public int ReleaseCount { get; set; }
    }

    // {"type":"error","code":429,"message":"...","retryAfterSeconds":60,"batches":1,"releaseCount":20}
    // Terminal. Batches already sent are valid partial results; the search did not complete.
    public class NdjsonError : NdjsonRecord
    {
        public override string Type => "error";
        public int Code { get; set; }
        public string Message { get; set; }
        public int? RetryAfterSeconds { get; set; }
        public int Batches { get; set; }
        public int ReleaseCount { get; set; }
    }

    // Null/zero values are omitted. Mirrors the fields of the Torznab/Newznab RSS item.
    public class NdjsonRelease
    {
        public string Guid { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public int IndexerId { get; set; }
        public string Indexer { get; set; }
        public string IndexerPrivacy { get; set; }
        public string Protocol { get; set; }
        public string InfoUrl { get; set; }
        public DateTime PublishDate { get; set; }
        public long? Size { get; set; }
        public string DownloadUrl { get; set; }
        public List<int> Categories { get; set; }
        public List<string> Flags { get; set; }
        public List<string> Languages { get; set; }
        public List<string> Subs { get; set; }
        public List<string> Genres { get; set; }
        public int? TvRageId { get; set; }
        public int? TvdbId { get; set; }
        public int? ImdbId { get; set; }
        public int? TmdbId { get; set; }
        public int? TraktId { get; set; }
        public int? TvMazeId { get; set; }
        public int? DoubanId { get; set; }
        public int? Year { get; set; }
        public int? Seeders { get; set; }
        public int? Peers { get; set; }
        public int? Files { get; set; }
        public int? Grabs { get; set; }
        public string Author { get; set; }
        public string BookTitle { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string Label { get; set; }
        public string Track { get; set; }
        public string InfoHash { get; set; }
        public double? MinimumRatio { get; set; }
        public long? MinimumSeedTime { get; set; }
        public double? DownloadVolumeFactor { get; set; }
        public double? UploadVolumeFactor { get; set; }
        public string PosterUrl { get; set; }
    }
}
