using System.Collections.Concurrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Dns.Serving;

/// <summary>How a member asks for a certificate: over the bus to the anchor, or, on the anchor, directly.</summary>
public interface ICertificateRequester
{
    /// <summary>Ask for a certificate for one name against a signing request. The answer arrives later.</summary>
    Task RequestAsync(string name, byte[] csrDer, CancellationToken ct);
}

/// <summary>The names this member should be serving, as the anchor last said.</summary>
public interface IServedNames
{
    /// <summary>Every name the anchor reports published for this member.</summary>
    IReadOnlyList<string> Published { get; }
}

/// <summary>
/// Keeps a certificate current for every name this member holds, and the web server serving each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciled, not triggered.</b> Each pass compares the names the anchor says this member holds with
/// the certificates on disk and the site the web server loads, and does whatever closes the gap. A
/// certificate that arrived while nothing was listening, a reload that failed, and a renewal that came
/// due are all the same pass.
/// </para>
/// <para>
/// <b>A request is not repeated while one is outstanding.</b> An order takes the anchor a minute or two,
/// so a name asked for within the last hour is left alone; if the answer never came, the next pass
/// after that asks again.
/// </para>
/// </remarks>
public sealed class CertificateKeeper(
    IServedNames names,
    CertificateFiles files,
    NginxSite site,
    ICertificateRequester requester,
    ILogger<CertificateKeeper> logger) : BackgroundService
{
    private static readonly TimeSpan Pass = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Patience = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0);
    private bool _saidUnprovisioned;

    /// <summary>Run a pass now rather than at the next interval: a certificate or a name just arrived.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    /// <summary>Keep a chain the anchor issued, and serve it.</summary>
    public void Receive(string name, string chainPem)
    {
        if (!files.Usable)
            return;

        if (files.Install(name, chainPem))
        {
            _asked.TryRemove(name, out _);
            logger.LogInformation("a certificate for {Name} is installed, valid until {NotAfter:u}",
                name, files.Held(name).NotAfter);
            Wake();
        }
        else
        {
            logger.LogWarning("a certificate for {Name} was not for this member's key — discarded", name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "certificates could not be brought up to date");
                }

                await _wake.WaitAsync(Pass, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure.
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        IReadOnlyList<string> published = names.Published;
        if (published.Count == 0)
            return;

        // A host nobody provisioned to serve generated sites keeps its names and serves them however
        // it already does. Said once: it is a normal state to be in, and the answer to "why is there no
        // certificate".
        if (!files.Usable || !site.Usable)
        {
            if (!_saidUnprovisioned)
            {
                _saidUnprovisioned = true;
                logger.LogInformation(
                    "this host is not set up to serve generated sites ({Root}) — names are not given certificates here",
                    files.Root);
            }

            return;
        }

        _saidUnprovisioned = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (string name in published)
        {
            if (!files.Held(name).Due(now))
                continue;

            if (_asked.TryGetValue(name, out DateTimeOffset at) && now - at < Patience)
                continue;

            _asked[name] = now;
            await requester.RequestAsync(name, files.SigningRequest(name), ct).ConfigureAwait(false);
            logger.LogInformation("asked for a certificate for {Name}", name);
        }

        await site.ServeAsync(published, ct).ConfigureAwait(false);
    }
}
