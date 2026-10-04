using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.Auth.Users;
using TheKrystalShip.KGSM.Dns.Anchor.Access;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// A browser reaching this anchor on the cluster's session: admitted on the key the accounts' holder
/// publishes, and evaluated for the route's <c>dns:*</c> action from this anchor's own authority replica.
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
    private readonly string _replicaPath;
    private readonly SqliteAuthorityStore _replica;

    // A new replica already holds version 1, so the first record delivered to it is 2.
    private long _version = 1;

    public ClusterBearerTests()
    {
        AnchorOptions options = AnchorOptions.FromSettings(new AnchorSettings
        {
            MemberId = "probe-dns",
            AccountsPath = Path.Combine(_root, "authority.db"),
            SessionsPath = Path.Combine(_root, "sessions.db"),
        });
        Directory.CreateDirectory(_root);

        _keys.Publish(new ECDsaSecurityKey(_signing) { KeyId = "k1" });
        _ended = new EndedSessions(options);
        _replicaPath = options.AccountsPath;
        _replica = new SqliteAuthorityStore(new UserStoreOptions { Path = _replicaPath });
        Apply(new CatalogRecord(
        [
            new CatalogActionRecord(DnsActions.NamesRead, "See names", "read", "cluster", false),
            new CatalogActionRecord(DnsActions.JournalRead, "Read logs", "read", "cluster", false),
            new CatalogActionRecord(DnsActions.ZoneCheck, "Check the zone", "execute", "cluster", false),
            new CatalogActionRecord(DnsActions.CertificatesRenew, "Renew", "execute", "cluster", false),
            new CatalogActionRecord(DnsActions.AliasesWrite, "Aliases", "write", "cluster", false),
            new CatalogActionRecord("kgsm:server.start", "Start servers", "execute", "instance", false),
        ], Next()));
    }

    private ClusterBearer Bearer(string? replicaPath = null)
    {
        var revocations = new ClusterSessionRevocations(
            _ended, new MemoryCache(Options.Create(new MemoryCacheOptions())), TimeSpan.FromMilliseconds(1));
        return new ClusterBearer(_keys, revocations, Access(replicaPath ?? _replicaPath));
    }

    private static MemberAccess Access(string replicaPath) =>
        new(new AuthorityReplicaFile(replicaPath, NullLogger<AuthorityReplicaFile>.Instance));

    [Fact]
    public async Task A_session_holding_the_routes_action_is_admitted_and_identified()
    {
        Account("active", DnsActions.NamesRead);

        ClusterBearer.Admission admission = await AuthorizeAsync(Mint(), DnsActions.NamesRead);

        Assert.Null(admission.Refusal);
        Assert.Equal("local:probe", admission.Identity!.ActorString);
    }

    /// <summary>
    /// Reading the page is not changing the zone: each write is its own action, and holding the read
    /// grants none of them. The refusal names what was missing.
    /// </summary>
    [Fact]
    public async Task Reading_the_names_does_not_reach_a_write()
    {
        Account("active", DnsActions.NamesRead);

        ClusterBearer.Admission admission = await AuthorizeAsync(Mint(), DnsActions.AliasesWrite);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(admission.Refusal));
        Assert.Null(admission.Identity);
    }

    [Fact]
    public async Task An_account_holding_nothing_is_refused_the_page()
    {
        Account("active");

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf((await AuthorizeAsync(Mint(), DnsActions.NamesRead)).Refusal));
    }

    /// <summary>A grant of another component's action reaches none of this anchor's.</summary>
    [Fact]
    public async Task Another_components_action_grants_nothing_here()
    {
        Account("active", "kgsm:server.start");

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf((await AuthorizeAsync(Mint(), DnsActions.ZoneCheck)).Refusal));
    }

    [Fact]
    public async Task An_account_awaiting_approval_holds_nothing()
    {
        Account("pending", DnsActions.NamesRead);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf((await AuthorizeAsync(Mint(), DnsActions.NamesRead)).Refusal));
    }

    [Fact]
    public async Task A_disabled_account_is_signed_out_here()
    {
        Account("disabled", DnsActions.NamesRead);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf((await AuthorizeAsync(Mint(), DnsActions.NamesRead)).Refusal));
    }

    [Fact]
    public async Task No_bearer_is_unauthorized()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf((await AuthorizeAsync(null, DnsActions.NamesRead)).Refusal));
    }

    [Fact]
    public async Task A_session_ended_elsewhere_is_refused()
    {
        Account("active", DnsActions.NamesRead);
        await _ended.RecordRevocationAsync("sid_ended", DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(StatusCodes.Status401Unauthorized,
            StatusOf((await AuthorizeAsync(Mint(sid: "sid_ended"), DnsActions.NamesRead)).Refusal));
    }

    [Fact]
    public async Task A_refresh_token_is_not_a_bearer()
    {
        Account("active", DnsActions.NamesRead);

        Assert.Equal(StatusCodes.Status401Unauthorized,
            StatusOf((await AuthorizeAsync(Mint(kind: KgsmTokenKind.Refresh), DnsActions.NamesRead)).Refusal));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        Account("active", DnsActions.NamesRead);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf((await AuthorizeAsync(
            Mint(key: new ECDsaSecurityKey(other) { KeyId = "k1" }), DnsActions.NamesRead)).Refusal));
    }

    /// <summary>
    /// No replica yet: "cannot tell", never "you hold nothing" — an outage must not tell somebody they
    /// lack access.
    /// </summary>
    [Fact]
    public async Task No_replica_cannot_tell_anybody_apart()
    {
        ClusterBearer.Admission admission = await AuthorizeAsync(
            Mint(), DnsActions.NamesRead, Bearer(Path.Combine(_root, "absent.db")));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(admission.Refusal));
    }

    [Fact]
    public async Task No_published_key_means_no_session_can_be_verified()
    {
        Account("active", DnsActions.NamesRead);
        string token = Mint();
        _keys.Clear();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf((await AuthorizeAsync(token, DnsActions.NamesRead)).Refusal));
    }

    // ── /me/access ────────────────────────────────────────────────────────────────────────────

    /// <summary>GET /me/access against the replica at <paramref name="replicaPath"/>: the status and the body.</summary>
    private async Task<(int Status, System.Text.Json.JsonElement Body)> MeAccessAsync(string replicaPath)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(Bearer(replicaPath));
        services.AddSingleton(Access(replicaPath));

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Headers.Authorization = "Bearer " + Mint();
        context.Response.Body = new MemoryStream();

        IResult result = await MeAccessEndpoint.HandleAsync(context);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, System.Text.Json.JsonDocument.Parse(context.Response.Body).RootElement);
    }

    [Fact]
    public async Task MeAccess_WithNoReplica_IsAnOutageNotAnEmptyAnswer()
    {
        (int status, System.Text.Json.JsonElement body) = await MeAccessAsync(Path.Combine(_root, "absent.db"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("authority_unavailable", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Somebody holding nothing here is let through, because they are exactly who needs to see that; and
    /// only this anchor's own actions are listed.
    /// </summary>
    [Fact]
    public async Task MeAccess_ListsThisAnchorsOwnActions()
    {
        Account("active", DnsActions.ZoneCheck, "kgsm:server.start");

        (int status, System.Text.Json.JsonElement body) = await MeAccessAsync(_replicaPath);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal([DnsActions.ZoneCheck], body.GetProperty("cluster").EnumerateArray().Select(a => a.GetString()));
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

    private long Next() => ++_version;

    /// <summary>Apply one replicated record, then confirm the replica current at its version.</summary>
    private void Apply(Func<DateTimeOffset, Task> apply)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        apply(now).GetAwaiter().GetResult();
        _replica.ConfirmAsync(new AuthorityCurrent(_version, 300, AccessContract.Version, now), now).GetAwaiter().GetResult();
    }

    private void Apply(CatalogRecord record) => Apply(now => _replica.ApplyAsync(record, now));

    private void Apply(AccountRecord record) => Apply(now => _replica.ApplyAsync(record, now));

    private void Apply(PermissionRecord record) => Apply(now => _replica.ApplyAsync(record, now));

    private void Apply(RoleRecord record) => Apply(now => _replica.ApplyAsync(record, now));

    private void Apply(AssignmentRecord record) => Apply(now => _replica.ApplyAsync(record, now));

    /// <summary>The probe's account at <paramref name="status"/>, holding <paramref name="actions"/> across the cluster.</summary>
    private void Account(string status, params string[] actions)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Apply(new AccountRecord("usr_probe", "probe", "Probe", "admitted", "person", status, now, now,
            [new ReplicatedIdentity(Handle, null)], null, [], Next()));
        if (actions.Length == 0)
            return;

        Apply(new PermissionRecord("prm_1", "Probe", actions, Next()));
        Apply(new RoleRecord("role_1", "Probes", "custom", 1, ["prm_1"], Next()));
        Apply(new AssignmentRecord("asg_1", "usr_probe", "role_1", "cluster", null, now, Next()));
    }

    private string Mint(string kind = KgsmTokenKind.Access, string sid = "sid_probe", SecurityKey? key = null) =>
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
                new Claim(KgsmAuthClaims.Host, "kgsm"),
                new Claim(KgsmAuthClaims.TokenKind, kind),
                new Claim(KgsmAuthClaims.SessionId, sid),
                new Claim(KgsmAuthClaims.Username, "probe"),
            ]),
        });

    private Task<ClusterBearer.Admission> AuthorizeAsync(string? token, string action, ClusterBearer? bearer = null)
    {
        var context = new DefaultHttpContext();
        if (token is not null)
            context.Request.Headers.Authorization = "Bearer " + token;
        return (bearer ?? Bearer()).AuthorizeAsync(context.Request, action, CancellationToken.None);
    }

    private static int? StatusOf(IResult? refusal) => (refusal as IStatusCodeHttpResult)?.StatusCode;

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
