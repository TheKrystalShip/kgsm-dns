using System.Net.Http.Json;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>
/// Asks the DNS anchor for this member's names, rather than waiting to be told them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Telling needs the member reachable; asking does not.</b> A member is reached at the names the anchor
/// gave it, so the anchor's own message reaches it only while its site serves one. A node that has just
/// joined, or one named again after it was removed from the cluster, serves none yet — and would wait to
/// be told what to serve by a message that cannot arrive. A member can always call out, so it asks.
/// </para>
/// <para>
/// <b>A refusal changes nothing.</b> The anchor answers only once it has handled this member's statement;
/// before then, and when it is not reachable, the book is left exactly as it is, so a member racing its
/// own statement never reads "not yet" as "you hold nothing".
/// </para>
/// </remarks>
public sealed class DnsNamesFetcher(
    IHttpClientFactory http,
    IClusterTokenService tokens,
    DnsNameBook book,
    CertificateKeeper keeper,
    ILogger<DnsNamesFetcher> logger)
{
    /// <summary>The named client the fetch is made with.</summary>
    public const string ClientName = "kgsm-dns-names";

    /// <summary>
    /// Ask the anchor at <paramref name="holderUrl"/> for this member's names, and keep the answer.
    /// </summary>
    /// <returns>Whether an answer was kept.</returns>
    public async Task<bool> FetchAsync(string holderUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, holderUrl.TrimEnd('/') + DnsRoutes.Names);
            ClusterCall.Authorize(request, tokens.Mint());

            using HttpResponseMessage response =
                await http.CreateClient(ClientName).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            DnsNames? names = await response.Content
                .ReadFromJsonAsync(DnsJsonContext.Default.DnsNames, ct).ConfigureAwait(false);
            if (names?.Names is null)
                return false;

            DnsNamesHandler.Keep(book, keeper, logger, names);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "could not ask {Holder} for this member's names", holderUrl);
            return false;
        }
    }
}
