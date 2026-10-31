using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns.Anchor.Acme;
using TheKrystalShip.KGSM.Dns.Anchor.Issuance;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class TxtProbeTests
{
    [Fact]
    public void An_answer_carrying_a_compressed_name_yields_its_txt_value()
    {
        byte[] query = TxtProbe.BuildQuery("_acme-challenge.walter.nodes.example.com", out ushort id);

        // The answer a server sends: the question echoed, then one TXT record whose owner is a pointer
        // back to the question's name — the compression every real server uses.
        var answer = new List<byte>(query);
        BinaryPrimitives.WriteUInt16BigEndian(answer.ToArray().AsSpan(), id);
        answer[2] = 0x84; // a response, authoritative
        answer[7] = 1;    // one answer
        answer.AddRange([0xC0, 0x0C]);            // name: pointer to offset 12
        answer.AddRange([0, 16, 0, 1]);           // TXT, IN
        answer.AddRange([0, 0, 0, 60]);           // TTL
        byte[] text = Encoding.ASCII.GetBytes("abc123-value");
        answer.AddRange([0, (byte)(text.Length + 1), (byte)text.Length]);
        answer.AddRange(text);

        IReadOnlyList<string> values = TxtProbe.ParseTxt([.. answer]);

        Assert.Equal(["abc123-value"], values);
    }

    [Fact]
    public void An_answer_with_no_records_yields_nothing()
    {
        byte[] query = TxtProbe.BuildQuery("_acme-challenge.walter.nodes.example.com", out _);

        Assert.Empty(TxtProbe.ParseTxt(query));
    }
}

public sealed class SigningRequestCheckTests
{
    private static byte[] Request(string subject, params string[] names)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in names)
            san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSigningRequest();
    }

    [Fact]
    public void A_request_for_exactly_the_name_is_accepted() =>
        Assert.Null(SigningRequestCheck.Refusal(
            Request("CN=walter.nodes.example.com", "walter.nodes.example.com"), "walter.nodes.example.com"));

    [Fact]
    public void A_request_slipping_in_a_second_name_is_refused_whole()
    {
        // The issuer would certify every name the zone can prove, so a second name would be a
        // certificate for somebody else's name.
        byte[] der = Request("CN=walter.nodes.example.com", "walter.nodes.example.com", "assistant.anchors.example.com");

        Assert.NotNull(SigningRequestCheck.Refusal(der, "walter.nodes.example.com"));
    }

    [Fact]
    public void A_request_for_another_name_is_refused() =>
        Assert.NotNull(SigningRequestCheck.Refusal(
            Request("CN=jessie.nodes.example.com", "jessie.nodes.example.com"), "walter.nodes.example.com"));

    [Fact]
    public void A_request_whose_subject_names_something_else_is_refused() =>
        Assert.NotNull(SigningRequestCheck.Refusal(
            Request("CN=assistant.anchors.example.com", "walter.nodes.example.com"), "walter.nodes.example.com"));

    [Fact]
    public void A_request_altered_after_it_was_signed_is_refused()
    {
        byte[] der = Request("CN=walter.nodes.example.com", "walter.nodes.example.com");
        der[^10] ^= 0xFF;

        Assert.NotNull(SigningRequestCheck.Refusal(der, "walter.nodes.example.com"));
    }
}

public sealed class CertificateFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kgsm-dns-tls-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Stand in for the issuer: certify whatever key a request carries, for its one name.</summary>
    private static string Issue(byte[] csr, string name, DateTimeOffset from, DateTimeOffset until)
    {
        CertificateRequest request = CertificateRequest.LoadSigningRequest(
            csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

        using ECDsa issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerRequest = new CertificateRequest("CN=test issuer", issuerKey, HashAlgorithmName.SHA256);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 issuer = issuerRequest.CreateSelfSigned(from.AddDays(-1), until.AddDays(1));

        using X509Certificate2 leaf = request.Create(issuer, from, until, [1, 2, 3, 4]);
        return leaf.ExportCertificatePem() + "\n" + issuer.ExportCertificatePem() + "\n";
    }

    [Fact]
    public void A_certificate_for_this_members_key_and_name_is_kept()
    {
        var files = new CertificateFiles(_root);
        const string name = "walter.nodes.example.com";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string chain = Issue(files.SigningRequest(name), name, now.AddDays(-1), now.AddDays(89));

        Assert.True(files.Install(name, chain));
        Assert.True(files.Held(name).Present);
        Assert.False(files.Held(name).Due(now));
    }

    [Fact]
    public void The_key_never_leaves_its_owner_and_is_reused_for_a_renewal()
    {
        var files = new CertificateFiles(_root);
        const string name = "walter.nodes.example.com";

        byte[] first = files.SigningRequest(name);
        byte[] second = files.SigningRequest(name);

        static byte[] KeyOf(byte[] csr) => CertificateRequest
            .LoadSigningRequest(csr, HashAlgorithmName.SHA256)
            .PublicKey.ExportSubjectPublicKeyInfo();

        Assert.Equal(KeyOf(first), KeyOf(second));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.KeyPath(name)));
    }

    [Fact]
    public void A_certificate_for_another_key_is_discarded()
    {
        var mine = new CertificateFiles(_root);
        var theirs = new CertificateFiles(Path.Combine(_root, "elsewhere"));
        const string name = "walter.nodes.example.com";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        mine.SigningRequest(name);
        string chain = Issue(theirs.SigningRequest(name), name, now.AddDays(-1), now.AddDays(89));

        Assert.False(mine.Install(name, chain));
        Assert.False(mine.Held(name).Present);
    }

    [Fact]
    public void A_certificate_for_another_name_is_discarded()
    {
        var files = new CertificateFiles(_root);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string chain = Issue(files.SigningRequest("walter.nodes.example.com"), "walter.nodes.example.com",
            now.AddDays(-1), now.AddDays(89));

        Assert.False(files.Install("jessie.nodes.example.com", chain));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(59, false)]
    [InlineData(61, true)]
    [InlineData(95, true)]
    public void Renewal_falls_due_in_the_last_third_of_the_lifetime(int daysIn, bool due)
    {
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-daysIn);
        var held = new HeldCertificate("walter.nodes.example.com", from, from.AddDays(90), "AB");

        Assert.Equal(due, held.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_name_without_a_certificate_is_due() =>
        Assert.True(new HeldCertificate("walter.nodes.example.com", null, null, "").Due(DateTimeOffset.UtcNow));

    [Fact]
    public void RenewsFrom_is_the_start_of_the_last_third_of_the_lifetime()
    {
        DateTimeOffset from = DateTimeOffset.UtcNow;
        DateTimeOffset until = from.AddDays(90);

        Assert.Equal(from.AddDays(60), HeldCertificate.RenewsFrom(from, until));
    }

    [Fact]
    public void Due_and_RenewsFrom_agree_on_the_same_certificate()
    {
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-61);
        DateTimeOffset until = from.AddDays(90);
        var held = new HeldCertificate("walter.nodes.example.com", from, until, "AB");

        Assert.True(held.Due(DateTimeOffset.UtcNow));
        Assert.True(DateTimeOffset.UtcNow >= HeldCertificate.RenewsFrom(from, until));
    }

    [Fact]
    public void The_generated_site_names_only_certificate_paths_and_the_components_own_rules()
    {
        var files = new CertificateFiles(_root);
        var site = new NginxSite(
            new NginxSiteOptions("kgsm-api", _root, "/etc/nginx/kgsm/kgsm-api.locations"),
            files, NullLogger<NginxSite>.Instance);

        string rendered = site.Render([new HeldCertificate("walter.nodes.example.com", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(90), "AB")]);

        Assert.Contains("server_name walter.nodes.example.com;", rendered, StringComparison.Ordinal);
        Assert.Contains("ssl_certificate     " + files.ChainPath("walter.nodes.example.com") + ";", rendered, StringComparison.Ordinal);
        Assert.Contains("include /etc/nginx/kgsm/kgsm-api.locations;", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy_pass", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restarted_member_advertises_the_names_its_site_already_serves_node_name_first()
    {
        var files = new CertificateFiles(_root);
        var site = new NginxSite(
            new NginxSiteOptions("kgsm-api", _root, "/etc/nginx/kgsm/kgsm-api.locations"),
            files, NullLogger<NginxSite>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string capability = "probe.anchors.example.com";
        const string node = "walter.nodes.example.com";
        const string lapsed = "old.anchors.example.com";
        Assert.True(files.Install(capability, Issue(files.SigningRequest(capability), capability, now.AddDays(-1), now.AddDays(89))));
        Assert.True(files.Install(node, Issue(files.SigningRequest(node), node, now.AddDays(-1), now.AddDays(89))));
        Assert.True(files.Install(lapsed, Issue(files.SigningRequest(lapsed), lapsed, now.AddDays(-90), now.AddDays(-1))));

        // The web server has served this file since before the process started; nothing has run a pass
        // yet, and the anchor has not answered.
        File.WriteAllText(Path.Combine(_root, "kgsm-api.conf"),
            site.Render([files.Held(capability), files.Held(node), files.Held(lapsed)]));

        var book = new DnsNameBook();
        book.Replace([
            new DnsNameEntry(node, NameKinds.Spell(NameKind.Member), "", "published", ""),
            new DnsNameEntry(capability, NameKinds.Spell(NameKind.Capability), "probe", "published", ""),
        ]);

        // An expired certificate answers no handshake anybody accepts, so its name is not an address.
        Assert.Equal(
            ["https://" + node, "https://" + capability],
            new DnsSelfAddresses(book, site).Addresses);
    }
}
