using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>
/// Releases every name a member holds once it has been removed from the cluster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Removed is a roster state, not an absence.</b> Removing a member marks it <c>left</c> one incarnation
/// above anything it said, and every member holds that row for days before dropping it, so an anchor that
/// was down when the removal was made still learns it from a peer. A member that is merely offline goes
/// suspect, then dead, and is reaped too — so a row missing from the roster says nothing about which of
/// the two happened, and only <c>left</c> is read as a departure. Offline is not departed: a dead member
/// keeps every name it holds.
/// </para>
/// <para>
/// <b>Acted on once it has settled.</b> A member that is still running refutes <c>left</c> by raising its
/// own incarnation, and the mesh overturns the removal. Waiting a settle window before releasing keeps a
/// refuted removal from withdrawing names the member goes on serving. A refutation slower than the window
/// costs nothing lasting: the member states itself again as soon as its incarnation rises.
/// </para>
/// <para>
/// <b>Acted on once per removal.</b> The removal acted on is recorded by the incarnation its row carried,
/// and a row still saying the same thing is not acted on again — on a later tick or after a restart. A
/// member that comes back states itself, which only a running member can do, and is named again at once,
/// whether or not its refutation has reached this anchor yet; releasing again on every tick would take
/// those names straight back. Removing it again raises the incarnation, and that removal is acted on.
/// </para>
/// <para>
/// <b>Everything goes with the member.</b> Its node name, its game names, any capability name still
/// following it, and the host it stated — so a capability left assigned to it reads as moving to a holder
/// that has not spoken, and its name is withdrawn rather than left pointing at a machine that is gone.
/// </para>
/// </remarks>
public sealed class Departures(
    NameService names,
    SqliteNameRegistry registry,
    MembersStore members,
    ClusterOptions cluster,
    ILogger<Departures> logger)
{
    // A running node was measured taking just over a minute to refute its removal. Releasing before a
    // refutation arrives costs that member its names until it next states itself, so the window is set
    // well past it.
    private static readonly TimeSpan LongestSettle = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a member stays <c>left</c> before its names are released: long enough for a running member
    /// to refute the removal, and at most half the window a departed row is held for, so the row is still
    /// there when it is read.
    /// </summary>
    public TimeSpan Settle
    {
        get
        {
            TimeSpan half = TimeSpan.FromMilliseconds(Math.Max(cluster.ReapMs, cluster.LeftReapMs) / 2.0);
            return half < LongestSettle ? half : LongestSettle;
        }
    }

    /// <summary>Release the names of every member this anchor's roster says has departed.</summary>
    /// <inheritdoc cref="Sweep"/>
    public async Task<IReadOnlySet<string>> SweepAsync(string self, CancellationToken ct) =>
        Sweep(await members.ListAsync(ct).ConfigureAwait(false), self, DateTimeOffset.UtcNow);

    /// <summary>
    /// Release the names of every member in <paramref name="roster"/> removed for the settle window whose
    /// removal has not been acted on.
    /// </summary>
    /// <param name="roster">This anchor's copy of the roster.</param>
    /// <param name="self">This anchor's own member id, which is never released here.</param>
    /// <param name="now">The time the settle window is measured against.</param>
    /// <returns>The departed members that held a name or a stated host.</returns>
    public IReadOnlySet<string> Sweep(IReadOnlyList<MemberRow> roster, string self, DateTimeOffset now)
    {
        var departed = new HashSet<string>(StringComparer.Ordinal);
        foreach (MemberRow row in roster)
        {
            if (!Departed(row, now) || string.Equals(row.MemberId, self, StringComparison.Ordinal))
                continue;

            if (registry.DepartureActedOn(row.MemberId) == row.Incarnation)
                continue;

            IReadOnlyList<string> released = names.ReleaseAll(row.MemberId);
            bool forgot = registry.ForgetHost(row.MemberId);
            registry.ActOnDeparture(row.MemberId, row.Incarnation);
            if (released.Count == 0 && !forgot)
                continue;

            departed.Add(row.MemberId);
            logger.LogInformation(
                "{Member} left the cluster — released {Count} name(s) and the host it stated",
                row.MemberId, released.Count);
        }

        return departed;
    }

    private bool Departed(MemberRow row, DateTimeOffset now) =>
        string.Equals(row.MembershipState, GossipState.Left, StringComparison.Ordinal)
        && row.StateChangedAt is { } since && now - since >= Settle;
}
