using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>Where a node's game servers are read from.</summary>
public interface IDnsInstanceSource
{
    /// <summary>
    /// Every game server installed on this node, or <see langword="null"/> when they could not be read.
    /// </summary>
    /// <remarks>
    /// "Could not read" and "none installed" are different answers: the first sends nothing, the second
    /// releases every game name the node holds.
    /// </remarks>
    ValueTask<IReadOnlyList<DnsInstance>?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Keeps the DNS anchor told which game servers this node has.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole set, from a successful read, or nothing.</b> The anchor releases the name of every
/// instance missing from what it is told, so a read that failed is never sent as an empty one. The next
/// successful read says it all again.
/// </para>
/// <para>
/// <b>Level-triggered.</b> The source is read every few seconds and the set is sent when it differs from
/// what was last sent, when the holder of <c>dns</c> changes, and on a slow refresh regardless — so an
/// install made while this process was stopped, a message the bus lost and an anchor that lost its
/// registry all converge without any of them being noticed.
/// </para>
/// </remarks>
public sealed class DnsInstancesWorker(
    ClusterOptions cluster,
    ClusterStateStore state,
    MembersStore members,
    IClusterBus bus,
    IDnsInstanceSource source,
    DnsMemberOptions options,
    ILogger<DnsInstancesWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private string? _sentTo;
    private string? _sentSet;
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A standalone node has no anchor to tell, and its servers keep whatever address they have.
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
                    logger.LogWarning(ex, "could not tell the DNS anchor which game servers this node has");
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

        IReadOnlyList<DnsInstance>? instances = await source.ReadAsync(ct).ConfigureAwait(false);
        if (instances is null)
            return;

        // Ordered, so the same servers read in another order are the same set and send nothing.
        List<DnsInstance> ordered = [.. instances.OrderBy(i => i.Instance, StringComparer.Ordinal)];
        string set = Fingerprint(ordered);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = !string.Equals(_sentTo, holder, StringComparison.Ordinal)
            || !string.Equals(_sentSet, set, StringComparison.Ordinal);

        if (!changed && now < _nextRefresh)
            return;

        await bus.EnqueueAsync(
            DnsMessageTypes.Instances,
            new DnsInstances(ordered),
            DnsJsonContext.Default.DnsInstances,
            [new ClusterTarget(row.MemberId, row.Url)],
            ct).ConfigureAwait(false);

        if (changed)
            logger.LogInformation("told {Holder} this node has {Count} game server(s)", holder, ordered.Count);

        _sentTo = holder;
        _sentSet = set;
        _nextRefresh = now + options.Refresh;
    }

    private static string Fingerprint(IReadOnlyList<DnsInstance> instances) =>
        string.Join('\n', instances.Select(i =>
            i.Instance + " " + i.Blueprint + " "
            + string.Join(',', i.Ports.Select(p => $"{p.Start}-{p.End}/{p.Protocol}"))));
}
