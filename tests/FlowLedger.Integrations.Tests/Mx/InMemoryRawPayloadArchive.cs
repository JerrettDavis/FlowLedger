using System.Collections.Concurrent;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>Test archive that records every payload so tests can assert retention.</summary>
internal sealed class InMemoryRawPayloadArchive : IRawPayloadArchive
{
    public ConcurrentQueue<(string Provider, string Operation, byte[] Payload, RawPayloadContext? Context)> Items { get; } = new();
    public bool FailOnArchive { get; set; }

    public Task<string> ArchiveAsync(string provider, string operation, ReadOnlyMemory<byte> payload, RawPayloadContext? context = null, CancellationToken cancellationToken = default)
    {
        if (FailOnArchive)
        {
            throw new IOException("simulated archive failure");
        }

        Items.Enqueue((provider, operation, payload.ToArray(), context));
        return Task.FromResult($"mem/{provider}/{operation}/{Items.Count}");
    }
}
