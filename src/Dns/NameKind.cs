namespace TheKrystalShip.KGSM.Dns;

/// <summary>What a published name names.</summary>
/// <remarks>
/// The kind picks the base a name lives under, and each base is a separate containment boundary: the
/// anchor writes inside its three bases and nowhere else, so a record that is not the cluster's — the
/// zone apex, mail, anything an operator made by hand — is outside every kind and cannot be reached
/// by any message a member sends.
/// </remarks>
public enum NameKind
{
    /// <summary>A member, reached by a browser and by other members. Under the nodes base.</summary>
    Member,

    /// <summary>A capability, pointing at whichever member holds it. Under the anchors base.</summary>
    Capability,

    /// <summary>A game server, played at directly. Under the play base.</summary>
    Game,
}

/// <summary>The wire spelling of a <see cref="NameKind"/>, used in a record's mark.</summary>
public static class NameKinds
{
    /// <summary>The spelling stored in a record mark and in the registry.</summary>
    public static string Spell(NameKind kind) => kind switch
    {
        NameKind.Member => "member",
        NameKind.Capability => "capability",
        NameKind.Game => "game",
        _ => "member",
    };

    /// <summary>Read a spelling back. Anything unrecognized is no kind at all.</summary>
    public static NameKind? Parse(string? spelling) => spelling switch
    {
        "member" => NameKind.Member,
        "capability" => NameKind.Capability,
        "game" => NameKind.Game,
        _ => null,
    };
}
