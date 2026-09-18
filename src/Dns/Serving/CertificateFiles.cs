using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TheKrystalShip.KGSM.Dns.Serving;

/// <summary>What a member holds for one name: whether it has a certificate, and how much life it has left.</summary>
/// <param name="Name">The name.</param>
/// <param name="NotBefore">When the certificate became valid, or null when there is none.</param>
/// <param name="NotAfter">When it expires, or null when there is none.</param>
/// <param name="Fingerprint">The leaf's SHA-256 fingerprint, hex, or empty when there is none.</param>
public sealed record HeldCertificate(string Name, DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, string Fingerprint)
{
    /// <summary>Whether there is a certificate at all.</summary>
    public bool Present => NotAfter is not null;

    /// <summary>
    /// Whether it is due for renewal: absent, expired, or into the last third of its lifetime.
    /// </summary>
    /// <remarks>
    /// A fraction rather than a number of days, because issuers shorten lifetimes and a fraction needs
    /// nothing revisited when they do.
    /// </remarks>
    public bool Due(DateTimeOffset now)
    {
        if (NotBefore is not { } from || NotAfter is not { } until)
            return true;

        TimeSpan lifetime = until - from;
        return now >= until - lifetime / 3;
    }
}

/// <summary>
/// One member's keys and certificates, one directory per name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is made here and stays here.</b> A signing request carries only the public half, and what
/// comes back is a certificate, which is public too. Nothing secret crosses the bus.
/// </para>
/// <para>
/// A key is readable by its owner alone. The web server reads it as root when it loads its
/// configuration, which is the only other reader it has.
/// </para>
/// </remarks>
public sealed class CertificateFiles(string root)
{
    /// <summary>The directory everything lives under.</summary>
    public string Root { get; } = root;

    /// <summary>Where one name's key lives.</summary>
    public string KeyPath(string name) => Path.Combine(Root, name, "privkey.pem");

    /// <summary>Where one name's chain lives.</summary>
    public string ChainPath(string name) => Path.Combine(Root, name, "fullchain.pem");

    /// <summary>Whether this member can keep certificates here at all.</summary>
    public bool Usable => Directory.Exists(Root);

    /// <summary>
    /// A signing request for one name, made with that name's key — generated on first use and reused
    /// after, so a renewal is a new certificate for the key the member already has.
    /// </summary>
    public byte[] SigningRequest(string name)
    {
        using ECDsa key = LoadOrCreateKey(name);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSigningRequest();
    }

    /// <summary>
    /// Keep a chain the anchor issued, after checking it is for this name and for this member's key.
    /// </summary>
    /// <returns>Whether it was kept. A chain for another key would be a certificate nothing here can use.</returns>
    public bool Install(string name, string chainPem)
    {
        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(chainPem);
        using ECDsa key = LoadOrCreateKey(name);

        using ECDsa? certified = leaf.GetECDsaPublicKey();
        if (certified is null
            || !certified.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
        {
            return false;
        }

        if (!leaf.MatchesHostname(name, allowWildcards: false))
            return false;

        WritePrivate(ChainPath(name), chainPem, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return true;
    }

    /// <summary>What is held for one name right now.</summary>
    public HeldCertificate Held(string name)
    {
        string path = ChainPath(name);
        if (!File.Exists(path) || !File.Exists(KeyPath(name)))
            return new HeldCertificate(name, null, null, "");

        try
        {
            using X509Certificate2 leaf = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return new HeldCertificate(
                name,
                new DateTimeOffset(leaf.NotBefore.ToUniversalTime()),
                new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
                leaf.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (CryptographicException)
        {
            // A chain that does not parse is no certificate: it is replaced, not served.
            return new HeldCertificate(name, null, null, "");
        }
    }

    private ECDsa LoadOrCreateKey(string name)
    {
        string path = KeyPath(name);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }

        WritePrivate(path, key.ExportPkcs8PrivateKeyPem(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    /// <summary>
    /// Write a file whole, with its mode set before any content is in it, and swap it into place so a
    /// reader never sees half of one.
    /// </summary>
    private static void WritePrivate(string path, string content, UnixFileMode mode)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        string staging = path + ".new";
        File.Delete(staging);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = mode;

        using (var stream = new FileStream(staging, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
        }

        File.Move(staging, path, overwrite: true);
    }
}
