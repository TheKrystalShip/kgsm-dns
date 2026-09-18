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
/// host changes, and on a slow refresh regardless. A message the bus lost, an anchor that lost its
/// registry and a holder that was reassigned all converge on the next statement, and none of them has
/// to be noticed to be healed.
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
    IClusterBus bus,
    DnsMemberOptions options,
    ILogger<DnsSelfWorker> logger) : BackgroundService
{
    private string? _sentTo;
    private string? _sentHost;
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;

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

    private async Task StateAsync(CancellationToken ct)
    {
        string? holder = await state.HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(holder) || string.Equals(holder, cluster.MemberId, StringComparison.Ordinal))
            return;

        MemberRow? row = await members.GetByMemberIdAsync(holder, ct).ConfigureAwait(false);
        if (row is null || !row.Enabled || string.IsNullOrWhiteSpace(row.Url))
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = !string.Equals(_sentTo, holder, StringComparison.Ordinal)
            || !string.Equals(_sentHost, options.PublicHost, StringComparison.Ordinal);

        if (!changed && now < _nextRefresh)
            return;

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
        _nextRefresh = now + options.Refresh;
    }
}
