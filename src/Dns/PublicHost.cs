using System.Net;
using System.Net.Sockets;

namespace TheKrystalShip.KGSM.Dns;

/// <summary>
/// Where a member is reached on the public internet, as a record can point at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hostname is the normal case, and a dynamic-DNS name is the usual one.</b> Most members sit on a
/// home connection whose address the provider moves whenever it likes, and a dynamic-DNS client on
/// that network already follows it. A cluster name is then a <c>CNAME</c> to that hostname and never
/// has to move itself: the address changes one level below, where something is already watching it.
/// </para>
/// <para>
/// An address literal is accepted for a member that does have a fixed one, and becomes an address
/// record. A private, loopback, link-local or shared carrier-grade address is refused: published, it
/// would be a name that resolves for nobody outside one building.
/// </para>
/// </remarks>
/// <param name="Type">`CNAME`, `A` or `AAAA`.</param>
/// <param name="Content">The hostname or the address, as the record carries it.</param>
public readonly record struct PublicHost(string Type, string Content)
{
    /// <summary>
    /// Read what a member is reached at, or explain why it cannot be published.
    /// </summary>
    /// <param name="written">A hostname or an address literal.</param>
    /// <param name="bases">The cluster's bases, which a host may not sit inside.</param>
    /// <param name="host">The host, when it can be published.</param>
    /// <param name="reason">Why it cannot, otherwise. One line, for a person.</param>
    public static bool TryRead(string? written, ClusterBases bases, out PublicHost host, out string reason)
    {
        ArgumentNullException.ThrowIfNull(bases);

        host = default;
        string value = (written ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        if (value.Length == 0)
        {
            reason = "no public host is known for this member";
            return false;
        }

        if (IPAddress.TryParse(value, out IPAddress? address))
        {
            if (!IsPublic(address))
            {
                reason = $"{value} is not a public address, so nothing outside its own network could reach it";
                return false;
            }

            host = new PublicHost(
                address.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A",
                address.ToString());
            reason = "";
            return true;
        }

        string[] labels = value.Split('.');
        if (labels.Length < 2 || value.Length > 253 || !labels.All(DnsLabel.IsValid))
        {
            reason = $"{value} is neither an address nor a hostname";
            return false;
        }

        // A name inside the cluster's own bases would make one cluster name an alias of another, and
        // the two can then point at each other with nothing outside the zone ever being reached.
        if (bases.Owns(value) || IsBase(value, bases))
        {
            reason = $"{value} is one of this cluster's own names, and a name cannot stand in for itself";
            return false;
        }

        host = new PublicHost("CNAME", value);
        reason = "";
        return true;
    }

    private static bool IsBase(string value, ClusterBases bases) =>
        string.Equals(value, bases.BaseOf(NameKind.Member), StringComparison.Ordinal)
        || string.Equals(value, bases.BaseOf(NameKind.Capability), StringComparison.Ordinal)
        || string.Equals(value, bases.BaseOf(NameKind.Game), StringComparison.Ordinal);

    /// <summary>Whether an address is reachable from the public internet at all.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 10
                || b[0] == 0
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // shared carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                 // link-local
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);                                // multicast and reserved
        }

        return !(address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast
            || address.Equals(IPAddress.IPv6None));
    }
}
