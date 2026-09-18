using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// Keeps this anchor's place in the cluster current, and does the zone's work while it holds the
/// capability.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claiming is not holding.</b> The claim is compare-and-set into an empty assignment, so two
/// anchors that both see no holder both succeed locally and the tie resolves when their gossip meets.
/// Every pass therefore re-reads who holds it, and a member that finds itself not the holder stops
/// writing to the zone — which is what keeps a second install a candidate rather than a second writer
/// of one credential's worth of records.
/// </para>
/// <para>
/// <b>A pass is reconciliation, not a reaction.</b> Names are re-derived and the zone re-compared on a
/// timer, so a record edited by hand, a write that failed halfway and a holder that changed mid-write
/// all converge without anyone noticing which of them happened.
/// </para>
/// </remarks>
internal sealed class ClusterMembershipWorker(
    ClusterOptions cluster,
    AnchorOptions options,
    ClusterStateStore state,
    NameService names,
    RecordReconciler reconciler,
    ILogger<ClusterMembershipWorker> logger)
    : ClusterCapabilityWorker(DnsCapability.Name, cluster, state, logger)
{
    private DateTimeOffset _nextZonePass = DateTimeOffset.MinValue;
    private bool _saidUnconfigured;

    protected override async Task OnStandingAsync(
        CapabilityHolding holding, bool changed, CancellationToken ct)
    {
        // Standing by means another member holds the capability and is writing the zone. Two writers
        // would each reconcile the other's records away, so this one does nothing at all.
        if (holding.Standing == CapabilityStanding.StandingBy)
            return;

        if (!options.Configured)
        {
            // Said once rather than every pass: an anchor with no zone or no credential is a normal
            // state to be installed in, and it is the answer to "why is nothing being published".
            if (!_saidUnconfigured)
            {
                _saidUnconfigured = true;
                logger.LogInformation(
                    "no zone and credential are configured — holding '{Capability}' and publishing nothing",
                    DnsCapability.Name);
            }

            return;
        }

        _saidUnconfigured = false;

        // The registry is read against the zone before anything is claimed. A holder whose store was
        // lost would otherwise draw itself a new name and then withdraw the one it is reached at. Until
        // the zone can be read, nothing is claimed and the next pass asks again.
        try
        {
            await reconciler.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "the zone could not be read — claiming nothing until it can be");
            return;
        }

        // This anchor's own name, and the name its capability is reached at. Claimed on every pass
        // because both are derived: the address can move, and the capability can be reassigned to
        // another member, so writing them once would leave whichever moved first behind.
        NameRow self = names.ClaimMember(options.MemberId, options.PublicAddress);
        names.ClaimCapability(DnsCapability.Name, options.MemberId);

        if (self.State == NameState.Blocked && changed)
            logger.LogWarning("this anchor's own name is not published: {Note}", self.Note);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _nextZonePass)
            return;

        _nextZonePass = now + options.Reconcile;

        try
        {
            RecordReconciler.Pass pass = await reconciler.RunAsync(ct).ConfigureAwait(false);

            // A quiet pass is the normal case and says nothing, so the log carries what changed rather
            // than a line per interval that an operator learns to scroll past.
            if (!pass.Quiet)
            {
                logger.LogInformation(
                    "zone pass: {Published} published, {Moved} moved, {Removed} withdrawn, {Recovered} recovered",
                    pass.Published, pass.Moved, pass.Removed, pass.Recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The zone is somebody else's service and it is allowed to be unreachable. Nothing is
            // dropped by a failed pass: the registry still says what the cluster wants, and the next
            // pass writes it.
            logger.LogWarning(ex, "the zone could not be brought to what the registry says");
        }
    }
}
