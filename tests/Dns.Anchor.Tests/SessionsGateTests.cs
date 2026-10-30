using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Dns.Anchor.Access;
using TheKrystalShip.KGSM.Sessions;
using TheKrystalShip.KGSM.Sessions.Testing;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>A browser reaching this anchor with the provider's access token: the token's actions decide.</summary>
public sealed class SessionsGateTests : IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sessions:Issuer"] = TestIssuer.DefaultIssuer,
            ["Sessions:Audience"] = TestIssuer.DefaultAudience,
        });
        builder.Services.AddKgsmSessions();
        _issuer.UseTestIssuer(builder.Services);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/gated", () => Results.Ok()).Gated(DnsActions.ZoneCheck);
        MeAccessEndpoint.Map(_app);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task NoToken_IsUnauthorized() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/gated", null)).StatusCode);

    [Fact]
    public async Task TokenWithoutTheAction_IsForbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Get("/gated", _issuer.Mint("u1", actions: [DnsActions.NamesRead]))).StatusCode);

    [Fact]
    public async Task TokenListingTheAction_IsAdmitted() =>
        Assert.Equal(HttpStatusCode.OK,
            (await Get("/gated", _issuer.Mint("u1", actions: [DnsActions.ZoneCheck]))).StatusCode);

    [Fact]
    public async Task MeAccess_ListsOnlyThisAnchorsActionsFromTheToken()
    {
        string token = _issuer.Mint("u1", actions: [DnsActions.NamesRead, "kgsm:instances.read"]);
        HttpResponseMessage response = await Get(MeAccessEndpoint.Path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] cluster = [.. body.RootElement.GetProperty("cluster").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal([DnsActions.NamesRead], cluster);
    }

    [Fact]
    public async Task MeAccess_ForSomebodyHoldingNothing_IsAnEmptyReport()
    {
        HttpResponseMessage response = await Get(MeAccessEndpoint.Path, _issuer.Mint("u1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("cluster").EnumerateArray());
    }
}
