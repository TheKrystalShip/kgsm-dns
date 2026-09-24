using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
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
/// <b>Only a cluster session is accepted.</b> It is verified against the key, audience and issuer the
/// holder of <c>auth</c> publishes, and this anchor holds nothing that could sign one.
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
    AnchorAccounts accounts)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The least a person must hold to read this anchor's pages.</summary>
    public const KgsmTier Minimum = KgsmTier.Viewer;

    /// <summary>The least a person must hold to change anything through this anchor.</summary>
    public const KgsmTier AdminMinimum = KgsmTier.Admin;

    /// <summary>
    /// Admit the request to a read, or answer it with why not.
    /// </summary>
    /// <returns>Null when the caller is admitted; otherwise the result to send back.</returns>
    public async Task<IResult?> RefuseAsync(HttpRequest request, CancellationToken ct) =>
        (await AuthorizeAsync(request, Minimum, ct).ConfigureAwait(false)).Refusal;

    /// <summary>
    /// Admit the request to a route requiring at least <paramref name="minimum"/>, or answer it with why
    /// not.
    /// </summary>
    /// <returns>
    /// A null <see cref="Admission.Refusal"/> and the caller's identity when admitted; otherwise the
    /// result to send back and no identity.
    /// </returns>
    public async Task<Admission> AuthorizeAsync(HttpRequest request, KgsmTier minimum, CancellationToken ct)
    {
        string header = request.Headers.Authorization.ToString();
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(token))
            return new Admission(Results.Unauthorized(), null);

        if (keys.Audience is null || keys.Issuer is null || keys.Keys.Count == 0)
        {
            return new Admission(Unavailable(
                "this anchor has not yet heard which member holds the cluster's accounts, so it cannot "
                + "verify a session"), null);
        }

        TokenValidationResult result = await Handler
            .ValidateTokenAsync(token, ClusterSessionValidation.Accepting(keys))
            .ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is not ClaimsIdentity claims)
            return new Admission(Results.Unauthorized(), null);

        // A refresh token lives far longer than an access token, and accepting one as a bearer would erase
        // the short lifetime that bounds what a stolen bearer is worth.
        if (claims.FindFirst(KgsmAuthClaims.TokenKind)?.Value != KgsmTokenKind.Access)
            return new Admission(Results.Unauthorized(), null);

        KgsmIdentity? identity = SessionClaims.ReadIdentity(claims);
        string? sessionId = SessionClaims.ReadSessionId(claims);
        if (identity is null || sessionId is null)
            return new Admission(Results.Unauthorized(), null);

        if (await revocations.IsRevokedAsync(sessionId, ct).ConfigureAwait(false))
            return new Admission(Results.Unauthorized(), null);

        AuthorityAnswer? answer = await accounts.ResolveAsync(identity, ct).ConfigureAwait(false);
        if (answer is not { } authority)
        {
            return new Admission(Unavailable(accounts.UnavailableReason
                ?? "this anchor has no copy of the cluster's accounts yet, so nobody can be identified here"), null);
        }

        // A switched-off account is a door closing: the session ends here as it does on every member.
        if (authority.Outcome == AuthorityOutcome.Disabled)
            return new Admission(Results.Unauthorized(), null);

        if (authority.Outcome != AuthorityOutcome.Ok || authority.Tier < minimum)
            return new Admission(Results.StatusCode(StatusCodes.Status403Forbidden), null);

        return new Admission(null, identity);
    }

    /// <summary>What a request resolved to: a refusal to send back, or who is asking.</summary>
    /// <param name="Refusal">The result to send back, or null when the caller is admitted.</param>
    /// <param name="Identity">The caller's identity, present exactly when <paramref name="Refusal"/> is null.</param>
    public readonly record struct Admission(IResult? Refusal, KgsmIdentity? Identity);

    private static IResult Unavailable(string reason) =>
        Results.Json(
            new AnchorError("cannot_verify", reason), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
