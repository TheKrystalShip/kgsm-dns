using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// The names this member is reached at, for the cluster to advertise: every name the anchor gave it that
/// its generated site serves with a certificate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Served, not merely held.</b> A name the anchor has published but this member has no certificate
/// for yet does not answer over HTTPS here, and advertising it would send every caller to a handshake
/// that fails.
/// </para>
/// <para>
/// A node's own name leads: it is the member's address whatever capabilities it holds, while a
/// capability name moves away with the capability.
/// </para>
/// </remarks>
public sealed class DnsSelfAddresses(DnsNameBook book, NginxSite site) : ISelfAddressSource
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Addresses
    {
        get
        {
            IReadOnlyList<string> served = site.Served;
            string? node = book.NodeName;
            return [.. served
                .OrderBy(name => string.Equals(name, node, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(name => "https://" + name)];
        }
    }
}
