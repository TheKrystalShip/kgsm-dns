using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns.Anchor.Acme;
using TheKrystalShip.KGSM.Dns.Anchor.Status;
using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Issuance;

/// <summary>One certificate to issue, and where the chain goes once it exists.</summary>
/// <param name="Name">The name it is for.</param>
/// <param name="Member">The member it is for.</param>
/// <param name="Csr">The signing request, DER.</param>
/// <param name="Deliver">Hands the PEM chain to the member.</param>
internal sealed record IssueRequest(string Name, string Member, byte[] Csr, Func<string, CancellationToken, Task> Deliver);

/// <summary>
/// Certificates waiting to be issued, worked one at a time off the request path.
/// </summary>
/// <remarks>
/// <para>
/// An order takes the issuer a minute or two: the challenge record has to reach every nameserver of the
/// zone before the issuer looks. Run inside the inbox handler that received the request, it would outlast
/// the sender's delivery timeout and be sent again, so a request is queued and acknowledged at once, and
/// the chain goes back as a message of its own.
/// </para>
/// <para>
/// A name already waiting or being issued is not queued twice. The member asks again an hour later if
/// nothing came back, which is what covers an order that failed.
/// </para>
/// <para>
/// Every order that ends is recorded in <see cref="CertificateOrders"/> and the journal. What is waiting
/// or in flight is reported by <see cref="Pending"/> and held only here.
/// </para>
/// </remarks>
internal sealed class IssuanceQueue(
    AcmeClient acme,
    IDns01Solver solver,
    CertificateOrders orders,
    DnsJournal journal,
    ILogger<IssuanceQueue> logger) : BackgroundService
{
    private readonly Channel<IssueRequest> _queue = Channel.CreateUnbounded<IssueRequest>();
    private readonly ConcurrentDictionary<string, DnsCertificateOrder> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queue one certificate. A name already waiting is left where it is.</summary>
    public bool Enqueue(IssueRequest request)
    {
        var order = new DnsCertificateOrder(
            request.Name, request.Member, "queued", DateTimeOffset.UtcNow, FinishedAt: null, NotAfter: null,
            RenewsFrom: null, Failure: null);
        if (!_waiting.TryAdd(request.Name, order))
            return false;

        _queue.Writer.TryWrite(request);
        return true;
    }

    /// <summary>Every order waiting or in flight, in name order.</summary>
    public IReadOnlyList<DnsCertificateOrder> Pending() =>
        [.. _waiting.Values.OrderBy(o => o.Name, StringComparer.Ordinal)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (IssueRequest request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                DateTimeOffset requestedAt = _waiting.TryGetValue(request.Name, out DnsCertificateOrder? waiting)
                    ? waiting.RequestedAt
                    : DateTimeOffset.UtcNow;
                _waiting[request.Name] = new DnsCertificateOrder(
                    request.Name, request.Member, "issuing", requestedAt, null, null, null, null);

                try
                {
                    string? chain = await IssueAsync(request, requestedAt, stoppingToken).ConfigureAwait(false);
                    if (chain is not null)
                        await request.Deliver(chain, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Issued and recorded, and not handed over. The member asks again once its patience
                    // runs out, and that order is the one it receives.
                    logger.LogWarning(ex, "the certificate for {Name} could not be sent to {Member}",
                        request.Name, request.Member);
                }
                finally
                {
                    _waiting.TryRemove(request.Name, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. What was queued is asked for again by its member.
        }
    }

    /// <summary>Order one certificate and record how the order ended.</summary>
    /// <returns>The PEM chain, or null when none was issued.</returns>
    private async Task<string?> IssueAsync(IssueRequest request, DateTimeOffset requestedAt, CancellationToken ct)
    {
        string chain;
        try
        {
            chain = await acme.IssueAsync(request.Name, request.Csr, solver, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "no certificate was issued for {Name} ({Member})", request.Name, request.Member);
            orders.Failed(request.Name, request.Member, requestedAt, ex.Message);
            await journal.FailedAsync(request.Name, request.Member, ex.Message, ct).ConfigureAwait(false);
            return null;
        }

        // Recorded before delivery: the certificate exists and counts against the issuer's limit whether or
        // not the member receives it.
        (DateTimeOffset? notBefore, DateTimeOffset? notAfter) = Validity(chain);
        orders.Issued(request.Name, request.Member, requestedAt, notBefore, notAfter);
        await journal.IssuedAsync(request.Name, request.Member, notAfter, ct).ConfigureAwait(false);
        return chain;
    }

    /// <summary>The validity of the leaf certificate at the head of a PEM chain, or nulls when it cannot be read.</summary>
    private static (DateTimeOffset? NotBefore, DateTimeOffset? NotAfter) Validity(string chainPem)
    {
        try
        {
            using X509Certificate2 leaf = X509Certificate2.CreateFromPem(chainPem);
            return (new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return (null, null);
        }
    }
}
