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
    /// <param name="holderHost">Where that member is reached, or <see langword="null"/> when it has not
    /// said. A name already following that holder keeps pointing where it does, because not having heard
    /// is not having been told otherwise; a name moving to a holder that has not said is withdrawn rather
    /// than left pointing at the previous one.</param>
    public NameRow ClaimCapability(string capability, string holder, string? holderHost)
    {
        lock (_gate)
            return ClaimCapabilityLocked(capability, holder, holderHost);
    }

    private NameRow ClaimCapabilityLocked(string capability, string holder, string? holderHost)
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
            return holderHost is null ? existing : Retarget(existing, holderHost);

        NameRow wanted = holderHost is null
            ? Row(name, NameKind.Capability, holder, capability, publicHost: "",
                blockedBecause: "the holder has not said where it is reached")
            : Row(name, NameKind.Capability, holder, capability, holderHost);

        NameRow row = Save(wanted with { RecordId = existing?.RecordId ?? "" });

        logger.LogInformation("{Name} follows {Holder}", name, holder);
        return row;
    }

    /// <summary>
    /// The name players reach one game server at, drawn on first sight and kept from then on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The label is the game — the blueprint reduced to a label — with the lowest free numbered suffix
    /// when another server holds it, so the second Factorio in a cluster is <c>factorio-2</c> whichever
    /// node it is on. A name is keyed on the node and the instance, so a node restating its servers keeps
    /// every name it already has.
    /// </para>
    /// <para>
    /// The record is an alias of the node's own name, so a node's address change moves one record and
    /// none of its game names. A node with no name, or one that cannot be published, has game names that
    /// are blocked rather than pointing at a guess.
    /// </para>
    /// </remarks>
    /// <param name="member">The node the server is on.</param>
    /// <param name="instance">The instance id.</param>
    /// <param name="blueprint">The game it runs.</param>
    public NameRow ClaimGame(string member, string instance, string blueprint)
    {
        lock (_gate)
        {
            NameRow? existing = registry.FindFor(NameKind.Game, member, instance);
            if (existing is not null)
                return RetargetGameLocked(existing);

            string label = DnsLabel.FirstFree(DnsLabel.Sanitize(blueprint), candidate =>
                registry.Holds(bases.Compose(NameKind.Game, candidate)));

            if (string.IsNullOrEmpty(label))
            {
                string fallback = DnsLabel.Sanitize(instance);
                return Save(PlayRow(
                    bases.Compose(NameKind.Game, "unnamed-" + (fallback.Length > 0 ? fallback : "instance")),
                    NameKind.Game, member, instance,
                    blockedBecause: "no label is free for this game — every numbered name in its series is held"));
            }

            NameRow row = Save(PlayRow(bases.Compose(NameKind.Game, label), NameKind.Game, member, instance));
            logger.LogInformation("{Instance} on {Member} is named {Name}", instance, member, row.Name);
            return row;
        }
    }

    /// <summary>
    /// Name an existing game server under an additional label, in the play base beside it. The record is
    /// an alias of the same node the game name is, so it follows the node exactly as the game name does.
    /// </summary>
    /// <param name="name">The alias's own fully qualified name, already composed and validated.</param>
    /// <param name="member">The node the aliased server is on.</param>
    /// <param name="instance">The aliased server's instance id.</param>
    /// <returns>The row, or <see langword="null"/> when the name is already held by something.</returns>
    public NameRow? ClaimAlias(string name, string member, string instance)
    {
        lock (_gate)
        {
            if (registry.Find(name) is not null)
                return null;

            NameRow row = Save(PlayRow(name, NameKind.Alias, member, instance));
            logger.LogInformation("{Name} is an alias of {Instance} on {Member}", name, instance, member);
            return row;
        }
    }

    /// <summary>
    /// Name a capability under an additional label directly under the zone. The record points where the
    /// capability's own name does, at its holder's public host, and follows the capability to whichever
    /// member holds it next.
    /// </summary>
    /// <remarks>
    /// The alias outlives any one holder: it is the administrator's name for the capability, not for the
    /// member serving it now, so a capability nobody holds leaves it blocked rather than released.
    /// </remarks>
    /// <param name="name">The alias's own fully qualified name, already composed and validated.</param>
    /// <param name="capability">The capability it names.</param>
    /// <returns>The row, or <see langword="null"/> when the name is already held by something.</returns>
    public NameRow? ClaimCapabilityAlias(string name, string capability)
    {
        lock (_gate)
        {
            if (registry.Find(name) is not null)
                return null;

            NameRow row = Save(CapabilityAliasRow(name, capability, previousMember: ""));
            logger.LogInformation("{Name} is an alias of the {Capability} capability", name, capability);
            return row;
        }
    }

    /// <summary>
    /// Bring every capability alias to where its capability's own name stands: the same holder, the same
    /// target, blocked for the same reason.
    /// </summary>
    /// <returns>The members whose aliases changed, including one an alias moved away from.</returns>
    public IReadOnlySet<string> RetargetCapabilityAliases()
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (NameRow row in registry.OfKind(NameKind.CapabilityAlias))
            {
                NameRow wanted = CapabilityAliasRow(row.Name, row.Key, row.Member) with { RecordId = row.RecordId };

                bool same = string.Equals(wanted.Member, row.Member, StringComparison.Ordinal)
                    && string.Equals(wanted.Type, row.Type, StringComparison.Ordinal)
                    && string.Equals(wanted.Content, row.Content, StringComparison.Ordinal)
                    && (wanted.State == NameState.Blocked) == (row.State == NameState.Blocked);
                if (same)
                    continue;

                Save(wanted);
                if (row.Member.Length > 0)
                    touched.Add(row.Member);
                if (wanted.Member.Length > 0)
                    touched.Add(wanted.Member);
            }
        }

        return touched;
    }

    /// <summary>
    /// The row a capability alias should be, given where the capability's own name stands.
    /// </summary>
    /// <remarks>
    /// The target is copied from the capability's row rather than pointing at the capability's name, so
    /// the alias resolves straight to the holder and the anchors base appears nowhere in what a person
    /// looks up. A capability nobody holds keeps the alias with the member that last served it, blocked,
    /// so its record is withdrawn and the member is told it no longer serves the name.
    /// </remarks>
    private NameRow CapabilityAliasRow(string name, string capability, string previousMember)
    {
        NameRow? held = registry.OfKind(NameKind.Capability)
            .FirstOrDefault(r => string.Equals(r.Key, capability, StringComparison.Ordinal));

        string? reason = held is null ? "nobody holds the " + capability + " capability"
            : held.State == NameState.Blocked ? "the capability's own name is not published: " + held.Note
            : string.IsNullOrEmpty(held.Content) ? "the capability's own name points nowhere yet"
            : null;

        return new NameRow(
            Name: name,
            Kind: NameKind.CapabilityAlias,
            Member: held?.Member ?? previousMember,
            Key: capability,
            Type: reason is null ? held!.Type : "CNAME",
            Content: reason is null ? held!.Content : "",
            State: reason is null ? NameState.Pending : NameState.Blocked,
            RecordId: "",
            Note: reason ?? "",
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Give up every game name and alias a node holds for an instance it no longer has.
    /// </summary>
    /// <param name="member">The node.</param>
    /// <param name="keep">The instances it still has.</param>
    /// <returns>The names released.</returns>
    public IReadOnlyList<string> ReleaseGamesExcept(string member, IReadOnlySet<string> keep)
    {
        var released = new List<string>();
        lock (_gate)
        {
            foreach (NameRow row in registry.ForMember(member)
                         .Where(r => (r.Kind == NameKind.Game || r.Kind == NameKind.Alias) && !keep.Contains(r.Key)))
            {
                registry.Remove(row.Name);
                released.Add(row.Name);
            }
        }

        foreach (string name in released)
            logger.LogInformation("released {Name}", name);

        return released;
    }

    /// <summary>
    /// Bring every game name and alias to where its node's own name stands, for a node whose name was
    /// drawn, blocked or published since.
    /// </summary>
    /// <returns>The members whose game names or aliases changed.</returns>
    public IReadOnlySet<string> RetargetGames()
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (NameRow row in registry.OfKind(NameKind.Game).Concat(registry.OfKind(NameKind.Alias)))
            {
                if (!ReferenceEquals(RetargetGameLocked(row), row))
                    touched.Add(row.Member);
            }
        }

        return touched;
    }

    private NameRow RetargetGameLocked(NameRow row)
    {
        // A name that could not be drawn stays blocked for its own reason; only the node's standing moves.
        if (row.Name.StartsWith("unnamed-", StringComparison.Ordinal))
            return row;

        NameRow wanted = PlayRow(row.Name, row.Kind, row.Member, row.Key) with { RecordId = row.RecordId };

        bool same = string.Equals(wanted.Content, row.Content, StringComparison.Ordinal)
            && (wanted.State == NameState.Blocked) == (row.State == NameState.Blocked);

        return same ? row : Save(wanted);
    }

    /// <summary>
    /// The row a game name or an alias of one should be, given where its node's own name stands.
    /// </summary>
    /// <remarks>
    /// A node name still waiting for its record is usable: the row resolves as soon as the node's record
    /// exists, and blocking on it would withdraw every game name and alias on a node each time its host
    /// moved.
    /// </remarks>
    private NameRow PlayRow(string name, NameKind kind, string member, string instance, string? blockedBecause = null)
    {
        NameRow? node = registry.FindFor(NameKind.Member, member, key: "");
        string? reason = blockedBecause
            ?? (node is null ? "the node has no name of its own"
                : node.State == NameState.Blocked ? "the node's own name is not published: " + node.Note
                : null);

        return new NameRow(
            Name: name,
            Kind: kind,
            Member: member,
            Key: instance,
            Type: "CNAME",
            Content: reason is null ? node!.Name : "",
            State: reason is null ? NameState.Pending : NameState.Blocked,
            RecordId: "",
            Note: reason ?? "",
            UpdatedAt: DateTimeOffset.UtcNow);
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
    /// Give up every name one member holds — its node name, its game names and any capability name
    /// following it. The records go with them on the next reconcile. A capability alias is the
    /// administrator's name for the capability rather than for this member, so it stays, and follows the
    /// capability on the next pass.
    /// </summary>
    /// <returns>The names released.</returns>
    public IReadOnlyList<string> ReleaseAll(string member)
    {
        var released = new List<string>();
        lock (_gate)
        {
            foreach (NameRow row in registry.ForMember(member).Where(r => r.Kind != NameKind.CapabilityAlias))
            {
                registry.Remove(row.Name);
                released.Add(row.Name);
            }
        }

        foreach (string name in released)
            logger.LogInformation("released {Name}", name);

        return released;
    }

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
