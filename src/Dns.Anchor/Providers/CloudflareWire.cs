using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Dns.Anchor.Providers;

/// <summary>One record as Cloudflare's API spells it.</summary>
internal sealed class CloudflareRecord
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("ttl")] public int Ttl { get; set; }
    [JsonPropertyName("proxied")] public bool Proxied { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
}

/// <summary>A record as it is written.</summary>
/// <remarks>
/// <c>proxied</c> is stated on every write and always false. A proxied record answers web traffic from
/// the provider's own addresses and carries nothing else, so a game name behind one resolves to
/// something no game client can connect to. Leaving the field out would accept whatever the zone
/// defaults to, which is a setting this anchor does not own.
/// </remarks>
internal sealed class CloudflareRecordWrite
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("ttl")] public int Ttl { get; set; }
    [JsonPropertyName("proxied")] public bool Proxied { get; set; }
    [JsonPropertyName("comment")] public string Comment { get; set; } = "";
}

internal sealed class CloudflareError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

internal sealed class CloudflareListResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CloudflareError>? Errors { get; set; }
    [JsonPropertyName("result")] public List<CloudflareRecord>? Result { get; set; }
    [JsonPropertyName("result_info")] public CloudflareResultInfo? ResultInfo { get; set; }
}

internal sealed class CloudflareResultInfo
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
}

internal sealed class CloudflareRecordResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CloudflareError>? Errors { get; set; }
    [JsonPropertyName("result")] public CloudflareRecord? Result { get; set; }
}

/// <summary>
/// The provider's wire shapes, serialized without reflection so the anchor publishes as Native AOT.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(CloudflareListResponse))]
[JsonSerializable(typeof(CloudflareRecordResponse))]
[JsonSerializable(typeof(CloudflareRecordWrite))]
internal sealed partial class CloudflareJsonContext : JsonSerializerContext;
