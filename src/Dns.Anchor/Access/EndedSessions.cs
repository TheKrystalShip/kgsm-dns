using System.Globalization;

using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Sessions;

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
/// Ending "every session of one person" names sessions a member minted itself, which is none here: a
/// person switched off entirely is refused by the account copy instead, on their next request.
/// </para>
/// </remarks>
internal sealed class EndedSessions : IClusterSessionAuthority
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

    /// <inheritdoc />
    /// <remarks>A session this anchor did not mint has no row of its own to end; the record above does it.</remarks>
    public Task RevokeAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> RevokeAllForHandleAsync(string handle, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string Spell(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// The session validator the shared revoke handler evicts from — which, on a member that mints no
/// sessions, has nothing cached to evict and no session of its own to call valid.
/// </summary>
internal sealed class NoLocalSessions : ISessionValidator
{
    /// <inheritdoc />
    public Task<bool> IsValidAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public void Evict(string sessionId)
    {
    }
}
