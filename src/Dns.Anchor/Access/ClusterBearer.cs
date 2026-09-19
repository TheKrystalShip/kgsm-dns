using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// Admits a person's browser to this anchor's reads on the session the cluster's auth anchor minted.
/// </summary>
/// <remarks>
/// <para>
/// <b>One sign-in reaches this page.</b> A person signs in once, at the member holding <c>auth</c>, and the
/// session verifies here against the key that member publishes through gossip. Nothing between the browser
/// and this anchor is needed, so the page stands in a cluster whose nodes run no Control Panel API.
/// </para>
/// <para>
/// <b>Only a cluster session is accepted.</b> The shared validation takes this surface's own sessions and
/// its cluster's; this anchor mints none, so its own half is a key generated at start and used to sign
/// nothing — no token can be presented that verifies against it.
/// </para>
/// <para>
/// <b>Authority is the account copy's, read on this request.</b> The tier the session was minted with is
/// ignored; the person's current tier and whether the account is switched on are read from this anchor's
/// own copy of the cluster's accounts. A session somebody ended elsewhere is refused from the moment the
/// announcement arrives.
/// </para>
/// <para>
/// <b>"Cannot tell" is its own answer.</b> An anchor that has not yet heard who holds the accounts, or has
/// no copy of them yet, cannot verify anybody. That is a 503 naming the reason rather than a 401, which
/// would send the browser back to a sign-in that works and then fails here the same way.
/// </para>
/// </remarks>
internal sealed class ClusterBearer(
    IClusterSessionKeys keys,
    ClusterSessionRevocations revocations,
    AnchorAccounts accounts,
    AnchorOptions options)
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly TokenValidationParameters _ownHalf = new()
    {
        ValidateIssuer = true,
        ValidIssuer = "kgsm-dns:" + options.MemberId,
        ValidateAudience = true,
        ValidAudience = "kgsm-dns:" + options.MemberId,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
    };

    /// <summary>The least a person must hold to read this anchor's pages.</summary>
    public const KgsmTier Minimum = KgsmTier.Viewer;

    /// <summary>
    /// Admit the request, or answer it with why not.
    /// </summary>
    /// <returns>Null when the caller is admitted; otherwise the result to send back.</returns>
    public async Task<IResult?> RefuseAsync(HttpRequest request, CancellationToken ct)
    {
        string header = request.Headers.Authorization.ToString();
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        if (keys.Audience is null || keys.Issuer is null || keys.Keys.Count == 0)
        {
            return Unavailable(
                "this anchor has not yet heard which member holds the cluster's accounts, so it cannot "
                + "verify a session");
        }

        TokenValidationResult result = await Handler
            .ValidateTokenAsync(token, ClusterSessionValidation.Accepting(_ownHalf, keys))
            .ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is not ClaimsIdentity claims)
            return Results.Unauthorized();

        // A refresh token lives far longer than an access token, and accepting one as a bearer would erase
        // the short lifetime that bounds what a stolen bearer is worth.
        if (claims.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Access)
            return Results.Unauthorized();

        KgsmIdentity? identity = SessionClaims.ReadIdentity(claims);
        string? sessionId = SessionClaims.ReadSessionId(claims);
        if (identity is null || sessionId is null)
            return Results.Unauthorized();

        if (await revocations.IsRevokedAsync(sessionId, ct).ConfigureAwait(false))
            return Results.Unauthorized();

        AuthorityAnswer? answer = await accounts.ResolveAsync(identity, ct).ConfigureAwait(false);
        if (answer is not { } authority)
        {
            return Unavailable(accounts.UnavailableReason
                ?? "this anchor has no copy of the cluster's accounts yet, so nobody can be identified here");
        }

        // A switched-off account is a door closing: the session ends here as it does on every member.
        if (authority.Outcome == AuthorityOutcome.Disabled)
            return Results.Unauthorized();

        if (authority.Outcome != AuthorityOutcome.Ok || authority.Tier < Minimum)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return null;
    }

    private static IResult Unavailable(string reason) =>
        Results.Json(
            new AnchorError("cannot_verify", reason), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
