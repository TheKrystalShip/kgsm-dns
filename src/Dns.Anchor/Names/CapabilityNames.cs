using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>
/// Keeps one name under the anchors base for every capability the cluster assigns, pointing at whoever
/// holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the assignments, every pass.</b> Nothing tells the anchor a capability moved; the
/// cluster's own state converges by gossip, and this re-reads it. A reassignment, a holder that changed
/// its host and a capability deliberately held by nobody all land the same way, without anyone noticing
/// which of them happened.
/// </para>
/// <para>
/// <b>A holder is reached where it said it is.</b> Every member states its public host to the anchor, the
/// same statement a node is named on, and the anchor states its own from configuration. A holder that has
/// not said yet leaves a name it already held where it was, and a name moving to it is withdrawn until it
/// does — a name pointing at the previous holder would send people to a member that no longer serves it.
/// </para>
/// </remarks>
public sealed class CapabilityNames(NameService names, SqliteNameRegistry registry, ClusterStateStore state)
{
    /// <summary>
    /// Bring every capability name to the assignments this member currently holds a copy of.
    /// </summary>
    /// <inheritdoc cref="Sync"/>
    public async Task<IReadOnlySet<string>> SyncAsync(string self, string selfHost, CancellationToken ct) =>
        Sync(await state.ListAsync(ct).ConfigureAwait(false), self, selfHost);

    /// <summary>
    /// Bring every capability name to what the assignments say.
    /// </summary>
    /// <param name="assignments">The cluster's assignments, as this member holds them.</param>
    /// <param name="self">This anchor's own member id.</param>
    /// <param name="selfHost">Where this anchor is reached, from its configuration.</param>
    /// <returns>The members whose names changed, including one that lost a name, so each can be told.</returns>
    public IReadOnlySet<string> Sync(IReadOnlyList<ClusterAssignment> assignments, string self, string selfHost)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, NameRow> before = registry.OfKind(NameKind.Capability)
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClusterAssignment assignment in assignments.Where(a => a.IsHeld))
        {
            held.Add(assignment.Capability);

            string? host = string.Equals(assignment.MemberId, self, StringComparison.Ordinal)
                ? selfHost
                : registry.HostOf(assignment.MemberId);

            NameRow after = names.ClaimCapability(assignment.Capability, assignment.MemberId, host);

            if (!before.TryGetValue(assignment.Capability, out NameRow? previous))
            {
                touched.Add(after.Member);
                continue;
            }

            if (previous != after)
                touched.Add(after.Member);
            if (!string.Equals(previous.Member, after.Member, StringComparison.Ordinal))
                touched.Add(previous.Member);
        }

        // A capability nobody holds has nobody to point at. Its name goes rather than resolving to the
        // member that last held it.
        foreach (NameRow orphan in before.Values.Where(r => !held.Contains(r.Key)))
        {
            names.Release(orphan.Name);
            touched.Add(orphan.Member);
        }

        touched.Remove(self);
        return touched;
    }
}
