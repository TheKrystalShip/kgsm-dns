using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// The names the DNS anchor last said this member holds.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory. Every member restates itself on startup and the anchor answers with the whole set, so
/// what a restart forgets comes back on the first exchange, and a copy on disk would only be one more
/// thing that can disagree with the anchor.
/// </para>
/// <para>
/// Nothing here is published until the anchor says it is: a name the registry holds but the provider
/// does not yet carry does not resolve, and reporting it as this member's address would send somebody
/// to a name that answers nobody.
/// </para>
/// </remarks>
public sealed class DnsNameBook : Serving.IServedNames
{
    private volatile IReadOnlyList<DnsNameEntry> _names = [];

    /// <summary>
    /// The names this member serves over HTTPS: its node name, the capability names it holds and their
    /// aliases, once published. A game name is played at directly and is never one of them.
    /// </summary>
    public IReadOnlyList<string> Published =>
        [.. _names.Where(n => n.Published && NameKinds.TakesCertificate(n.Kind)).Select(n => n.Name)];

    /// <inheritdoc/>
    public IReadOnlyList<string> Held =>
        [.. _names.Where(n => (n.Published || string.Equals(n.State, "pending", StringComparison.Ordinal))
                && NameKinds.TakesCertificate(n.Kind))
            .Select(n => n.Name)];

    /// <summary>When the anchor last told this member anything, or null if it never has.</summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    /// <inheritdoc/>
    public bool Answered => ReceivedAt is not null;

    /// <summary>Every name the anchor last reported, published or not.</summary>
    public IReadOnlyList<DnsNameEntry> All => _names;

    /// <summary>This member's node name, once the provider carries it; an anchor holds none.</summary>
    public string? NodeName =>
        _names.FirstOrDefault(n => n.Kind == NameKinds.Spell(NameKind.Member) && n.Published)?.Name;

    /// <summary>
    /// The name players reach one game server at, once the provider carries it; null otherwise.
    /// </summary>
    /// <param name="instance">The instance id.</param>
    public string? GameName(string instance) =>
        _names.FirstOrDefault(n => n.Kind == NameKinds.Spell(NameKind.Game)
            && string.Equals(n.Key, instance, StringComparison.Ordinal)
            && n.Published)?.Name;

    /// <summary>
    /// The other members' game servers wanting one of this server's ports on the same public address:
    /// empty when there are none, null when the anchor could not tell or has named no such server.
    /// </summary>
    /// <param name="instance">The instance id.</param>
    public IReadOnlyList<DnsCollision>? GameCollisions(string instance) =>
        _names.FirstOrDefault(n => n.Kind == NameKinds.Spell(NameKind.Game)
            && string.Equals(n.Key, instance, StringComparison.Ordinal))?.Collisions;

    /// <summary>Replace everything with what the anchor just said.</summary>
    public void Replace(IReadOnlyList<DnsNameEntry> names)
    {
        _names = names;
        ReceivedAt = DateTimeOffset.UtcNow;
    }
}
