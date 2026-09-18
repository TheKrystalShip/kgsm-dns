using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// Names a node that has said where it is reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the holder acts.</b> A member standing by drops the statement: two members writing one zone
/// would each reconcile the other's records away. The node restates itself on a timer and whenever the
/// holder changes, so nothing is lost by dropping it here.
/// </para>
/// <para>
/// <b>Only a node is named.</b> The sender's kind is read from this member's own roster, never from
/// the message, so an anchor cannot claim a node name by saying it is a node.
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
        if (sender is null || !string.Equals(sender.Kind, MemberKind.Node, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "{From} is not a node this member knows of — it holds no node name", envelope.From);
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

        names.ClaimMember(envelope.From, self.PublicHost);

        // The record is written by the next zone pass, brought forward so a node joining does not wait out
        // the whole interval; the node is told where it stands now and again once the pass has run.
        worker.RequestZonePass();
        await announcer.AnnounceAsync(envelope.From, ct).ConfigureAwait(false);
    }
}
