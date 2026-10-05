using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class NewznabNdjsonFixture
    {
        [TestCase("application/x-ndjson", true)]
        [TestCase("APPLICATION/X-NDJSON", true)]
        [TestCase("application/rss+xml, application/x-ndjson;q=0.9", true)]
        [TestCase("application/x-ndjson; q=0", false)]
        [TestCase("application/rss+xml", false)]
        [TestCase("*/*", false)]
        [TestCase("application/json", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void should_only_opt_in_when_ndjson_is_explicitly_accepted(string accept, bool expected)
        {
            NewznabNdjson.IsAccepted(new[] { accept }).Should().Be(expected);
        }

        [Test]
        public async Task should_write_one_flushed_json_line_per_record_with_type()
        {
            using var stream = new MemoryStream();

            await NewznabNdjson.WriteAsync(stream, new NdjsonStart { IndexerId = 5, Protocol = "torrent" }, CancellationToken.None);
            await NewznabNdjson.WriteAsync(stream, new NdjsonBatch { Sequence = 1, Releases = new List<NdjsonRelease> { new () { Guid = "g", Title = "t" } } }, CancellationToken.None);
            await NewznabNdjson.WriteAsync(stream, new NdjsonComplete { Batches = 1, ReleaseCount = 1 }, CancellationToken.None);

            var text = Encoding.UTF8.GetString(stream.ToArray());
            var lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

            text.Should().EndWith("\n");
            lines.Should().HaveCount(3);

            var docs = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();

            docs.Select(d => d.GetProperty("type").GetString()).Should().Equal("start", "batch", "complete");
            docs[0].GetProperty("version").GetInt32().Should().Be(1);
            docs[0].GetProperty("indexerId").GetInt32().Should().Be(5);
            docs[1].GetProperty("sequence").GetInt32().Should().Be(1);
            docs[1].GetProperty("releases")[0].GetProperty("guid").GetString().Should().Be("g");
            docs[2].GetProperty("releaseCount").GetInt32().Should().Be(1);
        }

        [Test]
        public async Task should_write_error_record_and_omit_null_fields()
        {
            using var stream = new MemoryStream();

            await NewznabNdjson.WriteAsync(stream, new NdjsonError { Code = 500, Message = "boom", Batches = 2, ReleaseCount = 7 }, CancellationToken.None);

            var doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement;

            doc.GetProperty("type").GetString().Should().Be("error");
            doc.GetProperty("code").GetInt32().Should().Be(500);
            doc.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
        }

        [Test]
        public void should_map_torrent_release_like_torznab_item()
        {
            var release = new TorrentInfo
            {
                Guid = "guid",
                Title = "Show S01E01",
                DownloadUrl = "http://proxy/dl",
                MagnetUrl = "magnet:?xt=urn:btih:abc",
                Size = 1234,
                IndexerId = 3,
                Indexer = "Idx",
                IndexerPrivacy = IndexerPrivacy.Private,
                TvdbId = 42,
                Seeders = 10,
                Peers = 12,
                InfoHash = "abc",
                Categories = new List<IndexerCategory> { new (5040, "TV/HD") }
            };

            var dto = NewznabNdjson.ToDto(release, DownloadProtocol.Torrent, false);

            dto.DownloadUrl.Should().Be("http://proxy/dl");
            NewznabNdjson.ToDto(release, DownloadProtocol.Torrent, true).DownloadUrl.Should().StartWith("magnet:");
            dto.Protocol.Should().Be("torrent");
            dto.IndexerPrivacy.Should().Be("private");
            dto.Categories.Should().Equal(5040);
            dto.TvdbId.Should().Be(42);
            dto.ImdbId.Should().BeNull();
            dto.Seeders.Should().Be(10);
            dto.Size.Should().Be(1234);
        }
    }
}
