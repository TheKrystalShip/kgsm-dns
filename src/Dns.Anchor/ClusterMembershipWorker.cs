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
    CapabilityNames capabilities,
    Departures departures,
    PortCollisions collisions,
    RecordReconciler reconciler,
    SqliteNameRegistry registry,
    NamesAnnouncer announcer,
    ILogger<ClusterMembershipWorker> logger)
    : ClusterCapabilityWorker(DnsCapability.Name, cluster, state, logger)
{
    private DateTimeOffset _nextZonePass = DateTimeOffset.MinValue;
    private bool _saidUnconfigured;

    /// <summary>
    /// Bring the next zone pass forward to the next tick, for a claim that should not wait out the
    /// whole interval — a node that has just said where it is reached.
    /// </summary>
    public void RequestZonePass() => _nextZonePass = DateTimeOffset.MinValue;

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

        // Every capability's name, this one's included, derived again from the assignments. Claimed on
        // every tick because a capability can be reassigned and a holder's host can change, and writing
        // them once would leave whichever moved first behind. A tick that moved any of them brings the
        // zone pass forward, so a reassignment reaches the zone in seconds rather than an interval.
        //
        // Game names follow their node's own name the same way: a node whose name was drawn, blocked or
        // published since its servers were named has them brought to match.
        //
        // A member removed from the cluster goes first, so a capability still assigned to it is re-derived
        // on this same tick as moving to a holder that has not spoken, and withdrawn.
        bool departed = (await departures.SweepAsync(options.MemberId, ct).ConfigureAwait(false)).Count > 0;
        var touched = new HashSet<string>(
            await capabilities.SyncAsync(options.MemberId, options.PublicHost, ct).ConfigureAwait(false),
            StringComparer.Ordinal);
        touched.UnionWith(names.RetargetGames());
        if (touched.Count > 0 || departed)
            RequestZonePass();

        // An anchor holds no node name — it is reached by what it serves — so one found under this
        // member's id is released: it would be a second name for a machine that already has one through
        // its node.
        foreach (NameRow held in names.Held(options.MemberId).Where(r => r.Kind == NameKind.Member))
            names.Release(held.Name);

        if (changed
            && names.Held(options.MemberId).FirstOrDefault(r => r.Key == DnsCapability.Name) is
                { State: NameState.Blocked } self)
        {
            logger.LogWarning("{Name} is not published: {Note}", self.Name, self.Note);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _nextZonePass)
        {
            foreach (string member in touched)
                await announcer.AnnounceAsync(member, ct).ConfigureAwait(false);
            return;
        }

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

            // A pass that changed where any name stands is followed by telling each member holding one,
            // and each that has said where it is reached, so a member learns it is published once it is
            // rather than on its next statement, and a member that lost a capability learns it lost the
            // name with it.
            //
            // Port collisions are checked on the same cadence — hosts are resolved again each time, since a
            // dynamic-DNS name moves with the address behind it — and a member whose servers' collisions
            // changed is told as well.
            IReadOnlySet<string> collided = await collisions.RefreshAsync(ct).ConfigureAwait(false);
            IEnumerable<string> told = touched.Concat(collided);
            if (!pass.Quiet || pass.Contested > 0)
                told = told.Concat(registry.All().Select(r => r.Member)).Concat(registry.StatedMembers());

            foreach (string member in told
                         .Where(m => !string.Equals(m, options.MemberId, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                await announcer.AnnounceAsync(member, ct).ConfigureAwait(false);
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
