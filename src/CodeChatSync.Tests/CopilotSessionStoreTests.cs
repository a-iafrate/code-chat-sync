using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers adding restored chats to Visual Studio's own chat index, which is what decides
/// whether a restored session is listed at all.
/// </summary>
public sealed class CopilotSessionStoreTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _databasePath;

    public CopilotSessionStoreTests()
    {
        _databasePath = _root.Combine("session-store.db");
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Register_InsertsARowBuiltFromTheSessionDescriptor()
    {
        CreateDatabase();
        var session = Session("11111111-1111-1111-1111-111111111111");

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        Assert.Null(registration.Reason);
        var row = Assert.Single(ReadSessions());
        Assert.Equal(session.Id, row["id"]);
        Assert.Equal(@"C:\projects\demo\", row["cwd"]);
        Assert.Equal("example/demo", row["repository"]);
        Assert.Equal("github", row["host_type"]);
        Assert.Equal("main", row["branch"]);
        Assert.Equal("Restored chat", row["summary"]);
        Assert.Equal("2026-09-29T09:00:40.457Z", row["created_at"]);
        Assert.Equal("2026-09-30T13:47:26.298Z", row["updated_at"]);
    }

    [Fact]
    public void Register_SkipsSessionsVisualStudioAlreadyLists()
    {
        CreateDatabase();
        var known = Session("22222222-2222-2222-2222-222222222222");
        var store = Store();
        store.Register([known], dryRun: false);

        var registration = store.Register([known, Session("33333333-3333-3333-3333-333333333333")], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        Assert.Equal(2, ReadSessions().Count);
    }

    [Fact]
    public void Register_RunAgainWithNothingNewChangesNothing()
    {
        CreateDatabase();
        var session = Session("44444444-4444-4444-4444-444444444444");
        var store = Store();
        store.Register([session], dryRun: false);
        var backupsAfterFirstRun = CountBackups();

        var registration = store.Register([session], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        Assert.Single(ReadSessions());
        Assert.Equal(backupsAfterFirstRun, CountBackups());
    }

    [Fact]
    public void Register_BacksUpTheIndexBeforeChangingIt()
    {
        CreateDatabase();

        Store().Register([Session("55555555-5555-5555-5555-555555555555")], dryRun: false);

        var backup = Assert.Single(Directory.GetDirectories(BackupRoot()));
        Assert.True(File.Exists(Path.Combine(backup, "session-store.db")));
    }

    [Fact]
    public void Register_DryRunReportsWithoutWritingOrBackingUp()
    {
        CreateDatabase();

        var registration = Store().Register([Session("66666666-6666-6666-6666-666666666666")], dryRun: true);

        Assert.Equal(1, registration.RegisteredCount);
        Assert.Empty(ReadSessions());
        Assert.False(Directory.Exists(BackupRoot()));
    }

    [Fact]
    public void Register_ExplainsItselfWhenVisualStudioHasNoIndexYet()
    {
        var registration = Store().Register([Session("77777777-7777-7777-7777-777777777777")], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        Assert.NotNull(registration.Reason);
    }

    /// <summary>
    /// The schema is undocumented, so a Visual Studio update that drops a column must
    /// degrade to a partial row rather than a failed sync.
    /// </summary>
    [Fact]
    public void Register_WritesOnlyTheColumnsTheSchemaStillHas()
    {
        CreateDatabase("CREATE TABLE sessions (id TEXT PRIMARY KEY, cwd TEXT, branch TEXT)");

        var registration = Store().Register([Session("88888888-8888-8888-8888-888888888888")], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        var row = Assert.Single(ReadSessions());
        Assert.Equal(@"C:\projects\demo\", row["cwd"]);
        Assert.Equal("main", row["branch"]);
    }

    [Fact]
    public void Register_LeavesAnUnrecognizedIndexAloneAndSaysWhy()
    {
        CreateDatabase("CREATE TABLE unrelated (value TEXT)");

        var registration = Store().Register([Session("99999999-9999-9999-9999-999999999999")], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        Assert.NotNull(registration.Reason);
    }

    [Fact]
    public void GetDefaultPath_PutsTheIndexBesideTheSessionFolders()
    {
        var path = CopilotSessionStore.GetDefaultPath(@"C:\data\CopilotCli\session-state");

        Assert.Equal(Path.Combine(@"C:\data\CopilotCli", "session-store.db"), path);
    }

    /// <summary>
    /// The chat list is rendered from the exchanges: a session row on its own leaves the
    /// chat invisible, which is exactly what twelve restored chats did.
    /// </summary>
    [Fact]
    public void Register_WritesTheExchangesTheChatListRenders()
    {
        CreateDatabase();
        var session = SessionWithTranscript("aaaaaaaa-1111-1111-1111-111111111111");

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        var turns = ReadTurns();
        Assert.Collection(
            turns,
            turn => Assert.Equal(("asked first", "answered first", 0), turn),
            turn => Assert.Equal(("asked second", "answered second", 1), turn));
    }

    /// <summary>
    /// The state left by an earlier version of the tool: the row is there, the exchanges
    /// are not, and no file needs copying, so only this step can still fix it.
    /// </summary>
    [Fact]
    public void Register_FillsInExchangesForASessionAlreadyInTheIndex()
    {
        CreateDatabase();
        var session = SessionWithTranscript("bbbbbbbb-2222-2222-2222-222222222222");
        Execute("INSERT INTO sessions (id, cwd) VALUES ($id, 'C:\\projects\\demo\\')", session.Id);

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        Assert.Equal(2, ReadTurns().Count);
        Assert.Single(ReadSessions());
    }

    [Fact]
    public void Register_LeavesAChatVisualStudioHasAlreadyRecordedAlone()
    {
        CreateDatabase();
        var session = SessionWithTranscript("cccccccc-3333-3333-3333-333333333333");
        Execute("INSERT INTO sessions (id, cwd) VALUES ($id, 'C:\\projects\\demo\\')", session.Id);
        Execute(
            "INSERT INTO turns (session_id, turn_index, user_message, assistant_response) "
            + "VALUES ($id, 0, 'written by the tool', 'leave me alone')",
            session.Id);

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        var turn = Assert.Single(ReadTurns());
        Assert.Equal(("written by the tool", "leave me alone", 0), turn);
    }

    [Fact]
    public void Register_RunAgainDoesNotDuplicateExchanges()
    {
        CreateDatabase();
        var session = SessionWithTranscript("dddddddd-4444-4444-4444-444444444444");
        var store = Store();
        store.Register([session], dryRun: false);

        var registration = store.Register([session], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        Assert.Equal(2, ReadTurns().Count);
    }

    /// <summary>
    /// A chat renamed on another PC is already listed here with its old name: the new one,
    /// which only the user chooses, is carried onto the existing row and nothing else.
    /// </summary>
    [Fact]
    public void Register_CarriesAUserChosenNameOntoAnExistingRow()
    {
        CreateDatabase();
        var session = SessionWithTranscript("eeeeeeee-5555-5555-5555-555555555555") with
        {
            Name = "Chosen name",
            IsUserNamed = true
        };
        Execute("INSERT INTO sessions (id, cwd, summary) VALUES ($id, 'C:\\elsewhere\\', 'Old name')", session.Id);
        Execute(
            "INSERT INTO turns (session_id, turn_index, user_message, assistant_response) VALUES ($id, 0, 'q', 'a')",
            session.Id);

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(1, registration.RegisteredCount);
        var row = Assert.Single(ReadSessions());
        Assert.Equal("Chosen name", row["summary"]);
        Assert.Equal("C:\\elsewhere\\", row["cwd"]);
        Assert.Equal(1, CountBackups());
    }

    [Fact]
    public void Register_LeavesAGeneratedNameOnAnExistingRowAlone()
    {
        CreateDatabase();
        var session = SessionWithTranscript("ffffffff-6666-6666-6666-666666666666") with
        {
            Name = "Generated",
            IsUserNamed = false
        };
        Execute("INSERT INTO sessions (id, summary) VALUES ($id, 'Visual Studio wrote this')", session.Id);
        Execute(
            "INSERT INTO turns (session_id, turn_index, user_message, assistant_response) VALUES ($id, 0, 'q', 'a')",
            session.Id);

        var registration = Store().Register([session], dryRun: false);

        Assert.Equal(0, registration.RegisteredCount);
        Assert.Equal("Visual Studio wrote this", Assert.Single(ReadSessions())["summary"]);
    }

    private CopilotSessionStore Store() => new(_databasePath, BackupRoot());

    private CopilotChatSession SessionWithTranscript(string id)
    {
        var directory = _root.Combine("sessions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            Path.Combine(directory, "events.jsonl"),
            [
                """{"type":"user.message","data":{"content":"asked first"},"timestamp":"2026-09-29T09:00:01.000Z"}""",
                """{"type":"assistant.message","data":{"content":"answered first"},"timestamp":"2026-09-29T09:00:02.000Z"}""",
                """{"type":"user.message","data":{"content":"asked second"},"timestamp":"2026-09-29T09:01:01.000Z"}""",
                """{"type":"assistant.message","data":{"content":"answered second"},"timestamp":"2026-09-29T09:01:02.000Z"}"""
            ]);

        return Session(id) with { SessionDirectory = directory };
    }

    private void Execute(string sql, string id)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private List<(string? Ask, string? Answer, long Index)> ReadTurns()
    {
        var turns = new List<(string?, string?, long)>();
        using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_message, assistant_response, turn_index FROM turns ORDER BY turn_index";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            turns.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2)));
        }

        return turns;
    }

    private string BackupRoot() => _root.Combine("backups");

    private int CountBackups() =>
        Directory.Exists(BackupRoot()) ? Directory.GetDirectories(BackupRoot()).Length : 0;

    private void CreateDatabase(string? schema = null)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema ?? """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT,
                repository TEXT,
                host_type TEXT,
                branch TEXT,
                summary TEXT,
                created_at TEXT DEFAULT (datetime('now')),
                updated_at TEXT DEFAULT (datetime('now'))
            );
            CREATE TABLE turns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES sessions(id),
                turn_index INTEGER NOT NULL,
                user_message TEXT,
                assistant_response TEXT,
                timestamp TEXT DEFAULT (datetime('now')),
                UNIQUE(session_id, turn_index)
            );
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private List<Dictionary<string, string?>> ReadSessions()
    {
        var rows = new List<Dictionary<string, string?>>();
        using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetString(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static CopilotChatSession Session(string id) => new()
    {
        Id = id,
        SessionDirectory = Path.Combine(@"C:\sessions", id),
        WorkingDirectory = @"C:\projects\demo\",
        Repository = "example/demo",
        HostType = "github",
        Branch = "main",
        Name = "Restored chat",
        CreatedAt = DateTimeOffset.Parse("2026-09-29T09:00:40.457Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-09-30T13:47:26.298Z"),
        Files = []
    };
}
