using TheKrystalShip.KGSM.Dns;

namespace TheKrystalShip.KGSM.Dns.Anchor.Registry;

/// <summary>Where a name stands.</summary>
public enum NameState
{
    /// <summary>The registry holds the name; the provider does not hold the record yet.</summary>
    Pending,

    /// <summary>The provider holds the record, as the registry describes it.</summary>
    Published,

    /// <summary>
    /// The name cannot be published as things stand, and the reason is recorded rather than retried
    /// blindly — a member with no public address, or a label whose whole numbered series is held.
    /// </summary>
    Blocked,
}

/// <summary>
/// One name the cluster has claimed.
/// </summary>
/// <remarks>
/// <para>
/// The row is the desired state and the record at the provider is reconciled to it, so a hand edit at
/// the provider is drift that heals rather than a change that wins. The one thing the provider is
/// authoritative for is <see cref="RecordId"/>, its own handle for the record.
/// </para>
/// <para>
/// <see cref="Content"/> is empty while the target is unknown — a member whose public address nobody
/// has observed yet. Such a row stays <see cref="NameState.Blocked"/> and no record is written,
/// because a name pointing at a guess is worse than a name that does not resolve.
/// </para>
/// </remarks>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Kind">What it names.</param>
/// <param name="Member">The member the name belongs to.</param>
/// <param name="Key">The capability or instance within that member, empty for a member's own name.</param>
/// <param name="Type">`A`, `AAAA` or `CNAME`.</param>
/// <param name="Content">The address or target, or empty while it is unknown.</param>
/// <param name="State">Where the name stands.</param>
/// <param name="RecordId">The provider's handle, empty until the record exists.</param>
/// <param name="Note">Why a blocked row is blocked, in one line. Empty otherwise.</param>
/// <param name="UpdatedAt">When this row was last written.</param>
public sealed record NameRow(
    string Name,
    NameKind Kind,
    string Member,
    string Key,
    string Type,
    string Content,
    NameState State,
    string RecordId,
    string Note,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The mark the record carries, derived from the row rather than stored beside it.</summary>
    public RecordMark Mark => new(Kind, Member, Key);
}
