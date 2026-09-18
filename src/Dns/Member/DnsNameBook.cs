using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// The names the DNS anchor last said this member holds.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory. Every node restates itself on startup and the anchor answers with the whole set, so
/// what a restart forgets comes back on the first exchange, and a copy on disk would only be one more
/// thing that can disagree with the anchor.
/// </para>
/// <para>
/// Nothing here is published until the anchor says it is: a name the registry holds but the provider
/// does not yet carry does not resolve, and reporting it as this member's address would send somebody
/// to a name that answers nobody.
/// </para>
/// </remarks>
public sealed class DnsNameBook
{
    private volatile IReadOnlyList<DnsNameEntry> _names = [];

    /// <summary>When the anchor last told this member anything, or null if it never has.</summary>
    public DateTimeOffset? ReceivedAt { get; private set; }

    /// <summary>Every name the anchor last reported, published or not.</summary>
    public IReadOnlyList<DnsNameEntry> All => _names;

    /// <summary>This node's own name, once the provider carries it.</summary>
    public string? NodeName =>
        _names.FirstOrDefault(n => n.Kind == NameKinds.Spell(NameKind.Member) && n.Published)?.Name;

    /// <summary>Replace everything with what the anchor just said.</summary>
    public void Replace(IReadOnlyList<DnsNameEntry> names)
    {
        _names = names;
        ReceivedAt = DateTimeOffset.UtcNow;
    }
}
