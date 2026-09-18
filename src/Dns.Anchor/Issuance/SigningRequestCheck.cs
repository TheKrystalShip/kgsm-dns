using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TheKrystalShip.KGSM.Dns.Anchor.Issuance;

/// <summary>
/// Whether a signing request asks for exactly one name and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The anchor signs nothing itself, but it is what decides which requests reach the issuer, and the
/// issuer certifies whatever names a request carries once the zone proves them. A request slipping a
/// second name in beside the one the member holds would be a certificate for somebody else's name, so
/// the request is read in full and refused whole if it names anything beyond the one it was sent for.
/// </para>
/// <para>
/// The request's own signature is checked on load, so a request that was altered after its key signed
/// it is refused before its contents are read.
/// </para>
/// </remarks>
internal static class SigningRequestCheck
{
    /// <summary>Why a request is refused, or <see langword="null"/> when it asks for exactly <paramref name="name"/>.</summary>
    public static string? Refusal(byte[] der, string name)
    {
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequest(
                der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        }
        catch (CryptographicException ex)
        {
            return "the signing request could not be read: " + ex.Message;
        }

        var dnsNames = new List<string>();
        foreach (X509Extension extension in request.CertificateExtensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                dnsNames.AddRange(san.EnumerateDnsNames());

                // Only DNS names are ever asked for. An address or a URI in the same extension is
                // something the issuer would refuse, and something this anchor has no business asking.
                if (san.EnumerateIPAddresses().Any())
                    return "the signing request names an address";
            }
        }

        if (dnsNames.Count != 1 || !string.Equals(dnsNames[0], name, StringComparison.OrdinalIgnoreCase))
            return $"the signing request names {string.Join(", ", dnsNames)} rather than {name} alone";

        string subject = request.SubjectName.Name;
        if (subject.Length > 0 && !string.Equals(subject, "CN=" + name, StringComparison.OrdinalIgnoreCase))
            return $"the signing request's subject is {subject} rather than {name}";

        return null;
    }
}
