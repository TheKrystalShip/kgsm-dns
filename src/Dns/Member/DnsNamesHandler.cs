using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// Takes what the DNS anchor says this member's names are.
/// </summary>
/// <remarks>
/// Accepted only from the member the cluster assigns the <c>dns</c> capability to. Any member can send
/// any message type, so without that check another member could tell this one its name is something
/// the zone does not say.
/// </remarks>
public sealed class DnsNamesHandler(
    ClusterStateStore state,
    DnsNameBook book,
    ILogger<DnsNamesHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Names;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (!string.Equals(holder, envelope.From, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "names from {From} ignored — {Holder} holds '{Capability}'",
                envelope.From, holder ?? "nobody", DnsCapability.Name);
            return;
        }

        DnsNames? names = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsNames);
        if (names is null)
        {
            logger.LogWarning("names from {From} could not be read — ignored", envelope.From);
            return;
        }

        string? before = book.NodeName;
        book.Replace(names.Names);

        if (!string.Equals(before, book.NodeName, StringComparison.Ordinal) && book.NodeName is { } now)
            logger.LogInformation("this node is reached at {Name}", now);
    }
}
