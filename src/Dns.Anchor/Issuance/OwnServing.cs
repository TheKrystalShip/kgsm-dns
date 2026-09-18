using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Anchor.Issuance;

/// <summary>
/// The names this anchor serves itself: the capability names it holds, once published.
/// </summary>
/// <remarks>
/// Read straight from the registry. The anchor is the one member that needs nobody to tell it its names,
/// and asking itself over the bus would be a round trip through its own inbox for a row it already has.
/// </remarks>
internal sealed class OwnNames(SqliteNameRegistry registry, AnchorOptions options) : IServedNames
{
    public IReadOnlyList<string> Published =>
        [.. registry.ForMember(options.MemberId)
            .Where(r => r.Kind == NameKind.Capability && r.State == NameState.Published)
            .Select(r => r.Name)];
}

/// <summary>
/// Issues this anchor's own certificates directly, with no bus between the request and the issuer.
/// </summary>
/// <remarks>
/// The chain is handed to the keeper through a lookup at delivery time rather than a constructor
/// reference, because the keeper is built with this requester and a cycle would never resolve.
/// </remarks>
internal sealed class OwnCertificateRequester(IssuanceQueue queue, AnchorOptions options, IServiceProvider services)
    : ICertificateRequester
{
    public Task RequestAsync(string name, byte[] csrDer, CancellationToken ct)
    {
        queue.Enqueue(new IssueRequest(name, options.MemberId, csrDer, (chain, _) =>
        {
            services.GetRequiredService<CertificateKeeper>().Receive(name, chain);
            return Task.CompletedTask;
        }));

        return Task.CompletedTask;
    }
}
