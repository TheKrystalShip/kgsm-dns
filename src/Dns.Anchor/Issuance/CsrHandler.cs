using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor.Issuance;

/// <summary>
/// Takes a member's signing request for one of its names and queues the certificate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a name the sender holds, published.</b> The registry row for the name must belong to the
/// member the bus authenticated and must be carried by the zone — a name still pending cannot be
/// validated, and a name held by somebody else is somebody else's certificate.
/// </para>
/// <para>
/// <b>Only that name.</b> The request itself is read and refused if it names anything else, because the
/// issuer certifies every name in a request the zone can prove.
/// </para>
/// </remarks>
internal sealed class CsrHandler(
    ClusterStateStore state,
    MembersStore members,
    SqliteNameRegistry registry,
    IssuanceQueue queue,
    IClusterBus bus,
    AnchorOptions options,
    Status.DnsJournal journal,
    ILogger<CsrHandler> logger) : IClusterMessageHandler
{
    public string Type => DnsMessageTypes.Csr;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        if (!await state.IsHolderAsync(DnsCapability.Name, ct).ConfigureAwait(false) || !options.Configured)
            return;

        DnsCsr? request = DnsJsonContext.Read(envelope.Payload, DnsJsonContext.Default.DnsCsr);
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            logger.LogWarning("a signing request from {From} could not be read — ignored", envelope.From);
            return;
        }

        NameRow? row = registry.Find(request.Name);
        if (row is null
            || !string.Equals(row.Member, envelope.From, StringComparison.Ordinal)
            || row.State != NameState.Published
            || row.Kind == NameKind.Game)
        {
            logger.LogWarning(
                "{From} asked for a certificate for {Name}, which it does not hold published — refused",
                envelope.From, request.Name);
            await journal.RefusedAsync(request.Name, envelope.From,
                "the name is not one this member holds published", ct).ConfigureAwait(false);
            return;
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(request.Csr);
        }
        catch (FormatException)
        {
            logger.LogWarning("the signing request from {From} is not base64 — ignored", envelope.From);
            return;
        }

        if (SigningRequestCheck.Refusal(der, request.Name) is { } refusal)
        {
            logger.LogWarning("the signing request from {From} is refused: {Refusal}", envelope.From, refusal);
            await journal.RefusedAsync(request.Name, envelope.From, refusal, ct).ConfigureAwait(false);
            return;
        }

        string member = envelope.From;
        bool queued = queue.Enqueue(new IssueRequest(request.Name, member, der, async (chain, token) =>
        {
            MemberRow? target = await members.GetByMemberIdAsync(member, token).ConfigureAwait(false);
            if (target is null || string.IsNullOrWhiteSpace(target.Url))
                return;

            await bus.EnqueueAsync(
                DnsMessageTypes.Cert,
                new DnsCert(request.Name, chain),
                DnsJsonContext.Default.DnsCert,
                [new ClusterTarget(target.MemberId, target.Url)],
                token).ConfigureAwait(false);
        }));

        if (queued)
            logger.LogInformation("queued a certificate for {Name} ({Member})", request.Name, member);
    }
}
