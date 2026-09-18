using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Dns.Anchor.Acme;

/// <summary>The issuer's directory: where each ACME resource is (RFC 8555 §7.1.1).</summary>
internal sealed class AcmeDirectory
{
    [JsonPropertyName("newNonce")] public string NewNonce { get; set; } = "";
    [JsonPropertyName("newAccount")] public string NewAccount { get; set; } = "";
    [JsonPropertyName("newOrder")] public string NewOrder { get; set; } = "";
}

internal sealed class AcmeIdentifier
{
    [JsonPropertyName("type")] public string Type { get; set; } = "dns";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

internal sealed class AcmeProblem
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

internal sealed class AcmeOrder
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("authorizations")] public List<string> Authorizations { get; set; } = [];
    [JsonPropertyName("finalize")] public string Finalize { get; set; } = "";
    [JsonPropertyName("certificate")] public string? Certificate { get; set; }
    [JsonPropertyName("error")] public AcmeProblem? Error { get; set; }
}

internal sealed class AcmeChallenge
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("error")] public AcmeProblem? Error { get; set; }
}

internal sealed class AcmeAuthorization
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("identifier")] public AcmeIdentifier Identifier { get; set; } = new();
    [JsonPropertyName("challenges")] public List<AcmeChallenge> Challenges { get; set; } = [];
}

internal sealed class AcmeNewAccount
{
    [JsonPropertyName("termsOfServiceAgreed")] public bool TermsOfServiceAgreed { get; set; } = true;
    [JsonPropertyName("contact")] public List<string>? Contact { get; set; }
}

internal sealed class AcmeNewOrder
{
    [JsonPropertyName("identifiers")] public List<AcmeIdentifier> Identifiers { get; set; } = [];
}

internal sealed class AcmeFinalize
{
    [JsonPropertyName("csr")] public string Csr { get; set; } = "";
}

/// <summary>An empty JSON object: the payload that tells the issuer a challenge is ready.</summary>
internal sealed class AcmeEmpty;

/// <summary>The account key's public half as a JWK, members in the order RFC 7638 fixes for a thumbprint.</summary>
internal sealed class AcmeJwk
{
    [JsonPropertyName("crv")] public string Crv { get; set; } = "P-256";
    [JsonPropertyName("kty")] public string Kty { get; set; } = "EC";
    [JsonPropertyName("x")] public string X { get; set; } = "";
    [JsonPropertyName("y")] public string Y { get; set; } = "";
}

internal sealed class AcmeProtected
{
    [JsonPropertyName("alg")] public string Alg { get; set; } = "ES256";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("jwk")] public AcmeJwk? Jwk { get; set; }
    [JsonPropertyName("kid")] public string? Kid { get; set; }
}

internal sealed class AcmeJws
{
    [JsonPropertyName("protected")] public string Protected { get; set; } = "";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

/// <summary>Every ACME shape, serialized without reflection so the anchor publishes as Native AOT.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AcmeDirectory))]
[JsonSerializable(typeof(AcmeOrder))]
[JsonSerializable(typeof(AcmeAuthorization))]
[JsonSerializable(typeof(AcmeChallenge))]
[JsonSerializable(typeof(AcmeProblem))]
[JsonSerializable(typeof(AcmeNewAccount))]
[JsonSerializable(typeof(AcmeNewOrder))]
[JsonSerializable(typeof(AcmeFinalize))]
[JsonSerializable(typeof(AcmeEmpty))]
[JsonSerializable(typeof(AcmeJwk))]
[JsonSerializable(typeof(AcmeProtected))]
[JsonSerializable(typeof(AcmeJws))]
internal sealed partial class AcmeJsonContext : JsonSerializerContext;
