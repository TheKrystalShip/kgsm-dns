namespace TheKrystalShip.KGSM.Dns;

/// <summary>
/// The mark a published record carries, saying which cluster name it is and who it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is what separates the cluster's records from everyone else's.</b> A zone holds records an
/// operator made by hand, and a record without this mark is never edited or deleted however much its
/// name looks like one the registry would produce. Ownership is therefore a fact read off the record
/// rather than inferred from the name.
/// </para>
/// <para>
/// <b>It is also the registry's backup.</b> The anchor's own store is the authority while it exists,
/// and every row is recoverable from the marks alone — which is what lets a replacement holder take
/// the capability and converge on what the previous one published, without any state having moved
/// between them.
/// </para>
/// </remarks>
/// <param name="Kind">What the name names.</param>
/// <param name="Member">The member id the name belongs to.</param>
/// <param name="Key">What the name is for within that member: a capability, an instance, or empty for
/// the member's own name.</param>
public readonly record struct RecordMark(NameKind Kind, string Member, string Key)
{
    private const string Prefix = "kgsm";

    /// <summary>The mark as it is written into a record's comment.</summary>
    public override string ToString() =>
        string.Join(':', Prefix, NameKinds.Spell(Kind), Member, Key);

    /// <summary>
    /// Read a mark off a record's comment, or <see langword="null"/> when the comment is not one.
    /// </summary>
    /// <remarks>
    /// A comment carrying anything else — a note somebody left, an empty string, a mark from software
    /// that is not this — parses as nothing, which is the same answer as no comment at all: the record
    /// is not the cluster's.
    /// </remarks>
    public static RecordMark? Parse(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        string[] parts = comment.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return null;

        NameKind? kind = NameKinds.Parse(parts[1]);
        if (kind is null || string.IsNullOrWhiteSpace(parts[2]))
            return null;

        return new RecordMark(kind.Value, parts[2], parts[3]);
    }
}
