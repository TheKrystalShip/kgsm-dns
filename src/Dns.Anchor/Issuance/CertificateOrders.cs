using System.Globalization;

using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Dns.Serving;
using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Issuance;

/// <summary>
/// Every certificate order this anchor finished, on the registry's own file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The anchor is the only member that sees every order.</b> A member knows its own certificates; only
/// the holder knows what it asked the issuer for across the whole cluster, which is what the issuer's
/// weekly limit is counted against, and what a failed order leaves no other trace of.
/// </para>
/// <para>
/// <b>An order is recorded when it ends</b>, issued or failed. One waiting or in flight is the queue's to
/// report and lives only as long as the process does, like the queue.
/// </para>
/// <para>
/// Rows older than <see cref="Kept"/> are dropped as new ones arrive. A name renews long before then, so
/// its newest order is always inside the window.
/// </para>
/// </remarks>
internal sealed class CertificateOrders
{
    /// <summary>How long a finished order is kept.</summary>
    internal static readonly TimeSpan Kept = TimeSpan.FromDays(120);

    private readonly string _connectionString;
    private readonly Lock _writeGate = new();

    public CertificateOrders(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();

        using SqliteConnection connection = Open();
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS certificate_orders (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    name         TEXT NOT NULL,
                    member       TEXT NOT NULL,
                    requested_at TEXT NOT NULL,
                    finished_at  TEXT NOT NULL,
                    outcome      TEXT NOT NULL,
                    not_after    TEXT,
                    failure      TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_certificate_orders_name ON certificate_orders (name, finished_at);
                """;
            cmd.ExecuteNonQuery();
        }

        // A store from before an order recorded when its certificate takes effect has no column for it.
        // Every row written from here on fills it in; a row from before still reports RenewsFrom null
        // rather than guessing when its lifetime started.
        if (!HasColumn(connection, "not_before"))
        {
            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE certificate_orders ADD COLUMN not_before TEXT";
            alter.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection connection, string column)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(certificate_orders)";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Record an order that produced a certificate.</summary>
    public void Issued(
        string name, string member, DateTimeOffset requestedAt, DateTimeOffset? notBefore, DateTimeOffset? notAfter) =>
        Finish(name, member, requestedAt, "issued", notBefore, notAfter, failure: null);

    /// <summary>Record an order that ended without one.</summary>
    public void Failed(string name, string member, DateTimeOffset requestedAt, string failure) =>
        Finish(name, member, requestedAt, "failed", notBefore: null, notAfter: null, failure);

    /// <summary>The newest finished order for each name, in name order.</summary>
    public IReadOnlyList<DnsCertificateOrder> Latest()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT name, member, outcome, requested_at, finished_at, not_before, not_after, failure
            FROM certificate_orders AS o
            WHERE id = (SELECT MAX(id) FROM certificate_orders WHERE name = o.name)
            ORDER BY name
            """;

        var orders = new List<DnsCertificateOrder>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset? notBefore = reader.IsDBNull(5) ? null : Time(reader.GetString(5));
            DateTimeOffset? notAfter = reader.IsDBNull(6) ? null : Time(reader.GetString(6));
            orders.Add(new DnsCertificateOrder(
                Name: reader.GetString(0),
                Member: reader.GetString(1),
                State: reader.GetString(2),
                RequestedAt: Time(reader.GetString(3)),
                FinishedAt: Time(reader.GetString(4)),
                NotAfter: notAfter,
                RenewsFrom: notBefore is { } from && notAfter is { } until
                    ? HeldCertificate.RenewsFrom(from, until)
                    : null,
                Failure: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return orders;
    }

    /// <summary>How many certificates were issued since one moment.</summary>
    public int IssuedSince(DateTimeOffset since) => CountSince(since, "issued");

    /// <summary>How many orders ended without a certificate since one moment.</summary>
    public int FailedSince(DateTimeOffset since) => CountSince(since, "failed");

    private int CountSince(DateTimeOffset since, string outcome)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM certificate_orders WHERE outcome = $outcome AND finished_at >= $since";
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$since", Spell(since));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Finish(
        string name, string member, DateTimeOffset requestedAt, string outcome, DateTimeOffset? notBefore,
        DateTimeOffset? notAfter, string? failure)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO certificate_orders
                    (name, member, requested_at, finished_at, outcome, not_before, not_after, failure)
                VALUES ($name, $member, $requested_at, $finished_at, $outcome, $not_before, $not_after, $failure);
                DELETE FROM certificate_orders WHERE finished_at < $oldest;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$member", member);
            cmd.Parameters.AddWithValue("$requested_at", Spell(requestedAt));
            cmd.Parameters.AddWithValue("$finished_at", Spell(now));
            cmd.Parameters.AddWithValue("$outcome", outcome);
            cmd.Parameters.AddWithValue("$not_before", notBefore is { } from ? Spell(from) : DBNull.Value);
            cmd.Parameters.AddWithValue("$not_after", notAfter is { } until ? Spell(until) : DBNull.Value);
            cmd.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$oldest", Spell(now - Kept));
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Round-trip ISO-8601 in UTC, which sorts as text in the order it sorts as time.
    private static string Spell(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string spelled) =>
        DateTimeOffset.Parse(spelled, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
