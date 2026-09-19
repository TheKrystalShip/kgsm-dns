using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Dns.Serving;

/// <summary>Where one component's generated site lives, and what it proxies with.</summary>
/// <param name="Component">The component, such as <c>kgsm-api</c>: names the generated file and the
/// proxy rules it includes.</param>
/// <param name="SitesDirectory">Where the generated file is written. The web server includes every file
/// in it.</param>
/// <param name="LocationsFile">The component's own proxy rules, installed with the component. Reviewable
/// content, written by nobody at runtime.</param>
public sealed record NginxSiteOptions(string Component, string SitesDirectory, string LocationsFile)
{
    /// <summary>The generated file.</summary>
    public string SitePath => Path.Combine(SitesDirectory, Component + ".conf");

    /// <summary>Whether the host is provisioned to serve generated sites at all.</summary>
    public bool Usable => Directory.Exists(SitesDirectory) && File.Exists(LocationsFile);
}

/// <summary>
/// The server blocks a member's names are served by, written from a fixed template.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member writes only names and certificate paths.</b> Everything about how a request is handled
/// — the upstream, the timeouts, the body limits — is in the component's locations file, which ships
/// with the component and is included by every block. A generated file can therefore not route
/// anything anywhere the component's own reviewed rules do not.
/// </para>
/// <para>
/// <b>A name is written only once it has a certificate.</b> The web server refuses to load a block whose
/// certificate file is missing, and one refused block takes every other site on the machine down with
/// it. A name waiting for its first certificate is simply not in the file yet.
/// </para>
/// <para>
/// <b>Reloaded, then proved.</b> A reload keeps serving the old configuration if the new one does not
/// load, and says so only in the server's own log. So after a reload the site is checked the way a
/// client sees it: a TLS handshake to this machine naming each name, comparing the certificate served
/// with the one on disk. The proof is also what a renewal is caught by: a new certificate is written to
/// the path the site already names, so nothing about the configuration changes and the web server keeps
/// answering with what it read into memory — a name answering with any other certificate than the one
/// held for it is reloaded and proved again.
/// </para>
/// </remarks>
public sealed class NginxSite(NginxSiteOptions options, CertificateFiles files, ILogger<NginxSite> logger)
{
    private volatile IReadOnlyList<string>? _served;

    /// <summary>Whether the host is provisioned to serve generated sites at all.</summary>
    public bool Usable => options.Usable;

    /// <summary>Whether this component has a generated site on disk, serving names or not.</summary>
    public bool Serving => File.Exists(options.SitePath);

    /// <summary>
    /// The names this component serves: every name the last pass proved answers with its certificate, or,
    /// before any pass has run, every name in the site already on disk that has a current certificate —
    /// the web server has been serving that file since before this process started.
    /// </summary>
    public IReadOnlyList<string> Served => _served ??= OnDisk();

    private IReadOnlyList<string> OnDisk()
    {
        if (!File.Exists(options.SitePath))
            return [];

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return [.. File.ReadLines(options.SitePath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("server_name ", StringComparison.Ordinal))
            .Select(line => line["server_name ".Length..].TrimEnd(';').Trim())
            .Where(name => files.Held(name) is { Present: true } held && held.NotAfter > now)];
    }

    /// <summary>
    /// Serve exactly these names, each with the certificate held for it.
    /// </summary>
    /// <returns>Whether every name is served with the certificate on disk once this returns.</returns>
    public async Task<bool> ServeAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        if (!options.Usable)
            return false;

        List<HeldCertificate> served =
            [.. names.Distinct(StringComparer.OrdinalIgnoreCase).Select(files.Held).Where(h => h.Present)];

        string wanted = Render(served);
        string current = File.Exists(options.SitePath) ? await File.ReadAllTextAsync(options.SitePath, ct).ConfigureAwait(false) : "";
        bool reloaded = false;

        if (!string.Equals(wanted, current, StringComparison.Ordinal))
        {
            string staging = options.SitePath + ".new";
            await File.WriteAllTextAsync(staging, wanted, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(staging, options.SitePath, overwrite: true);

            if (!await ReloadAsync(ct).ConfigureAwait(false))
                return false;

            reloaded = true;
        }

        (List<string> proven, bool stale) = await ProveAsync(served, ct).ConfigureAwait(false);

        // A renewed certificate is written to the path the site already names, so the file the web server
        // loads at startup is the same one and nothing about the configuration changed. It goes on
        // answering with the certificate it read into memory until it is told to read the file again — so
        // a name proved to be served with a certificate other than the one on disk is reloaded and proved
        // again. A name that answers nothing is not reachable from this machine, which a reload does not
        // change.
        if (stale && !reloaded && await ReloadAsync(ct).ConfigureAwait(false))
            (proven, _) = await ProveAsync(served, ct).ConfigureAwait(false);

        _served = proven;
        return proven.Count == served.Count;
    }

    /// <summary>
    /// Check each name the way a client sees it, and report whether any is answered with a certificate
    /// other than the one held for it.
    /// </summary>
    private async Task<(List<string> Proven, bool Stale)> ProveAsync(
        IReadOnlyList<HeldCertificate> served, CancellationToken ct)
    {
        var proven = new List<string>(served.Count);
        bool stale = false;

        foreach (HeldCertificate certificate in served)
        {
            string? live = await ServedFingerprintAsync(certificate.Name, ct).ConfigureAwait(false);
            if (string.Equals(live, certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                proven.Add(certificate.Name);
                continue;
            }

            stale |= live is not null;
            logger.LogWarning(
                "{Name} is not served with the certificate on disk — the web server answers with {Live}",
                certificate.Name, live ?? "nothing");
        }

        return (proven, stale);
    }

    internal string Render(IReadOnlyList<HeldCertificate> served)
    {
        var site = new StringBuilder();
        site.Append("# Generated by ").Append(options.Component)
            .Append(" for the names the cluster's DNS anchor gave it. Rewritten whenever they or their\n")
            .Append("# certificates change; the proxy rules are the component's own, in ")
            .Append(options.LocationsFile).Append(".\n");

        foreach (HeldCertificate certificate in served)
        {
            site.Append('\n')
                .Append("server {\n")
                .Append("    listen      443 ssl;\n")
                .Append("    listen      [::]:443 ssl;\n")
                .Append("    http2       on;\n")
                .Append("    server_name ").Append(certificate.Name).Append(";\n")
                .Append("    ssl_certificate     ").Append(files.ChainPath(certificate.Name)).Append(";\n")
                .Append("    ssl_certificate_key ").Append(files.KeyPath(certificate.Name)).Append(";\n")
                .Append("    include ").Append(options.LocationsFile).Append(";\n")
                .Append("}\n");
        }

        return site.ToString();
    }

    private async Task<bool> ReloadAsync(CancellationToken ct)
    {
        // Through systemd, which asks polkit: the grant setup installs lets this account reload the web
        // server and do nothing else to it.
        var start = new ProcessStartInfo("systemctl", ["--no-ask-password", "reload", "nginx.service"])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using Process process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("the web server could not be reloaded: {Error}", error.Trim());
            return false;
        }

        logger.LogInformation("the web server reloaded {Site}", options.SitePath);

        // A reload returns once the signal is sent; the new workers take a moment to come up.
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The fingerprint of the certificate this machine serves for a name, or null if none is.</summary>
    private static async Task<string?> ServedFingerprintAsync(string name, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await tcp.ConnectAsync("127.0.0.1", 443, timeout.Token).ConfigureAwait(false);

            string? fingerprint = null;
            using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            {
                // Read, never trusted: what is being asked is which certificate is served, not whether
                // this client would accept it.
                if (certificate is not null)
                    fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                return true;
            });

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = name }, timeout.Token)
                .ConfigureAwait(false);
            return fingerprint;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException)
        {
            return null;
        }
    }
}
