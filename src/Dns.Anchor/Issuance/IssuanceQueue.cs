using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns.Anchor.Acme;

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
/// </remarks>
internal sealed class IssuanceQueue(
    AcmeClient acme,
    IDns01Solver solver,
    ILogger<IssuanceQueue> logger) : BackgroundService
{
    private readonly Channel<IssueRequest> _queue = Channel.CreateUnbounded<IssueRequest>();
    private readonly ConcurrentDictionary<string, byte> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queue one certificate. A name already waiting is left where it is.</summary>
    public bool Enqueue(IssueRequest request)
    {
        if (!_waiting.TryAdd(request.Name, 0))
            return false;

        _queue.Writer.TryWrite(request);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (IssueRequest request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    string chain = await acme.IssueAsync(request.Name, request.Csr, solver, stoppingToken)
                        .ConfigureAwait(false);
                    await request.Deliver(chain, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "no certificate was issued for {Name} ({Member})", request.Name, request.Member);
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
}
