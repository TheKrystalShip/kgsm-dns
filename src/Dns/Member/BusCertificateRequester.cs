using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>Asks the member holding <c>dns</c> for a certificate, over the bus.</summary>
/// <remarks>
/// Only the request travels: a PKCS#10 request carries the public key and a signature over it, and the
/// private key stays in this member's own directory.
/// </remarks>
public sealed class BusCertificateRequester(
    ClusterStateStore state,
    MembersStore members,
    IClusterBus bus) : ICertificateRequester
{
    public async Task RequestAsync(string name, byte[] csrDer, CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(holder))
            return;

        MemberRow? row = await members.GetByMemberIdAsync(holder, ct).ConfigureAwait(false);
        if (row is null || !row.Enabled || string.IsNullOrWhiteSpace(row.Url))
            return;

        await bus.EnqueueAsync(
            DnsMessageTypes.Csr,
            new DnsCsr(name, Convert.ToBase64String(csrDer)),
            DnsJsonContext.Default.DnsCsr,
            [new ClusterTarget(row.MemberId, row.Url)],
            ct).ConfigureAwait(false);
    }
}
