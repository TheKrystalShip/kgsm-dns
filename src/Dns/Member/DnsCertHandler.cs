using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// Takes a certificate the DNS anchor issued for one of this member's names.
/// </summary>
/// <remarks>
/// Accepted only from the member holding <c>dns</c>, and kept only if it certifies this member's own key
/// for that name — so neither another member nor a mix-up can put a certificate in front of this
/// member's site that its key cannot serve.
/// </remarks>
public sealed class DnsCertHandler(
    ClusterStateStore state,
    CertificateKeeper keeper,
    ILogger<DnsCertHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Cert;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (!string.Equals(holder, envelope.From, StringComparison.Ordinal))
        {
            logger.LogWarning("a certificate from {From} ignored — it does not hold '{Capability}'",
                envelope.From, DnsCapability.Name);
            return;
        }

        DnsCert? cert = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsCert);
        if (cert is null || string.IsNullOrWhiteSpace(cert.Name) || string.IsNullOrWhiteSpace(cert.Chain))
        {
            logger.LogWarning("a certificate from {From} could not be read — ignored", envelope.From);
            return;
        }

        keeper.Receive(cert.Name, cert.Chain);
    }
}
