using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// Names every game server a node says it has, and releases the names of those it no longer has.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is the whole truth about one node.</b> A node sends it only from a successful read of its
/// engine, so an instance missing from it has been uninstalled. Its release touches that node's names and
/// nobody else's: every name is keyed on the sender the bus authenticated.
/// </para>
/// <para>
/// <b>Only a node has game servers.</b> The sender's kind is read from this member's own roster, never
/// from the message.
/// </para>
/// </remarks>
internal sealed class InstancesHandler(
    ClusterOptions cluster,
    ClusterStateStore state,
    MembersStore members,
    NameService names,
    SqliteNameRegistry registry,
    RecordReconciler reconciler,
    ClusterMembershipWorker worker,
    NamesAnnouncer announcer,
    AnchorOptions options,
    ILogger<InstancesHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Instances;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        if (!await state.IsHolderAsync(DnsCapability.Name, ct).ConfigureAwait(false) || !options.Configured)
            return;

        if (string.Equals(envelope.From, cluster.MemberId, StringComparison.Ordinal))
            return;

        DnsInstances? message = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsInstances);
        if (message?.Instances is null)
        {
            logger.LogWarning("a game server list from {From} could not be read — ignored", envelope.From);
            return;
        }

        MemberRow? sender = await members.GetByMemberIdAsync(envelope.From, ct).ConfigureAwait(false);
        if (sender is null || !string.Equals(sender.Kind, MemberKind.Node, StringComparison.Ordinal))
        {
            logger.LogInformation("{From} is not a node this anchor knows of — it holds no game names", envelope.From);
            return;
        }
        // The same wait every claim makes: against an unread registry a server the zone already names would
        // draw a second name. The node repeats the set, so a zone that cannot be read yet loses nothing.
        try
        {
            await reconciler.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "the zone could not be read — {From}'s game servers are named on its next list", envelope.From);
            return;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (DnsInstance instance in message.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Instance))
                continue;

            keep.Add(instance.Instance);
            NameRow row = names.ClaimGame(envelope.From, instance.Instance, instance.Blueprint ?? "");

            // Kept beside the name for what the ports are read for: which servers on one public address
            // want the same one.
            registry.SetPorts(row.Name, instance.Ports ?? []);
        }

        names.ReleaseGamesExcept(envelope.From, keep);

        worker.RequestZonePass();
        await announcer.AnnounceAsync(envelope.From, ct).ConfigureAwait(false);
    }
}
