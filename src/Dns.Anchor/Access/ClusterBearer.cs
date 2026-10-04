using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// Admits a person's browser to one of this anchor's actions on the session the cluster's auth anchor
/// minted.
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
/// <b>The session names who, the replica says what.</b> The person is resolved from the session's
/// identity in this anchor's own authority replica and evaluated there for the route's <c>dns:*</c>
/// action, so a role change or a disabled account lands on the next request. A session somebody ended
/// elsewhere is refused from the moment the announcement arrives.
/// </para>
/// <para>
/// <b>"Cannot tell" is its own answer.</b> An anchor that has not yet heard who holds the accounts cannot
/// verify anybody, and one whose replica cannot be read cannot evaluate them. Both are a 503 naming the
/// reason rather than a 401 or a 403, which would send the browser back to a sign-in that works, or tell
/// somebody they lack access on the strength of an outage.
/// </para>
/// </remarks>
internal sealed class ClusterBearer(
    IClusterSessionKeys keys,
    ClusterSessionRevocations revocations,
    MemberAccess access)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Admit the request to <paramref name="action"/>, or answer it with why not.</summary>
    /// <returns>Null when the caller is admitted; otherwise the result to send back.</returns>
    public async Task<IResult?> RefuseAsync(HttpRequest request, string action, CancellationToken ct) =>
        (await AuthorizeAsync(request, action, ct).ConfigureAwait(false)).Refusal;

    /// <summary>
    /// Admit the request to <paramref name="action"/>, or answer it with why not. A null action admits any
    /// usable account here — what <c>/me/access</c> needs, since somebody holding nothing is exactly who
    /// needs to see that.
    /// </summary>
    /// <returns>
    /// A null <see cref="Admission.Refusal"/> and the caller's identity when admitted; otherwise the
    /// result to send back and no identity.
    /// </returns>
    public async Task<Admission> AuthorizeAsync(HttpRequest request, string? action, CancellationToken ct)
    {
        string header = request.Headers.Authorization.ToString();
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(token))
            return new Admission(Results.Unauthorized(), null);

        if (keys.Audience is null || keys.Issuer is null || keys.Keys.Count == 0)
        {
            return new Admission(Unavailable("cannot_verify",
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

        MemberAccessCaller caller = await access.ResolveAsync(identity, ct).ConfigureAwait(false);
        switch (caller.Refusal)
        {
            case MemberAccessRefusal.Unavailable:
                return new Admission(Unavailable("authority_unavailable",
                    caller.Reason ?? "this anchor's authority replica could not be read"), null);

            // A switched-off account is a door closing: the session ends here as it does on every member.
            case MemberAccessRefusal.AccountDisabled:
                return new Admission(Results.Unauthorized(), null);

            case MemberAccessRefusal.NoAccount:
                return new Admission(Results.StatusCode(StatusCodes.Status403Forbidden), null);
        }

        if (action is not null && !caller.Evaluator!.Allows(caller.AccountId!, action, AccessScope.Cluster).Allowed)
            return new Admission(Refused(action), null);

        return new Admission(null, identity);
    }

    /// <summary>What a request resolved to: a refusal to send back, or who is asking.</summary>
    /// <param name="Refusal">The result to send back, or null when the caller is admitted.</param>
    /// <param name="Identity">The caller's identity, present exactly when <paramref name="Refusal"/> is null.</param>
    public readonly record struct Admission(IResult? Refusal, KgsmIdentity? Identity);

    /// <summary>A 403 naming the action, so whoever is asked to grant it knows what to grant.</summary>
    private static IResult Refused(string action) =>
        Results.Json(
            new AnchorError("forbidden", $"This needs {action}."), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult Unavailable(string code, string reason) =>
        Results.Json(
            new AnchorError(code, reason), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
