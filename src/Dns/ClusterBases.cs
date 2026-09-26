namespace TheKrystalShip.KGSM.Dns;

/// <summary>
/// The three bases a cluster's names live under, inside one zone, and the one label at its top an
/// administrator may name a capability by.
/// </summary>
/// <remarks>
/// <para>
/// Separate bases are what make containment checkable rather than assumed. Every name the anchor
/// writes sits under one of them, so "is this record the cluster's?" is a string test on the name and
/// not a judgement about who might have made it. A zone holds plenty the cluster has no business
/// touching — the apex, mail, a panel address an operator points where they like — and all of it is
/// outside every base.
/// </para>
/// <para>
/// A cluster needs one domain and no more: the bases are labels inside a zone the operator already
/// owns, not zones of their own.
/// </para>
/// </remarks>
/// <param name="Zone">The DNS zone, such as <c>example.com</c>.</param>
/// <param name="Nodes">The label members are named under, such as <c>nodes</c>.</param>
/// <param name="Anchors">The label capabilities are named under, such as <c>anchors</c>.</param>
/// <param name="Play">The label game servers are named under, such as <c>play</c>.</param>
public sealed record ClusterBases(string Zone, string Nodes, string Anchors, string Play)
{
    /// <summary>The fully qualified base for one kind.</summary>
    public string BaseOf(NameKind kind) => kind switch
    {
        NameKind.Member => Nodes + "." + Zone,
        NameKind.Capability => Anchors + "." + Zone,
        NameKind.Game => Play + "." + Zone,
        NameKind.Alias => Play + "." + Zone,
        NameKind.CapabilityAlias => Zone,
        _ => Zone,
    };

    /// <summary>The fully qualified name one label takes under one kind's base.</summary>
    public string Compose(NameKind kind, string label) => label + "." + BaseOf(kind);

    /// <summary>
    /// The label inside <paramref name="name"/>, when the name is one label under this kind's base.
    /// </summary>
    /// <remarks>
    /// A name deeper than one label under a base is not this cluster's, even though it ends with the
    /// base: nothing publishes such a name, so a record carrying one was made by something else and
    /// stays untouched.
    /// </remarks>
    public string? LabelOf(NameKind kind, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        string suffix = "." + BaseOf(kind);
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        string label = name[..^suffix.Length];
        return label.Contains('.', StringComparison.Ordinal) ? null : label;
    }

    /// <summary>Whether a name sits directly under any of the three bases.</summary>
    /// <remarks>
    /// The top of the zone is not a base: it holds the operator's own names — the panel, a machine's
    /// hand-made name — which a member may even state as where it is reached. A capability alias lives
    /// there only by an administrator's word, and is recognised by <see cref="HoldsAlias"/> instead.
    /// </remarks>
    public bool Owns(string? name) =>
        LabelOf(NameKind.Member, name) is not null
        || LabelOf(NameKind.Capability, name) is not null
        || LabelOf(NameKind.Game, name) is not null
        || LabelOf(NameKind.Alias, name) is not null;

    /// <summary>
    /// Whether <paramref name="name"/> is a capability alias this cluster could have written: one label
    /// directly under the zone, which is not one of the three bases themselves.
    /// </summary>
    /// <remarks>
    /// A shape test only. What makes such a record the cluster's is its mark, because the operator's own
    /// names share the shape; this is what keeps a mark from reaching anywhere deeper or elsewhere.
    /// </remarks>
    public bool HoldsAlias(string? name) =>
        LabelOf(NameKind.CapabilityAlias, name) is { } label && !IsBaseLabel(label);

    /// <summary>Whether a label names one of the three bases.</summary>
    public bool IsBaseLabel(string label) =>
        string.Equals(label, Nodes, StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, Anchors, StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, Play, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The challenge name an ACME order validates a name through.
    /// </summary>
    /// <remarks>
    /// It sits one label deeper than the name being validated, so it is outside every base by the rule
    /// above. Containment is therefore checked against the name a certificate is for, before the
    /// challenge record derived from it is written.
    /// </remarks>
    public static string ChallengeFor(string name) => "_acme-challenge." + name;
}
