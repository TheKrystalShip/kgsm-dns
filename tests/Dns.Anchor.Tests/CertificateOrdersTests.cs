using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Dns.Anchor.Issuance;
using TheKrystalShip.KGSM.Dns.Serving;
using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class CertificateOrdersTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kgsm-dns-orders-{Guid.NewGuid():N}", "names.db");

    public CertificateOrdersTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        string? directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void An_issued_order_reports_when_it_will_be_renewed()
    {
        var orders = new CertificateOrders(_path);
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        DateTimeOffset notBefore = requestedAt;
        DateTimeOffset notAfter = notBefore.AddDays(90);

        orders.Issued("walter.nodes.example.com", "walter", requestedAt, notBefore, notAfter);

        DnsCertificateOrder order = Assert.Single(orders.Latest());
        Assert.Equal(HeldCertificate.RenewsFrom(notBefore, notAfter), order.RenewsFrom);
    }

    [Fact]
    public void A_failed_order_reports_no_renewal_time()
    {
        var orders = new CertificateOrders(_path);

        orders.Failed("walter.nodes.example.com", "walter", DateTimeOffset.UtcNow, "the issuer refused");

        DnsCertificateOrder order = Assert.Single(orders.Latest());
        Assert.Null(order.RenewsFrom);
        Assert.Equal(1, orders.FailedSince(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(0, orders.IssuedSince(DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Fact]
    public void A_store_from_before_not_before_was_recorded_opens_and_reports_it_null()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate,
               }.ToString()))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE certificate_orders (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    name         TEXT NOT NULL,
                    member       TEXT NOT NULL,
                    requested_at TEXT NOT NULL,
                    finished_at  TEXT NOT NULL,
                    outcome      TEXT NOT NULL,
                    not_after    TEXT,
                    failure      TEXT
                );
                INSERT INTO certificate_orders (name, member, requested_at, finished_at, outcome, not_after, failure)
                VALUES ('walter.nodes.example.com', 'walter', '2020-01-01T00:00:00Z', '2020-01-01T00:00:00Z',
                        'issued', '2020-04-01T00:00:00Z', NULL);
                """;
            create.ExecuteNonQuery();
        }

        var orders = new CertificateOrders(_path);

        DnsCertificateOrder order = Assert.Single(orders.Latest());
        Assert.NotNull(order.NotAfter);
        Assert.Null(order.RenewsFrom);

        // The column now exists, and a row written from here on fills it in.
        orders.Issued("jessie.nodes.example.com", "jessie", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        DnsCertificateOrder fresh = orders.Latest().Single(o => o.Name == "jessie.nodes.example.com");
        Assert.NotNull(fresh.RenewsFrom);
    }
}
