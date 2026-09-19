using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// Asks for a certificate for one of this member's names ahead of its own schedule, because an admin
/// asked the DNS anchor to renew it.
/// </summary>
/// <remarks>
/// Accepted only from the member holding <c>dns</c>, and acted on only for a name this member holds
/// published — the same name the keeper would ask for on its own once it came due. The request then goes
/// the way every other one does: a signing request to the anchor, a chain back.
/// </remarks>
public sealed class DnsRenewHandler(
    ClusterStateStore state,
    CertificateKeeper keeper,
    ILogger<DnsRenewHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Renew;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (!string.Equals(holder, envelope.From, StringComparison.Ordinal))
        {
            logger.LogWarning("a renewal from {From} ignored — it does not hold '{Capability}'",
                envelope.From, DnsCapability.Name);
            return;
        }

        DnsRenew? renew = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsRenew);
        if (renew is null || string.IsNullOrWhiteSpace(renew.Name))
        {
            logger.LogWarning("a renewal from {From} could not be read — ignored", envelope.From);
            return;
        }

        if (!await keeper.ForceAsync(renew.Name, ct).ConfigureAwait(false))
            logger.LogWarning("a renewal for {Name} ignored — this member does not serve it published", renew.Name);
    }
}
