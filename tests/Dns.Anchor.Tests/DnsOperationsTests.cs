using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.KGSM.Dns.Anchor.Access;
using TheKrystalShip.KGSM.Dns.Anchor.Status;
using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// <c>GET /operations</c>: every gated route and its <c>dns:*</c> action, read off the declarations the
/// handlers enforce — the routes as this anchor maps them, not a list beside them.
/// </summary>
public sealed class DnsOperationsTests
{
    private static OperationManifest Built()
    {
        WebApplication app = WebApplication.CreateSlimBuilder().Build();
        StatusEndpoints.Map(app);
        AdminEndpoints.Map(app);
        DnsOperations.Map(app);
        return DnsOperations.Build(new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources));
    }

    [Fact]
    public void EveryBrowserRoute_IsPublishedWithItsAction_AtTheCluster()
    {
        OperationManifest m = Built();

        Assert.Contains(m.Operations, o => o is { Method: "GET", Route: DnsStatusRoutes.Status, Action: DnsActions.NamesRead });
        Assert.Contains(m.Operations, o => o is { Method: "GET", Route: DnsStatusRoutes.Events, Action: DnsActions.JournalRead });
        Assert.Contains(m.Operations, o => o is { Method: "POST", Route: DnsStatusRoutes.ZoneCheck, Action: DnsActions.ZoneCheck });
        Assert.Contains(m.Operations, o => o is { Method: "POST", Route: DnsStatusRoutes.Aliases, Action: DnsActions.AliasesWrite });
        Assert.All(m.Operations, o => Assert.Equal(Operation.Scopes.Cluster, o.Scope));
    }

    [Fact]
    public void TheDocumentItself_AndUngatedRoutes_AreNotOperations()
    {
        Assert.DoesNotContain(Built().Operations, o => o.Route == DnsOperations.Path);
    }
}
