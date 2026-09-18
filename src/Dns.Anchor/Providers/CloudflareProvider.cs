using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Dns.Anchor.Providers;

/// <summary>The provider is holding a name the cluster asked for on a record it does not own.</summary>
/// <remarks>
/// Cloudflare refuses a second record on a name a <c>CNAME</c> already holds, and that refusal is the
/// lock that keeps two claims for one name from both succeeding. It is raised as its own type because
/// the caller answers it by choosing another label, which is a different thing from a call that failed.
/// </remarks>
public sealed class DnsNameTakenException(string name, string message) : Exception(message)
{
    /// <summary>The name that is already held.</summary>
    public string Name { get; } = name;
}

/// <summary>A call to the provider failed, and the message is what it said.</summary>
public sealed class DnsProviderException(string message) : Exception(message);

/// <summary>
/// The zone at Cloudflare.
/// </summary>
/// <remarks>
/// <para>
/// The token is zone-scoped, which is the narrowest scope the provider offers: there is no way to
/// restrict a credential to part of a zone. That is the whole reason one member holds it and every
/// other member asks — a token on each node would let any node rewrite any name in the zone, and the
/// names that matter most are the ones people sign in through.
/// </para>
/// <para>
/// Nothing here decides what to write. A call arrives with the name, the content and the mark already
/// settled, so the containment rules live in one place and this type cannot quietly widen them.
/// </para>
/// </remarks>
public sealed class CloudflareProvider(
    IHttpClientFactory factory,
    CloudflareOptions options,
    ILogger<CloudflareProvider> logger) : IDnsProvider
{
    /// <summary>The named client, so the timeout is configured once where the client is registered.</summary>
    public const string HttpClientName = "cloudflare";

    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    public bool Configured =>
        !string.IsNullOrWhiteSpace(options.ZoneId) && !string.IsNullOrWhiteSpace(options.Token);

    public async Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken ct = default)
    {
        RequireConfigured();

        var records = new List<ProviderRecord>();
        using HttpClient client = Client();

        // Paged through to the end rather than reading the first page: a zone holds more than one page
        // of records, and a listing that stops early reads as "that name is free" for everything it did
        // not fetch — which is the one wrong answer that ends with a record being overwritten.
        for (int page = 1; page <= 100; page++)
        {
            string url = $"{ApiBase}/zones/{options.ZoneId}/dns_records?per_page=100&page={page}";
            using HttpResponseMessage response = await client.GetAsync(url, ct).ConfigureAwait(false);
            CloudflareListResponse body = await ReadAsync(
                response, CloudflareJsonContext.Default.CloudflareListResponse, ct).ConfigureAwait(false);

            foreach (CloudflareRecord record in body.Result ?? [])
                records.Add(Project(record));

            int total = body.ResultInfo?.TotalPages ?? 1;
            if (page >= total)
                break;
        }

        return records;
    }

    public async Task<ProviderRecord> CreateAsync(DesiredRecord record, CancellationToken ct = default)
    {
        RequireConfigured();

        using HttpClient client = Client();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{ApiBase}/zones/{options.ZoneId}/dns_records",
            Write(record),
            CloudflareJsonContext.Default.CloudflareRecordWrite,
            ct).ConfigureAwait(false);

        // A name already held answers 400 with one of two codes: 81053 when an address or CNAME record
        // holds it, 81054 when a CNAME does and the new record is not one. It is a state, not a
        // failure: the caller picks the next label. Matched on the code rather than the message,
        // because the wording is prose the provider is free to rephrase.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (ErrorCodes(raw).Any(code => code is NameHeldByRecord or NameHeldByCname))
                throw new DnsNameTakenException(record.Name, Summarize(raw, response));

            throw new DnsProviderException($"the provider answered 400: {Summarize(raw, response)}");
        }

        CloudflareRecordResponse body = await ReadAsync(
            response, CloudflareJsonContext.Default.CloudflareRecordResponse, ct).ConfigureAwait(false);

        logger.LogInformation("published {Type} {Name} → {Content}", record.Type, record.Name, record.Content);
        return Project(body.Result ?? throw new DnsProviderException("the provider created no record"));
    }

    public async Task<ProviderRecord> UpdateAsync(
        string id, DesiredRecord record, CancellationToken ct = default)
    {
        RequireConfigured();

        using HttpClient client = Client();
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"{ApiBase}/zones/{options.ZoneId}/dns_records/{id}",
            Write(record),
            CloudflareJsonContext.Default.CloudflareRecordWrite,
            ct).ConfigureAwait(false);

        CloudflareRecordResponse body = await ReadAsync(
            response, CloudflareJsonContext.Default.CloudflareRecordResponse, ct).ConfigureAwait(false);

        logger.LogInformation("moved {Type} {Name} → {Content}", record.Type, record.Name, record.Content);
        return Project(body.Result ?? throw new DnsProviderException("the provider returned no record"));
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        RequireConfigured();

        using HttpClient client = Client();
        using HttpResponseMessage response = await client.DeleteAsync(
            $"{ApiBase}/zones/{options.ZoneId}/dns_records/{id}", ct).ConfigureAwait(false);

        // A record that is already gone is the state this call is for, so a 404 is success.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await ReadAsync(response, CloudflareJsonContext.Default.CloudflareRecordResponse, ct)
            .ConfigureAwait(false);
    }

    private HttpClient Client()
    {
        HttpClient client = factory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        return client;
    }

    private void RequireConfigured()
    {
        if (!Configured)
            throw new DnsProviderException("no zone and token are configured for the DNS provider");
    }

    private static CloudflareRecordWrite Write(DesiredRecord record) => new()
    {
        Name = record.Name,
        Type = record.Type,
        Content = record.Content,
        Ttl = record.Ttl,
        Proxied = false,
        Comment = record.Comment,
    };

    private static ProviderRecord Project(CloudflareRecord record) => new(
        Id: record.Id ?? "",
        Name: record.Name ?? "",
        Type: record.Type ?? "",
        Content: record.Content ?? "",
        Ttl: record.Ttl,
        Proxied: record.Proxied,
        Comment: record.Comment ?? "");

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new DnsProviderException(
                $"the provider answered {(int)response.StatusCode}: {await Detail(response, ct).ConfigureAwait(false)}");
        }

        T? body = await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
        if (body is null)
            throw new DnsProviderException("the provider answered with nothing");

        return body;
    }

    /// <summary>
    /// What the provider said went wrong, as one line.
    /// </summary>
    /// <remarks>
    /// Read from the body rather than reported as a status code alone: the difference between a token
    /// that lacks a permission, a zone id that is not this zone's, and a name that is already held is
    /// only ever in the body, and all three arrive as the same code.
    /// </remarks>
    private static async Task<string> Detail(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Summarize(raw, response);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return response.ReasonPhrase ?? "no detail";
        }
    }

    /// <summary>A name is held by an address or <c>CNAME</c> record.</summary>
    private const int NameHeldByRecord = 81053;

    /// <summary>A name is held by a <c>CNAME</c>, which may share it with nothing.</summary>
    private const int NameHeldByCname = 81054;

    private static string Summarize(string raw, HttpResponseMessage response)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return response.ReasonPhrase ?? "no detail";

        List<string> messages = [.. Errors(raw).Select(e => e.Message).Where(m => m.Length > 0)];
        if (messages.Count > 0)
            return string.Join("; ", messages);

        return raw.Length > 400 ? raw[..400] : raw;
    }

    private static IEnumerable<int> ErrorCodes(string raw) => Errors(raw).Select(e => e.Code);

    private static List<(int Code, string Message)> Errors(string raw)
    {
        var found = new List<(int, string)>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("errors", out JsonElement errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (JsonElement error in errors.EnumerateArray())
            {
                int code = error.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32()
                    : 0;
                string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? ""
                    : "";
                found.Add((code, message));
            }
        }
        catch (JsonException)
        {
            // Not the provider's error shape. The raw text is still reported by the caller.
        }

        return found;
    }
}

/// <summary>What the Cloudflare implementation needs to act on a zone.</summary>
/// <param name="ZoneId">The zone's id, which the provider's dashboard and API both name.</param>
/// <param name="Token">The API token. It is a secret, held by this member alone.</param>
public sealed record CloudflareOptions(string ZoneId, string Token);
