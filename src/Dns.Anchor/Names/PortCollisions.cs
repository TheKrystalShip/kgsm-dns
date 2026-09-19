using System.Net;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>Turns a stated host into the addresses it reaches.</summary>
public interface IHostResolver
{
    /// <summary>The host's addresses, or <see langword="null"/> when it could not be resolved.</summary>
    Task<IReadOnlyList<IPAddress>?> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Resolves a host through the machine's own resolver; an address literal is itself.</summary>
public sealed class SystemHostResolver : IHostResolver
{
    public async Task<IReadOnlyList<IPAddress>?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
            return [literal];

        try
        {
            IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length > 0 ? addresses : null;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which game servers on different members want the same port at the same public address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Members behind one router share one port space.</b> Two nodes whose stated hosts resolve to a
/// common address are reached through the same forwards, so two servers declaring an overlapping port on
/// the same protocol cannot both be reached, whichever of them is running. Each is flagged with the
/// other; both names stay published, because which of them runs is the router's and the hosts' to
/// decide, not the zone's.
/// </para>
/// <para>
/// <b>Servers on one node are not compared.</b> A node already knows its own servers' ports, and the
/// engine answers for two of them wanting one port on the machine that runs both.
/// </para>
/// <para>
/// <b>Unknown is not none.</b> A server whose node has not stated a host or whose host cannot be
/// resolved, whose ports this anchor has not been told, or which could share an address with such a
/// server, is reported as unknown rather than as colliding with nothing. A node that stated it has no
/// public host shares no address with anything.
/// </para>
/// <para>
/// Hosts are resolved on every check, since a dynamic-DNS name moves when the address behind it does;
/// the result is held in memory and rebuilt on the anchor's zone pass.
/// </para>
/// </remarks>
public sealed class PortCollisions(SqliteNameRegistry registry, IHostResolver resolver)
{
    private volatile IReadOnlyDictionary<string, IReadOnlyList<DnsCollision>?> _current =
        new Dictionary<string, IReadOnlyList<DnsCollision>?>(StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<string, IReadOnlySet<IPAddress>?> _addresses =
        new Dictionary<string, IReadOnlySet<IPAddress>?>(StringComparer.Ordinal);

    /// <summary>
    /// What a member's stated host resolved to at the last check: empty for a member that stated none,
    /// null when it could not be resolved or the member has not been checked.
    /// </summary>
    public IReadOnlySet<IPAddress>? AddressesOf(string member) =>
        _addresses.TryGetValue(member, out IReadOnlySet<IPAddress>? addresses) ? addresses : null;

    /// <summary>
    /// A game name's collisions as of the last check: empty for none, null when it could not be told or
    /// the name has not been checked.
    /// </summary>
    public IReadOnlyList<DnsCollision>? For(string name) =>
        _current.TryGetValue(name, out IReadOnlyList<DnsCollision>? collisions) ? collisions : null;

    /// <summary>Check every game name again.</summary>
    /// <returns>The members holding a name whose collisions changed, so each can be told.</returns>
    /// <param name="ct">Cancels the check.</param>
    /// <param name="self">
    /// The member running the check, and the host its configuration states. It states none to itself, so
    /// without this it would be the one member whose address is never known.
    /// </param>
    public async Task<IReadOnlySet<string>> RefreshAsync(
        CancellationToken ct, (string Member, string Host)? self = null)
    {
        IReadOnlyList<NameRow> games = registry.OfKind(NameKind.Game);
        IReadOnlyDictionary<string, IReadOnlyList<DnsPort>> ports = registry.AllPorts();

        // Every member that stated a host is resolved, not only those running a game server, so the
        // members sharing a router can be seen before either of them runs one.
        var addresses = new Dictionary<string, IReadOnlySet<IPAddress>?>(StringComparer.Ordinal);
        foreach (string member in games.Select(g => g.Member)
                     .Concat(registry.StatedMembers())
                     .Concat(self is { } s ? [s.Member] : [])
                     .Distinct(StringComparer.Ordinal))
        {
            // Never stated is unknown; stated none is a node nobody outside reaches, which shares no
            // public address with anything.
            string? host = self is { } me && string.Equals(member, me.Member, StringComparison.Ordinal)
                ? me.Host
                : registry.HostOf(member);
            if (host is not null && string.IsNullOrWhiteSpace(host))
            {
                addresses[member] = new HashSet<IPAddress>();
                continue;
            }

            IReadOnlyList<IPAddress>? resolved = host is null
                ? null
                : await resolver.ResolveAsync(host.Trim(), ct).ConfigureAwait(false);
            addresses[member] = resolved is null ? null : new HashSet<IPAddress>(resolved);
        }

        Dictionary<string, IReadOnlyList<DnsCollision>?> next = Compute(games, ports, addresses);

        var touched = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyList<DnsCollision>?> previous = _current;
        foreach (NameRow game in games)
        {
            previous.TryGetValue(game.Name, out IReadOnlyList<DnsCollision>? before);
            next.TryGetValue(game.Name, out IReadOnlyList<DnsCollision>? after);
            if (!Same(before, after) || !previous.ContainsKey(game.Name))
                touched.Add(game.Member);
        }

        _current = next;
        _addresses = addresses;
        return touched;
    }

    internal static Dictionary<string, IReadOnlyList<DnsCollision>?> Compute(
        IReadOnlyList<NameRow> games,
        IReadOnlyDictionary<string, IReadOnlyList<DnsPort>> ports,
        IReadOnlyDictionary<string, IReadOnlySet<IPAddress>?> addresses)
    {
        var result = new Dictionary<string, IReadOnlyList<DnsCollision>?>(StringComparer.Ordinal);

        foreach (NameRow game in games)
        {
            IReadOnlySet<IPAddress>? mine = addresses.GetValueOrDefault(game.Member);
            if (mine is null || !ports.TryGetValue(game.Name, out IReadOnlyList<DnsPort>? myPorts))
            {
                result[game.Name] = null;
                continue;
            }

            var found = new List<DnsCollision>();
            bool unknown = false;

            foreach (NameRow other in games)
            {
                if (string.Equals(other.Member, game.Member, StringComparison.Ordinal))
                    continue;

                IReadOnlySet<IPAddress>? theirs = addresses.GetValueOrDefault(other.Member);
                if (theirs is null)
                {
                    // A node whose address is unknown could be behind this one's router.
                    unknown = true;
                    break;
                }

                if (!mine.Overlaps(theirs))
                    continue;

                if (!ports.TryGetValue(other.Name, out IReadOnlyList<DnsPort>? theirPorts))
                {
                    unknown = true;
                    break;
                }

                foreach (DnsPort a in myPorts)
                {
                    foreach (DnsPort b in theirPorts)
                    {
                        if (!string.Equals(a.Protocol, b.Protocol, StringComparison.OrdinalIgnoreCase))
                            continue;

                        int start = Math.Max(a.Start, b.Start);
                        int end = Math.Min(a.End, b.End);
                        if (start <= end)
                            found.Add(new DnsCollision(other.Name, other.Member, start, end, a.Protocol.ToLowerInvariant()));
                    }
                }
            }

            result[game.Name] = unknown
                ? null
                : [.. found.Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Start)];
        }

        return result;
    }

    private static bool Same(IReadOnlyList<DnsCollision>? a, IReadOnlyList<DnsCollision>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}
