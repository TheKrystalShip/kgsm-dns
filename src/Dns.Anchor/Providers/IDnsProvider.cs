using TheKrystalShip.KGSM.Dns;

namespace TheKrystalShip.KGSM.Dns.Anchor.Providers;

/// <summary>One record as the provider holds it.</summary>
/// <param name="Id">The provider's own handle for the record, needed to change or remove it.</param>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Type">`A`, `AAAA`, `CNAME` or `TXT`.</param>
/// <param name="Content">The address, target or text, depending on the type.</param>
/// <param name="Ttl">Seconds. Zero means the provider's automatic value.</param>
/// <param name="Proxied">Whether the provider answers for this name itself instead of publishing the
/// content. A proxy carries web traffic only, so a proxied game name silently stops being playable.</param>
/// <param name="Comment">The record's comment, which is where a <see cref="RecordMark"/> lives.</param>
public sealed record ProviderRecord(
    string Id,
    string Name,
    string Type,
    string Content,
    int Ttl,
    bool Proxied,
    string Comment)
{
    /// <summary>The mark this record carries, or <see langword="null"/> when it carries none.</summary>
    public RecordMark? Mark => RecordMark.Parse(Comment);
}

/// <summary>What the anchor asks a record to be.</summary>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Type">`A`, `AAAA`, `CNAME` or `TXT`.</param>
/// <param name="Content">The address, target or text.</param>
/// <param name="Ttl">Seconds.</param>
/// <param name="Comment">The mark, or a bare note for a record that outlives no registry row.</param>
public sealed record DesiredRecord(string Name, string Type, string Content, int Ttl, string Comment);

/// <summary>
/// The zone, as the one member holding a credential for it can act on.
/// </summary>
/// <remarks>
/// <para>
/// Narrow on purpose: list, create, update, delete, and nothing about zones, accounts or anything else
/// a token can reach. What the anchor may do to a zone is then bounded by what this interface can
/// express, which is a smaller thing to reason about than what the credential can do.
/// </para>
/// <para>
/// An implementation never decides what to write. Containment, ownership and targets are settled
/// before a call arrives here.
/// </para>
/// </remarks>
public interface IDnsProvider
{
    /// <summary>Whether this provider is configured enough to be used at all.</summary>
    bool Configured { get; }

    /// <summary>Every record in the zone, including the ones this cluster did not make.</summary>
    /// <remarks>
    /// The full listing rather than a filtered one: a name the cluster wants may already be held by a
    /// record it does not own, and that is exactly the case a filtered listing would hide.
    /// </remarks>
    Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>Create one record, returning it as the provider now holds it.</summary>
    Task<ProviderRecord> CreateAsync(DesiredRecord record, CancellationToken ct = default);

    /// <summary>Replace the content, TTL and comment of one record the provider already holds.</summary>
    Task<ProviderRecord> UpdateAsync(string id, DesiredRecord record, CancellationToken ct = default);

    /// <summary>Remove one record.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
