using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    /// <summary>
    /// A running search that publishes batches of releases as they become available.
    /// Disposing cancels any outstanding indexer work and waits for it to unwind.
    /// </summary>
    public sealed class NewznabSearchStream : IAsyncDisposable
    {
        private readonly Channel<IList<ReleaseInfo>> _channel = Channel.CreateUnbounded<IList<ReleaseInfo>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        private readonly CancellationTokenSource _cts;
        private readonly Task _producer;

        public NewznabSearchStream(Func<CancellationToken, Action<IList<ReleaseInfo>>, Task> producer, CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var token = _cts.Token;

            _producer = Task.Run(async () =>
            {
                try
                {
                    await producer(token, batch => _channel.Writer.TryWrite(batch));

                    _channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    _channel.Writer.TryComplete(ex);
                }
            });
        }

        /// <summary>
        /// Yields non-empty batches until the search finishes. Throws if the search was cancelled or failed
        /// (after yielding everything published before the failure).
        /// </summary>
        public IAsyncEnumerable<IList<ReleaseInfo>> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            await _producer.ConfigureAwait(false);

            _cts.Dispose();
        }
    }
}
