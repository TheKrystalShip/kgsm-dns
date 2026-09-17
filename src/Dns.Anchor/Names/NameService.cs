using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Names;

/// <summary>
/// Who holds which name, and what each one points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every target is derived here, never accepted from a caller.</b> A member name points at the
/// address observed for that member, a capability name at the member the cluster assigns it to, and a
/// game name at its owner's member name. A member asking for a name therefore cannot say where it
/// resolves, which is what bounds a compromised member to pointing names at itself.
/// </para>
/// <para>
/// <b>A name is sticky.</b> A member that already holds one keeps it across restarts, address changes
/// and re-claims; it is released only when the member leaves the roster. Certificates, game names and
/// people's bookmarks all name it, so drawing a second one would break all three to no end.
/// </para>
/// </remarks>
public sealed class NameService(
    SqliteNameRegistry registry,
    ClusterBases bases,
    MemberNamePool pool,
    ILogger<NameService> logger)
{
    /// <summary>
    /// The name a member is known by, drawn on first sight and kept from then on.
    /// </summary>
    /// <param name="member">The member id, as the cluster knows it.</param>
    /// <param name="address">The public address observed for it, or empty when none has been.</param>
    /// <returns>The row, whose state says whether it can be published.</returns>
    public NameRow ClaimMember(string member, string address)
    {
        NameRow? existing = registry.FindFor(NameKind.Member, member, key: "");
        if (existing is not null)
            return Retarget(existing, address);

        string label = pool.Draw(member, taken: candidate =>
            registry.Holds(bases.Compose(NameKind.Member, candidate)));

        if (string.IsNullOrEmpty(label))
        {
            logger.LogWarning(
                "no name is free for member {Member} — the configured list is exhausted", member);
            return Blocked(
                bases.Compose(NameKind.Member, "unnamed-" + member),
                NameKind.Member, member, key: "", type: "A",
                note: "every name in the configured list is held");
        }

        var row = new NameRow(
            Name: bases.Compose(NameKind.Member, label),
            Kind: NameKind.Member,
            Member: member,
            Key: "",
            Type: TypeFor(address),
            Content: address,
            State: string.IsNullOrEmpty(address) ? NameState.Blocked : NameState.Pending,
            RecordId: "",
            Note: string.IsNullOrEmpty(address) ? NoAddress : "",
            UpdatedAt: DateTimeOffset.UtcNow);

        registry.Put(row);
        logger.LogInformation("member {Member} is named {Name}", member, row.Name);
        return row;
    }

    /// <summary>
    /// The name a capability is reached at, pointing at whichever member holds it.
    /// </summary>
    /// <remarks>
    /// The label is the capability itself, so the name is the same in every cluster and an operator can
    /// write it down before it exists. It is a <c>CNAME</c> to the holder's member name rather than a
    /// copy of its address: an address change is then one record to move, and a reassignment is the
    /// other.
    /// </remarks>
    public NameRow ClaimCapability(string capability, string holder)
    {
        string label = DnsLabel.Sanitize(capability);
        if (string.IsNullOrEmpty(label))
        {
            return Blocked(
                bases.Compose(NameKind.Capability, "invalid"),
                NameKind.Capability, holder, capability, type: "CNAME",
                note: "the capability name cannot be spelled as a label");
        }

        string name = bases.Compose(NameKind.Capability, label);
        NameRow? holderName = registry.FindFor(NameKind.Member, holder, key: "");

        // The holder's own name has to exist before a capability can point at it. Until it does the row
        // is blocked rather than pointed at an address copied from somewhere, which would then be a
        // second place the holder's address is written.
        if (holderName is null)
        {
            return Save(new NameRow(
                Name: name,
                Kind: NameKind.Capability,
                Member: holder,
                Key: capability,
                Type: "CNAME",
                Content: "",
                State: NameState.Blocked,
                RecordId: registry.Find(name)?.RecordId ?? "",
                Note: "the member holding it has no name yet",
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        return Save(new NameRow(
            Name: name,
            Kind: NameKind.Capability,
            Member: holder,
            Key: capability,
            Type: "CNAME",
            Content: holderName.Name,
            State: NameState.Pending,
            RecordId: registry.Find(name)?.RecordId ?? "",
            Note: "",
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    /// <summary>Point an existing member name at a freshly observed address.</summary>
    /// <remarks>
    /// A row already published at that address is returned untouched, so a member reporting the same
    /// address on every pass writes nothing and the reconcile has nothing to do.
    /// </remarks>
    public NameRow Retarget(NameRow row, string address)
    {
        if (string.Equals(row.Content, address, StringComparison.OrdinalIgnoreCase)
            && (row.State != NameState.Blocked || string.IsNullOrEmpty(address)))
        {
            return row;
        }

        return Save(row with
        {
            Type = TypeFor(address),
            Content = address,
            State = string.IsNullOrEmpty(address) ? NameState.Blocked : NameState.Pending,
            Note = string.IsNullOrEmpty(address) ? NoAddress : "",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Every name one member holds.</summary>
    public IReadOnlyList<NameRow> Held(string member) => registry.ForMember(member);

    /// <summary>
    /// Give up one name. The record goes with it on the next reconcile.
    /// </summary>
    public void Release(string name)
    {
        if (registry.Find(name) is null)
            return;

        registry.Remove(name);
        logger.LogInformation("released {Name}", name);
    }

    /// <summary>
    /// Record a row the anchor cannot act on, so the reason is readable rather than being a name that
    /// never appears.
    /// </summary>
    private NameRow Blocked(string name, NameKind kind, string member, string key, string type, string note) =>
        Save(new NameRow(
            Name: name,
            Kind: kind,
            Member: member,
            Key: key,
            Type: type,
            Content: "",
            State: NameState.Blocked,
            RecordId: "",
            Note: note,
            UpdatedAt: DateTimeOffset.UtcNow));

    private NameRow Save(NameRow row)
    {
        registry.Put(row);
        return row;
    }

    /// <summary>
    /// Which record type an address needs. A colon is the only thing that distinguishes the two
    /// families in a literal, so it is what the choice reads.
    /// </summary>
    private static string TypeFor(string address) =>
        address.Contains(':', StringComparison.Ordinal) ? "AAAA" : "A";

    private const string NoAddress = "no public address has been observed for this member";
}
