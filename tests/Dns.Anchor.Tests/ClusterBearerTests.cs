using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Dns.Anchor.Access;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// A browser reaching this anchor on the cluster's session: admitted on the key the accounts' holder
/// publishes, with the person's authority read from this anchor's own copy of the accounts.
/// </summary>
public sealed class ClusterBearerTests : IDisposable
{
    private const string Audience = "kgsm-cluster";
    private const string Issuer = "kgsm";
    private const string Handle = "local:usr_probe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kgsm-dns-bearer-{Guid.NewGuid():N}");
    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeKeys _keys = new();
    private readonly EndedSessions _ended;
    private readonly AnchorAccounts _accounts;
    private readonly SqliteUserStore _store;
    private readonly ClusterBearer _bearer;

    public ClusterBearerTests()
    {
        AnchorOptions options = AnchorOptions.FromSettings(new AnchorSettings
        {
            MemberId = "probe-dns",
            AccountsPath = Path.Combine(_root, "accounts.db"),
            SessionsPath = Path.Combine(_root, "sessions.db"),
        });
        Directory.CreateDirectory(_root);

        _keys.Publish(new ECDsaSecurityKey(_signing) { KeyId = "k1" });
        _ended = new EndedSessions(options);
        _accounts = new AnchorAccounts(options, NullLogger<AnchorAccounts>.Instance);
        _store = new SqliteUserStore(new UserStoreOptions { Path = options.AccountsPath });

        var revocations = new ClusterSessionRevocations(
            _ended, new MemoryCache(Options.Create(new MemoryCacheOptions())), TimeSpan.FromMilliseconds(1));
        _bearer = new ClusterBearer(_keys, revocations, _accounts, options);
    }

    [Fact]
    public async Task A_cluster_session_for_an_active_account_is_admitted()
    {
        await AccountAsync(KgsmTier.Viewer, UserStatus.Active);

        Assert.Null(await RefuseAsync(Mint()));
    }

    [Fact]
    public async Task The_tier_is_the_accounts_not_the_sessions()
    {
        // Minted as an admin; the copy says the account holds nothing now. The copy wins.
        await AccountAsync(KgsmTier.None, UserStatus.Active);

        Assert.Equal(StatusCodes.Status403Forbidden, await StatusOfAsync(Mint(tier: "admin")));
    }

    [Fact]
    public async Task A_disabled_account_is_signed_out_here()
    {
        await AccountAsync(KgsmTier.Admin, UserStatus.Disabled);

        Assert.Equal(StatusCodes.Status401Unauthorized, await StatusOfAsync(Mint()));
    }

    [Fact]
    public async Task A_session_ended_elsewhere_is_refused()
    {
        await AccountAsync(KgsmTier.Admin, UserStatus.Active);
        await _ended.RecordRevocationAsync("sid_ended", DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(StatusCodes.Status401Unauthorized, await StatusOfAsync(Mint(sid: "sid_ended")));
    }

    [Fact]
    public async Task A_refresh_token_is_not_a_bearer()
    {
        await AccountAsync(KgsmTier.Admin, UserStatus.Active);

        Assert.Equal(StatusCodes.Status401Unauthorized, await StatusOfAsync(Mint(kind: KgsmTokenKind.Refresh)));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        await AccountAsync(KgsmTier.Admin, UserStatus.Active);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(StatusCodes.Status401Unauthorized,
            await StatusOfAsync(Mint(key: new ECDsaSecurityKey(other) { KeyId = "k1" })));
    }

    [Fact]
    public async Task An_empty_account_copy_cannot_tell_anybody_apart()
    {
        // Nothing has been copied from the accounts' holder yet: "cannot tell", never "you hold nothing".
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, await StatusOfAsync(Mint()));
    }

    [Fact]
    public async Task No_published_key_means_no_session_can_be_verified()
    {
        await AccountAsync(KgsmTier.Admin, UserStatus.Active);
        string token = Mint();
        _keys.Clear();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, await StatusOfAsync(token));
    }

    [Fact]
    public async Task No_bearer_is_unauthorized()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, await StatusOfAsync(token: null));
    }

    public void Dispose()
    {
        _signing.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task AccountAsync(KgsmTier tier, UserStatus status)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _store.CreateAsync(new KgsmUser(
            "usr_probe", "probe", "Probe", tier, TierSource.Granted, status, now, now));
        await _store.AddCredentialAsync(new UserCredential(
            "cred_probe", "usr_probe", CredentialKind.Identity, Handle, null, null, now, null));
    }

    private string Mint(
        string tier = "viewer", string kind = KgsmTokenKind.Access, string sid = "sid_probe",
        SecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                key ?? _keys.Keys[0], SecurityAlgorithms.EcdsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", Handle),
                new Claim(KgsmAuthClaims.Tier, tier),
                new Claim(KgsmAuthClaims.Host, "kgsm"),
                new Claim(KgsmAuthClaims.TokenKind, kind),
                new Claim(KgsmAuthClaims.SessionId, sid),
                new Claim(KgsmAuthClaims.Username, "probe"),
            ]),
        });

    private Task<IResult?> RefuseAsync(string? token)
    {
        var context = new DefaultHttpContext();
        if (token is not null)
            context.Request.Headers.Authorization = "Bearer " + token;
        return _bearer.RefuseAsync(context.Request, CancellationToken.None);
    }

    private async Task<int?> StatusOfAsync(string? token) =>
        await RefuseAsync(token) is IStatusCodeHttpResult refusal ? refusal.StatusCode : null;

    private sealed class FakeKeys : IClusterSessionKeys
    {
        private List<SecurityKey> _keys = [];

        public string? Audience { get; private set; }

        public string? Issuer { get; private set; }

        public IReadOnlyList<SecurityKey> Keys => _keys;

        public void Publish(SecurityKey key)
        {
            _keys = [key];
            Audience = ClusterBearerTests.Audience;
            Issuer = ClusterBearerTests.Issuer;
        }

        public void Clear()
        {
            _keys = [];
            Audience = null;
            Issuer = null;
        }
    }
}
