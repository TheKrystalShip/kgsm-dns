using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>
/// Who holds which name, and what each one points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name points at where its holder is reached, and nowhere else.</b> A node's name and a
/// capability's name both resolve to the public host of the member they belong to — normally that
/// member's dynamic-DNS name, which already follows a home connection's changing address. A member
/// cannot ask for a name that resolves somewhere other than itself, which is what bounds a compromised
/// member to pointing names at itself.
/// </para>
/// <para>
/// <b>Only nodes are named under the nodes base.</b> A machine commonly runs a node and several
/// anchors, each a member of its own. Anchors are reached by the capability they hold, so a machine
/// carries one node name however many anchors run beside it, and a capability's name moves with the
/// capability rather than with whichever machine held it first.
/// </para>
/// <para>
/// <b>A name is sticky.</b> A node that already holds one keeps it across restarts, host changes and
/// re-claims; it is released only when the member leaves the roster. Certificates, game names and
/// people's bookmarks all name it, so drawing a second one would break all three to no end.
/// </para>
/// </remarks>
public sealed class NameService(
    SqliteNameRegistry registry,
    ClusterBases bases,
    MemberNamePool pool,
    ILogger<NameService> logger)
{
    // Claims arrive from the inbox and from the worker at once. A draw reads what is held and then
    // writes, so two unserialized claims could each see a label free and the second would overwrite the
    // first's row — two nodes on one name. Every claim and release holds this for its read and its write.
    private readonly Lock _gate = new();

    /// <summary>
    /// The name a node is known by, drawn on first sight and kept from then on.
    /// </summary>
    /// <param name="member">The member id, as the cluster knows it.</param>
    /// <param name="publicHost">Where it is reached: a hostname, an address, or empty when unknown.</param>
    /// <returns>The row, whose state says whether it can be published.</returns>
    public NameRow ClaimMember(string member, string publicHost)
    {
        lock (_gate)
            return ClaimMemberLocked(member, publicHost);
    }

    private NameRow ClaimMemberLocked(string member, string publicHost)
    {
        NameRow? existing = registry.FindFor(NameKind.Member, member, key: "");
        if (existing is not null)
            return Retarget(existing, publicHost);

        string label = pool.Draw(member, taken: candidate =>
            registry.Holds(bases.Compose(NameKind.Member, candidate)));

        if (string.IsNullOrEmpty(label))
        {
            logger.LogWarning(
                "no name is free for member {Member} — the configured list is exhausted", member);
            return Save(Row(
                bases.Compose(NameKind.Member, "unnamed-" + DnsLabel.Sanitize(member)),
                NameKind.Member, member, key: "", publicHost: "",
                blockedBecause: "every name in the configured list is held"));
        }

        NameRow row = Save(Row(
            bases.Compose(NameKind.Member, label), NameKind.Member, member, key: "", publicHost));

        logger.LogInformation("member {Member} is named {Name}", member, row.Name);
        return row;
    }

    /// <summary>
    /// The name a capability is reached at, pointing at wherever its holder is.
    /// </summary>
    /// <remarks>
    /// The label is the capability itself, so the name is the same in every cluster and an operator can
    /// write it down before it exists. It points at the holder's public host directly, so a
    /// reassignment is one record to move and an address change at the holder is none.
    /// </remarks>
    /// <param name="capability">The capability, as the cluster's assignment names it.</param>
    /// <param name="holder">The member the cluster assigns it to.</param>
    /// <param name="holderHost">Where that member is reached.</param>
    public NameRow ClaimCapability(string capability, string holder, string holderHost)
    {
        lock (_gate)
            return ClaimCapabilityLocked(capability, holder, holderHost);
    }

    private NameRow ClaimCapabilityLocked(string capability, string holder, string holderHost)
    {
        string label = DnsLabel.Sanitize(capability);
        if (string.IsNullOrEmpty(label))
        {
            return Save(Row(
                bases.Compose(NameKind.Capability, "invalid"),
                NameKind.Capability, holder, capability, publicHost: "",
                blockedBecause: "the capability name cannot be spelled as a label"));
        }

        string name = bases.Compose(NameKind.Capability, label);
        NameRow? existing = registry.Find(name);

        // The same holder at the same host is the steady state, and re-writing the row on every pass
        // would make a quiet cluster look busy in the registry's timestamps.
        if (existing is not null && existing.Member == holder)
            return Retarget(existing, holderHost);

        NameRow row = Save(Row(name, NameKind.Capability, holder, capability, holderHost) with
        {
            RecordId = existing?.RecordId ?? "",
        });

        logger.LogInformation("{Name} follows {Holder}", name, holder);
        return row;
    }

    /// <summary>Point an existing name at a freshly stated host.</summary>
    /// <remarks>
    /// A row already carrying that host is returned untouched, so a member stating the same host on
    /// every pass writes nothing and the reconcile has nothing to do.
    /// </remarks>
    public NameRow Retarget(NameRow row, string publicHost)
    {
        NameRow wanted = Row(row.Name, row.Kind, row.Member, row.Key, publicHost) with
        {
            RecordId = row.RecordId,
        };

        bool same = string.Equals(wanted.Type, row.Type, StringComparison.Ordinal)
            && string.Equals(wanted.Content, row.Content, StringComparison.Ordinal)
            && (wanted.State == NameState.Blocked) == (row.State == NameState.Blocked);

        return same ? row : Save(wanted);
    }

    /// <summary>Every name one member holds.</summary>
    public IReadOnlyList<NameRow> Held(string member) => registry.ForMember(member);

    /// <summary>
    /// Give up one name. The record goes with it on the next reconcile.
    /// </summary>
    public void Release(string name)
    {
        lock (_gate)
        {
            if (registry.Find(name) is null)
                return;

            registry.Remove(name);
        }

        logger.LogInformation("released {Name}", name);
    }

    /// <summary>
    /// The row a name should be, given where its holder is reached.
    /// </summary>
    /// <remarks>
    /// A host that cannot be published — none known, not public, one of this cluster's own names — is
    /// a blocked row carrying the reason, rather than a record pointing at a guess.
    /// </remarks>
    private NameRow Row(
        string name, NameKind kind, string member, string key, string publicHost, string? blockedBecause = null)
    {
        bool usable = PublicHost.TryRead(publicHost, bases, out PublicHost host, out string reason);
        string note = blockedBecause ?? (usable ? "" : reason);
        bool blocked = blockedBecause is not null || !usable;

        return new NameRow(
            Name: name,
            Kind: kind,
            Member: member,
            Key: key,
            Type: blocked ? "CNAME" : host.Type,
            Content: blocked ? "" : host.Content,
            State: blocked ? NameState.Blocked : NameState.Pending,
            RecordId: "",
            Note: note,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private NameRow Save(NameRow row)
    {
        registry.Put(row);
        return row;
    }
}
