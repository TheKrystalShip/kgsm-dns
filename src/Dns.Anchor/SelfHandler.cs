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
/// Records where a member says it is reached, and names it if it is a node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the holder acts.</b> A member standing by drops the statement: two members writing one zone
/// would each reconcile the other's records away. Every member restates itself on a timer and whenever
/// the holder changes, so nothing is lost by dropping it here.
/// </para>
/// <para>
/// <b>Every member's host is kept; only a node is named.</b> Any member can hold a capability, and the
/// capability's name points at the host its holder stated here. The sender's kind is read from this
/// member's own roster, never from the message, so an anchor cannot claim a node name by saying it is a
/// node.
/// </para>
/// <para>
/// <b>The name is keyed on the sender, never on anything in the payload.</b> The bus has already
/// checked that <see cref="ClusterEnvelope.From"/> is the member whose token signed the call, so a
/// member can only ever state where it itself is reached.
/// </para>
/// </remarks>
internal sealed class SelfHandler(
    ClusterOptions cluster,
    ClusterStateStore state,
    MembersStore members,
    NameService names,
    SqliteNameRegistry registry,
    RecordReconciler reconciler,
    ClusterMembershipWorker worker,
    NamesAnnouncer announcer,
    AnchorOptions options,
    ILogger<SelfHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Self;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        if (!await state.IsHolderAsync(DnsCapability.Name, ct).ConfigureAwait(false) || !options.Configured)
            return;

        if (string.Equals(envelope.From, cluster.MemberId, StringComparison.Ordinal))
            return;

        DnsSelf? self = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsSelf);
        if (self is null)
        {
            logger.LogWarning("a statement from {From} could not be read — ignored", envelope.From);
            return;
        }

        MemberRow? sender = await members.GetByMemberIdAsync(envelope.From, ct).ConfigureAwait(false);
        if (sender is null)
        {
            logger.LogInformation("{From} is not a member this anchor knows of — ignored", envelope.From);
            return;
        }

        // A claim waits for the registry to have been read against the zone, for the same reason the
        // worker's own claims do: against an unread registry a node the zone already names would draw a
        // second name. A zone that cannot be read yet leaves the statement for the node's next one.
        try
        {
            await reconciler.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "the zone could not be read — {From} is named on its next statement", envelope.From);
            return;
        }

        // Kept for every member, because any member can be assigned a capability, and its name points
        // wherever the holder said it is reached.
        string? previous = registry.HostOf(envelope.From);
        registry.StateHost(envelope.From, self.PublicHost.Trim());

        if (string.Equals(sender.Kind, MemberKind.Node, StringComparison.Ordinal))
        {
            names.ClaimMember(envelope.From, self.PublicHost);
        }
        else
        {
            // An anchor is reached by the capability it holds, never by a node name.
            foreach (NameRow held in names.Held(envelope.From).Where(r => r.Kind == NameKind.Member))
                names.Release(held.Name);
        }

        if (!string.Equals(previous, self.PublicHost.Trim(), StringComparison.Ordinal))
        {
            logger.LogInformation(
                "{From} is reached at {Host}", envelope.From,
                string.IsNullOrWhiteSpace(self.PublicHost) ? "(no public host)" : self.PublicHost.Trim());
        }

        // The record is written by the next zone pass, brought forward so a member joining does not wait
        // out the whole interval; the member is told where it stands now and again once the pass has run.
        worker.RequestZonePass();
        await announcer.AnnounceAsync(envelope.From, ct).ConfigureAwait(false);
    }
}
