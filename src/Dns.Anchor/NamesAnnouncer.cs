using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// Tells a member every name the registry holds for it.
/// </summary>
/// <remarks>
/// Always the whole set. A member keeps nothing but the latest answer, so an announcement that arrives
/// late, twice or out of order is corrected by the next one, and neither side keeps a cursor.
/// </remarks>
public sealed class NamesAnnouncer(
    SqliteNameRegistry registry,
    MembersStore members,
    IClusterBus bus,
    ILogger<NamesAnnouncer> logger)
{
    /// <summary>Send one member its names.</summary>
    /// <returns>Whether there was an address to send them to.</returns>
    public async Task<bool> AnnounceAsync(string memberId, CancellationToken ct)
    {
        MemberRow? row = await members.GetByMemberIdAsync(memberId, ct).ConfigureAwait(false);
        if (row is null || !row.Enabled || string.IsNullOrWhiteSpace(row.Url))
        {
            // A member this anchor cannot address is told on the pass after it becomes addressable,
            // rather than queued at nobody for the whole retry window.
            logger.LogDebug("{Member} has no address yet — its names wait for the next pass", memberId);
            return false;
        }

        List<DnsNameEntry> names =
        [
            .. registry.ForMember(memberId).Select(n => new DnsNameEntry(
                Name: n.Name,
                Kind: NameKinds.Spell(n.Kind),
                Key: n.Key,
                State: Spell(n.State),
                Note: n.Note)),
        ];

        await bus.EnqueueAsync(
            DnsMessageTypes.Names,
            new DnsNames(names),
            DnsJsonContext.Default.DnsNames,
            [new ClusterTarget(row.MemberId, row.Url)],
            ct).ConfigureAwait(false);

        return true;
    }

    private static string Spell(NameState state) => state switch
    {
        NameState.Published => "published",
        NameState.Blocked => "blocked",
        _ => "pending",
    };
}
