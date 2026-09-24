using System.Globalization;

using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// The cluster sessions somebody ended before they expired, as this anchor has been told.
/// </summary>
/// <remarks>
/// <para>
/// <b>This anchor mints no session</b>, so every session it sees was signed elsewhere and verifies against
/// the key the holder of <c>auth</c> publishes. A signature stays valid until the token expires; a
/// sign-out elsewhere is announced over the bus, and this list is what turns that announcement into a
/// refusal here. Kept on disk so a restart does not accept again a bearer that was already ended.
/// </para>
/// <para>
/// A person switched off entirely is refused by the account copy instead, on their next request.
/// </para>
/// </remarks>
internal sealed class EndedSessions : IClusterSessionDenyList
{
    private readonly string _connectionString;
    private readonly Lock _writeGate = new();

    public EndedSessions(AnchorOptions options)
    {
        string full = Path.GetFullPath(options.SessionsPath);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();

        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS ended_sessions (
                sid   TEXT PRIMARY KEY,
                until TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public Task<bool> IsRevokedAsync(string sessionId, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM ended_sessions WHERE sid = $sid AND until > $now";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$now", Spell(DateTimeOffset.UtcNow));
        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    /// <inheritdoc />
    public Task RecordRevocationAsync(string sessionId, DateTimeOffset until, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO ended_sessions (sid, until) VALUES ($sid, $until)
                ON CONFLICT(sid) DO UPDATE SET until = MAX(until, excluded.until);
                DELETE FROM ended_sessions WHERE until <= $now;
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$until", Spell(until));
            cmd.Parameters.AddWithValue("$now", Spell(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string Spell(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
