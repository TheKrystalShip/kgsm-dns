using System.Globalization;

using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor.Registry;

/// <summary>
/// Every name this cluster has claimed, and where each member said it is reached, on the holder's own
/// SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// The registry is the authority for what the zone should say, and the provider is reconciled to it.
/// It is also recoverable: every name is derivable from the marks on the records themselves, so a
/// member that takes the capability rebuilds it rather than inheriting a file. The stated hosts and the
/// ports each game server declares are not in the zone and are not recovered; every member restates
/// them when the capability moves.
/// </para>
/// <para>
/// SQLite is single-writer, so writes serialise through a process lock while WAL keeps reads
/// concurrent. Timestamps are ISO-8601 round-trip strings, which sort lexicographically in the order
/// they sort chronologically.
/// </para>
/// </remarks>
public sealed class SqliteNameRegistry
{
    private readonly string _connectionString;
    private readonly Lock _writeGate = new();

    /// <summary>Open, or create, the registry at one path.</summary>
    public SqliteNameRegistry(string databasePath)
    {
        string full = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS names (
                name       TEXT PRIMARY KEY,
                kind       TEXT NOT NULL,
                member     TEXT NOT NULL,
                key        TEXT NOT NULL,
                type       TEXT NOT NULL,
                content    TEXT NOT NULL,
                state      TEXT NOT NULL,
                record_id  TEXT NOT NULL,
                note       TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_names_member ON names (member);
            CREATE INDEX IF NOT EXISTS idx_names_kind ON names (kind);
            CREATE TABLE IF NOT EXISTS game_ports (
                name       TEXT PRIMARY KEY,
                ports      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS hosts (
                member     TEXT PRIMARY KEY,
                host       TEXT NOT NULL,
                stated_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS departures (
                member      TEXT PRIMARY KEY,
                incarnation INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row, in name order.</summary>
    public IReadOnlyList<NameRow> All()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM names ORDER BY name";
        return Read(cmd);
    }

    /// <summary>Every row belonging to one member.</summary>
    public IReadOnlyList<NameRow> ForMember(string member)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM names WHERE member = $member ORDER BY name";
        cmd.Parameters.AddWithValue("$member", member);
        return Read(cmd);
    }

    /// <summary>Every row of one kind.</summary>
    public IReadOnlyList<NameRow> OfKind(NameKind kind)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM names WHERE kind = $kind ORDER BY name";
        cmd.Parameters.AddWithValue("$kind", NameKinds.Spell(kind));
        return Read(cmd);
    }

    /// <summary>One row by its name, or <see langword="null"/>.</summary>
    public NameRow? Find(string name)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM names WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return Read(cmd).FirstOrDefault();
    }

    /// <summary>
    /// The row holding one member's name of a kind, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A member holds at most one name of each (kind, key) pair, which is what makes a name sticky: the
    /// claim for a member that already has one finds it here and re-uses it rather than drawing again.
    /// </remarks>
    public NameRow? FindFor(NameKind kind, string member, string key)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT " + Columns + " FROM names WHERE kind = $kind AND member = $member AND key = $key";
        cmd.Parameters.AddWithValue("$kind", NameKinds.Spell(kind));
        cmd.Parameters.AddWithValue("$member", member);
        cmd.Parameters.AddWithValue("$key", key);
        return Read(cmd).FirstOrDefault();
    }

    /// <summary>Write one row, replacing whatever the name held.</summary>
    public void Put(NameRow row)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO names (name, kind, member, key, type, content, state, record_id, note, updated_at)
                VALUES ($name, $kind, $member, $key, $type, $content, $state, $record_id, $note, $updated_at)
                ON CONFLICT(name) DO UPDATE SET
                    kind = excluded.kind,
                    member = excluded.member,
                    key = excluded.key,
                    type = excluded.type,
                    content = excluded.content,
                    state = excluded.state,
                    record_id = excluded.record_id,
                    note = excluded.note,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$name", row.Name);
            cmd.Parameters.AddWithValue("$kind", NameKinds.Spell(row.Kind));
            cmd.Parameters.AddWithValue("$member", row.Member);
            cmd.Parameters.AddWithValue("$key", row.Key);
            cmd.Parameters.AddWithValue("$type", row.Type);
            cmd.Parameters.AddWithValue("$content", row.Content);
            cmd.Parameters.AddWithValue("$state", Spell(row.State));
            cmd.Parameters.AddWithValue("$record_id", row.RecordId);
            cmd.Parameters.AddWithValue("$note", row.Note);
            cmd.Parameters.AddWithValue(
                "$updated_at", row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Remove one row. A name the registry does not hold is already in the wanted state.</summary>
    public void Remove(string name)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM names WHERE name = $name; DELETE FROM game_ports WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Record the ports a game server declares, as its node last said.</summary>
    /// <remarks>
    /// Not in the zone and not recovered from it, like the stated hosts: a node restates its servers when
    /// the capability moves and on its own refresh.
    /// </remarks>
    public void SetPorts(string name, IReadOnlyList<DnsPort> ports)
    {
        string text = string.Join(';', ports.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.Start}-{p.End}/{p.Protocol}")));

        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO game_ports (name, ports) VALUES ($name, $ports)
                ON CONFLICT(name) DO UPDATE SET ports = excluded.ports
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ports", text);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every game name's declared ports.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DnsPort>> AllPorts()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, ports FROM game_ports";
        var result = new Dictionary<string, IReadOnlyList<DnsPort>>(StringComparer.Ordinal);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = ParsePorts(reader.GetString(1));
        return result;
    }

    private static List<DnsPort> ParsePorts(string text)
    {
        var ports = new List<DnsPort>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = part.IndexOf('/');
            int dash = part.IndexOf('-');
            if (slash < 0 || dash < 0 || dash > slash)
                continue;
            if (int.TryParse(part[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start)
                && int.TryParse(part[(dash + 1)..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
            {
                ports.Add(new DnsPort(start, end, part[(slash + 1)..]));
            }
        }

        return ports;
    }

    /// <summary>
    /// Record where a member last said it is reached. Empty is a statement too: the member has no
    /// public host.
    /// </summary>
    public void StateHost(string member, string host)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO hosts (member, host, stated_at) VALUES ($member, $host, $stated_at)
                ON CONFLICT(member) DO UPDATE SET host = excluded.host, stated_at = excluded.stated_at
                """;
            cmd.Parameters.AddWithValue("$member", member);
            cmd.Parameters.AddWithValue("$host", host);
            cmd.Parameters.AddWithValue(
                "$stated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The removal of a member whose names were released, as the incarnation its <c>left</c> row carried,
    /// or <see langword="null"/> when none has been acted on.
    /// </summary>
    public long? DepartureActedOn(string member)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT incarnation FROM departures WHERE member = $member";
        cmd.Parameters.AddWithValue("$member", member);
        return cmd.ExecuteScalar() is long incarnation ? incarnation : null;
    }

    /// <summary>Record that one removal of a member has been acted on.</summary>
    public void ActOnDeparture(string member, long incarnation)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO departures (member, incarnation) VALUES ($member, $incarnation)
                ON CONFLICT(member) DO UPDATE SET incarnation = excluded.incarnation
                """;
            cmd.Parameters.AddWithValue("$member", member);
            cmd.Parameters.AddWithValue("$incarnation", incarnation);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Forget where a member said it is reached, so it reads as never having said.
    /// </summary>
    /// <returns>Whether there was a statement to forget.</returns>
    public bool ForgetHost(string member)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM hosts WHERE member = $member";
            cmd.Parameters.AddWithValue("$member", member);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Where a member last said it is reached, or <see langword="null"/> when it never has.
    /// </summary>
    /// <remarks>
    /// Never said and said none are different answers. A member that has not spoken to this holder yet
    /// — which is every member for a moment after the capability moves — is not a member with no host.
    /// </remarks>
    public string? HostOf(string member)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT host FROM hosts WHERE member = $member";
        cmd.Parameters.AddWithValue("$member", member);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Every member that has said where it is reached.</summary>
    public IReadOnlyList<string> StatedMembers()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT member FROM hosts ORDER BY member";
        var members = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            members.Add(reader.GetString(0));
        return members;
    }

    /// <summary>Every member's statement of where it is reached, and when it last made it.</summary>
    public IReadOnlyList<(string Member, string Host, DateTimeOffset StatedAt)> StatedHosts()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT member, host, stated_at FROM hosts ORDER BY member";
        var hosts = new List<(string, string, DateTimeOffset)>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hosts.Add((reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(
                reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return hosts;
    }

    /// <summary>Whether any row holds this name.</summary>
    public bool Holds(string name) => Find(name) is not null;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private const string Columns =
        "name, kind, member, key, type, content, state, record_id, note, updated_at";

    private static List<NameRow> Read(SqliteCommand cmd)
    {
        var rows = new List<NameRow>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NameRow(
                Name: reader.GetString(0),
                Kind: NameKinds.Parse(reader.GetString(1)) ?? NameKind.Member,
                Member: reader.GetString(2),
                Key: reader.GetString(3),
                Type: reader.GetString(4),
                Content: reader.GetString(5),
                State: ParseState(reader.GetString(6)),
                RecordId: reader.GetString(7),
                Note: reader.GetString(8),
                UpdatedAt: DateTimeOffset.Parse(
                    reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return rows;
    }

    private static string Spell(NameState state) => state switch
    {
        NameState.Published => "published",
        NameState.Blocked => "blocked",
        _ => "pending",
    };

    private static NameState ParseState(string spelling) => spelling switch
    {
        "published" => NameState.Published,
        "blocked" => NameState.Blocked,
        _ => NameState.Pending,
    };
}
