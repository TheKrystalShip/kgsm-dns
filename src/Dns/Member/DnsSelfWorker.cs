using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>What a member tells the DNS anchor about itself, and how often.</summary>
/// <param name="PublicHost">Where the member is reached from the internet — normally its network's
/// dynamic-DNS name. Empty is sent as it is: the member keeps its names, and nothing is published for
/// them.</param>
/// <param name="Refresh">How often the statement is repeated when nothing has changed.</param>
public sealed record DnsMemberOptions(string PublicHost, TimeSpan Refresh);

/// <summary>
/// Keeps the DNS anchor told where this member is reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member states it, node or anchor.</b> A node is named from it, and a capability's name
/// points at wherever its holder said it is reached — so an anchor that never states its host holds a
/// capability whose name resolves nowhere.
/// </para>
/// <para>
/// <b>A statement, repeated, rather than a change announced once.</b> The whole of what a member says is
/// one host, so it says all of it every time: when the anchor holding the capability changes, when the
/// host changes, when this member's incarnation rises, and on a slow refresh regardless. A rise is how a
/// member refutes the cluster taking it for gone — removed, or dead — and the anchor may have released
/// its names meanwhile; they come back from this statement, not from the next refresh. A message the bus lost, an anchor that lost its
/// registry and a holder that was reassigned all converge on the next statement, and none of them has
/// to be noticed to be healed.
/// </para>
/// <para>
/// <b>It also asks for the answer.</b> The anchor tells a member its names over the bus, which reaches the
/// member only through a name its site already serves — so a member serving none would never hear. The
/// worker fetches its names from the holder (<see cref="DnsNamesFetcher"/>) for a minute after each
/// statement, while the anchor has not answered, and while a name waits for its record.
/// </para>
/// <para>
/// <b>The host is trusted as stated.</b> Checking it against the address the member calls from is not
/// reliable enough to refuse on — a member calling over IPv6 while its dynamic-DNS name carries only an
/// IPv4 address, or one whose outbound traffic leaves through a different link than inbound arrives on,
/// would be refused with a correct configuration. Getting the host right is the deployer's part.
/// </para>
/// </remarks>
public sealed class DnsSelfWorker(
    ClusterOptions cluster,
    ClusterStateStore state,
    MembersStore members,
    SelfIncarnation incarnation,
    IClusterBus bus,
    DnsNameBook book,
    DnsNamesFetcher fetcher,
    DnsMemberOptions options,
    ILogger<DnsSelfWorker> logger) : BackgroundService
{
    // Long enough for the anchor to handle a statement and write the record it produces.
    private static readonly TimeSpan FetchAfterStating = TimeSpan.FromMinutes(1);

    private string? _sentTo;
    private string? _sentHost;
    private long _sentAt = -1;
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _fetchUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFetch = DateTimeOffset.MinValue;

    /// <summary>
    /// How often the holder is re-read. Matched to the gossip cadence, because that is what a
    /// reassignment arrives on.
    /// </summary>
    private TimeSpan Interval =>
        TimeSpan.FromMilliseconds(cluster.GossipMs > 0 ? cluster.GossipMs : 5000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A standalone member has no anchor to tell. It keeps whatever address it is configured with and
        // this component does nothing at all.
        if (!cluster.Enabled)
            return;

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await StateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "could not tell the DNS anchor where this member is reached");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure.
        }
    }

    internal async Task StateAsync(CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(holder) || string.Equals(holder, cluster.MemberId, StringComparison.Ordinal))
            return;

        MemberRow? row = await members.GetByMemberIdAsync(holder, ct).ConfigureAwait(false);
        if (row is null || !row.Enabled || string.IsNullOrWhiteSpace(row.Url))
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long current = incarnation.Current;
        bool changed = !string.Equals(_sentTo, holder, StringComparison.Ordinal)
            || !string.Equals(_sentHost, options.PublicHost, StringComparison.Ordinal);

        if (changed || current != _sentAt || now >= _nextRefresh)
        {
            await bus.EnqueueAsync(
                DnsMessageTypes.Self,
                new DnsSelf(options.PublicHost),
                DnsJsonContext.Default.DnsSelf,
                [new ClusterTarget(row.MemberId, row.Url)],
                ct).ConfigureAwait(false);

            if (changed)
            {
                logger.LogInformation(
                    "told {Holder} this member is reached at {Host}",
                    holder, string.IsNullOrEmpty(options.PublicHost) ? "(no public host)" : options.PublicHost);
            }

            _sentTo = holder;
            _sentHost = options.PublicHost;
            _sentAt = current;
            _nextRefresh = now + options.Refresh;
            _fetchUntil = now + FetchAfterStating;
        }

        // Asked for as well as told: the anchor's answer reaches this member only through a name it
        // already serves. Asked every tick while a statement is settling, while the anchor has not
        // answered, and while a name is waiting for its record; otherwise on the slow refresh.
        bool settling = now < _fetchUntil || !book.Answered || book.Held.Count != book.Published.Count;
        if ((settling || now >= _nextFetch) && await fetcher.FetchAsync(row.Url, ct).ConfigureAwait(false))
            _nextFetch = now + options.Refresh;
    }
}
