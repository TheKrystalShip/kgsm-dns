using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Dns.Anchor.Acme;

/// <summary>An issuer refused, and the message is what it said.</summary>
public sealed class AcmeException(string message) : Exception(message);

/// <summary>
/// Places the challenge record an issuer validates a name through, and takes it away again.
/// </summary>
public interface IDns01Solver
{
    /// <summary>
    /// Publish <paramref name="value"/> as a TXT record at <c>_acme-challenge.</c> of
    /// <paramref name="name"/>, and return once the zone's own servers answer with it.
    /// </summary>
    Task PresentAsync(string name, string value, CancellationToken ct);

    /// <summary>Remove what <see cref="PresentAsync"/> published for <paramref name="name"/>.</summary>
    Task CleanupAsync(string name, CancellationToken ct);
}

/// <summary>
/// The ACME order flow (RFC 8555) for one name at a time, answered by DNS-01.
/// </summary>
/// <remarks>
/// <para>
/// Written against the protocol rather than a library because the anchor is Native AOT, and the flow
/// is small: an account, an order, one challenge, a finalize, a download. Every request is a JWS
/// signed with the account key, carrying a nonce the issuer handed out on the previous response.
/// </para>
/// <para>
/// <b>DNS-01 because it never contacts the member.</b> Validation reads a TXT record in the zone, so a
/// node with no certificate, no open port 80 and no name serving anything yet can still be issued one —
/// which is what lets a node's first certificate exist before anything about it is reachable.
/// </para>
/// </remarks>
public sealed class AcmeClient(
    HttpClient http,
    string directoryUrl,
    string contact,
    string accountKeyPath,
    ILogger<AcmeClient> logger) : IDisposable
{
    private AcmeAccountKey? _key;
    private AcmeDirectory? _directory;
    private string? _nonce;
    private string? _kid;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Issue a certificate for one name against a signing request, returning the PEM chain.
    /// </summary>
    /// <remarks>
    /// One order at a time per holder. Orders are few — one per name per renewal window — and
    /// serializing them keeps the nonce chain and the challenge records in the zone simple to reason
    /// about.
    /// </remarks>
    public async Task<string> IssueAsync(string name, byte[] csrDer, IDns01Solver solver, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAccountAsync(ct).ConfigureAwait(false);

            var newOrder = new AcmeNewOrder { Identifiers = [new AcmeIdentifier { Value = name }] };
            (AcmeOrder order, string orderUrl) = await PostAsync(
                _directory!.NewOrder, newOrder, AcmeJsonContext.Default.AcmeNewOrder,
                AcmeJsonContext.Default.AcmeOrder, ct).ConfigureAwait(false);

            if (order.Status == "pending")
            {
                foreach (string authorizationUrl in order.Authorizations)
                    await AuthorizeAsync(name, authorizationUrl, solver, ct).ConfigureAwait(false);
            }

            order = await WaitForAsync(orderUrl, AcmeJsonContext.Default.AcmeOrder,
                o => o.Status is "ready" or "valid" or "invalid", ct).ConfigureAwait(false);
            if (order.Status == "invalid")
                throw new AcmeException($"the order for {name} is invalid: {order.Error?.Detail}");

            if (order.Status == "ready")
            {
                var finalize = new AcmeFinalize { Csr = AcmeAccountKey.Base64Url(csrDer) };
                await PostAsync(order.Finalize, finalize, AcmeJsonContext.Default.AcmeFinalize,
                    AcmeJsonContext.Default.AcmeOrder, ct).ConfigureAwait(false);

                order = await WaitForAsync(orderUrl, AcmeJsonContext.Default.AcmeOrder,
                    o => o.Status is "valid" or "invalid", ct).ConfigureAwait(false);
            }

            if (order.Status != "valid" || string.IsNullOrEmpty(order.Certificate))
                throw new AcmeException($"the order for {name} ended {order.Status}: {order.Error?.Detail}");

            using HttpResponseMessage download = await SendSignedAsync(order.Certificate, "", ct)
                .ConfigureAwait(false);
            string chain = await download.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            logger.LogInformation("issued a certificate for {Name}", name);
            return chain;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AuthorizeAsync(string name, string authorizationUrl, IDns01Solver solver, CancellationToken ct)
    {
        AcmeAuthorization authorization = await GetAsync(
            authorizationUrl, AcmeJsonContext.Default.AcmeAuthorization, ct).ConfigureAwait(false);

        if (authorization.Status == "valid")
            return;

        AcmeChallenge challenge = authorization.Challenges.FirstOrDefault(c => c.Type == "dns-01")
            ?? throw new AcmeException($"the issuer offered no dns-01 challenge for {name}");

        string value = _key!.Dns01Value(challenge.Token);

        try
        {
            await solver.PresentAsync(name, value, ct).ConfigureAwait(false);

            // An empty object says "ready, validate now".
            await PostAsync(challenge.Url, new AcmeEmpty(), AcmeJsonContext.Default.AcmeEmpty,
                AcmeJsonContext.Default.AcmeChallenge, ct).ConfigureAwait(false);

            authorization = await WaitForAsync(authorizationUrl, AcmeJsonContext.Default.AcmeAuthorization,
                a => a.Status is "valid" or "invalid" or "deactivated" or "expired" or "revoked", ct)
                .ConfigureAwait(false);

            if (authorization.Status != "valid")
            {
                string detail = authorization.Challenges.FirstOrDefault(c => c.Type == "dns-01")?.Error?.Detail
                    ?? authorization.Status;
                throw new AcmeException($"the issuer did not validate {name}: {detail}");
            }
        }
        finally
        {
            // Removed whatever happened: a challenge record left behind is a stale TXT in somebody's
            // zone, and the next order writes its own.
            try
            {
                await solver.CleanupAsync(name, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "the challenge record for {Name} could not be removed", name);
            }
        }
    }

    private async Task EnsureAccountAsync(CancellationToken ct)
    {
        _key ??= AcmeAccountKey.LoadOrCreate(accountKeyPath);

        if (_directory is null)
        {
            using HttpResponseMessage response = await http.GetAsync(directoryUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            _directory = await ReadAsync(response, AcmeJsonContext.Default.AcmeDirectory, ct).ConfigureAwait(false);
        }

        if (_kid is not null)
            return;

        // Registering with a key the issuer already knows answers with that account's URL, so this is
        // how an existing account is found as well as how a new one is made.
        var account = new AcmeNewAccount
        {
            Contact = string.IsNullOrWhiteSpace(contact) ? null : ["mailto:" + contact.Trim()],
        };

        string payload = JsonSerializer.Serialize(account, AcmeJsonContext.Default.AcmeNewAccount);
        using HttpResponseMessage created = await SendSignedAsync(
            _directory.NewAccount, payload, ct, useJwk: true).ConfigureAwait(false);

        _kid = created.Headers.Location?.ToString()
            ?? throw new AcmeException("the issuer registered the account but named no account URL");
    }

    private async Task<(TResponse Body, string Location)> PostAsync<TRequest, TResponse>(
        string url, TRequest request, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(request, requestType);
        using HttpResponseMessage response = await SendSignedAsync(url, payload, ct).ConfigureAwait(false);
        TResponse body = await ReadAsync(response, responseType, ct).ConfigureAwait(false);
        return (body, response.Headers.Location?.ToString() ?? url);
    }

    /// <summary>A POST-as-GET: a signed request with an empty payload, which is how ACME reads a resource.</summary>
    private async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendSignedAsync(url, "", ct).ConfigureAwait(false);
        return await ReadAsync(response, type, ct).ConfigureAwait(false);
    }

    private async Task<T> WaitForAsync<T>(string url, JsonTypeInfo<T> type, Func<T, bool> done, CancellationToken ct)
    {
        // Two minutes is far past how long either issuer takes to validate a DNS-01 challenge whose
        // record is already visible at the zone's own servers, which the solver waited for.
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            T current = await GetAsync(url, type, ct).ConfigureAwait(false);
            if (done(current))
                return current;

            if (DateTimeOffset.UtcNow > giveUp)
                throw new AcmeException($"the issuer did not finish {url} within two minutes");

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sign and send one request, retrying once on a stale nonce — the one refusal that means "ask
    /// again", which a nonce spent by a concurrent request or held past its lifetime produces.
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedAsync(
        string url, string payload, CancellationToken ct, bool useJwk = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            string nonce = await NonceAsync(ct).ConfigureAwait(false);
            _nonce = null;

            var header = new AcmeProtected
            {
                Nonce = nonce,
                Url = url,
                Jwk = useJwk ? _key!.Jwk : null,
                Kid = useJwk ? null : _kid,
            };

            string protectedPart = AcmeAccountKey.Base64Url(
                JsonSerializer.SerializeToUtf8Bytes(header, AcmeJsonContext.Default.AcmeProtected));
            string payloadPart = payload.Length == 0 ? "" : AcmeAccountKey.Base64Url(Encoding.UTF8.GetBytes(payload));
            string signature = AcmeAccountKey.Base64Url(
                _key!.Sign(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart)));

            var jws = new AcmeJws { Protected = protectedPart, Payload = payloadPart, Signature = signature };
            var content = new StringContent(JsonSerializer.Serialize(jws, AcmeJsonContext.Default.AcmeJws));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");

            HttpResponseMessage response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? next))
                _nonce = next.FirstOrDefault();

            if (response.IsSuccessStatusCode)
                return response;

            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();

            AcmeProblem? problem = null;
            try { problem = JsonSerializer.Deserialize(raw, AcmeJsonContext.Default.AcmeProblem); }
            catch (JsonException) { }

            if (attempt == 0 && problem?.Type == "urn:ietf:params:acme:error:badNonce")
                continue;

            throw new AcmeException(
                $"the issuer answered {(int)response.StatusCode} for {url}: {problem?.Detail ?? raw}");
        }
    }

    private async Task<string> NonceAsync(CancellationToken ct)
    {
        if (_nonce is not null)
            return _nonce;

        using var request = new HttpRequestMessage(HttpMethod.Head, _directory!.NewNonce);
        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values))
            throw new AcmeException("the issuer handed out no nonce");

        return values.First();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        T? body = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), type, ct).ConfigureAwait(false);
        return body ?? throw new AcmeException("the issuer answered with nothing");
    }

    public void Dispose()
    {
        _key?.Dispose();
        _gate.Dispose();
    }
}
