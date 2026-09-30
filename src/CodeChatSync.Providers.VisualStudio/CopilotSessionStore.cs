using System.Globalization;
using CodeChatSync.Core;
using Microsoft.Data.Sqlite;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>Outcome of announcing sessions to Visual Studio's chat index.</summary>
public sealed record CopilotSessionRegistration(int RegisteredCount, string? Reason = null);

/// <summary>
/// Adds restored sessions to <c>session-store.db</c>, the index Visual Studio keeps of
/// the Copilot chats on this PC.
/// </summary>
/// <remarks>
/// <para>
/// Copying a session folder is not enough to make its chat appear. Visual Studio builds
/// its chat list from this database: a row in <c>sessions</c> identifies the chat, and
/// the rows in <c>turns</c> are what the list actually renders — the entry's title is the
/// user's message and its subtitle the assistant's reply. A session with a
/// <c>sessions</c> row but no turns stays invisible, which was observed directly: twelve
/// restored sessions registered that way remained absent from the list, while a chat
/// created locally appeared as soon as Visual Studio wrote its single turn.
/// </para>
/// <para>
/// Both are therefore inserted, built entirely from the session's own files:
/// <c>workspace.yaml</c> for the row, <c>events.jsonl</c> for the turns (see
/// <see cref="CopilotTranscriptTurns"/>).
/// </para>
/// <para>
/// The database is <em>never</em> copied in either direction. It indexes the sessions of
/// every repository on the PC, so moving the file would carry unrelated clients' data
/// into the sync repository. Only rows for the project being synced are written, and
/// only for sessions that are already present locally.
/// </para>
/// <para>
/// The schema is undocumented (version 6 when this was written) and is treated as
/// best-effort: the columns actually present are read at runtime and only those are
/// written, so a Visual Studio update that adds or drops one degrades to a skipped
/// registration with an explanation instead of a failed sync.
/// </para>
/// </remarks>
public sealed class CopilotSessionStore
{
    /// <summary>Name of the index file, a sibling of the <c>session-state</c> folder.</summary>
    public const string FileName = "session-store.db";

    private const string SessionsTable = "sessions";
    private const string TurnsTable = "turns";
    private const string IdColumn = "id";

    /// <summary>Timestamp format Visual Studio itself writes into this database.</summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// Columns filled from the session descriptor, in the order they were observed.
    /// Any that a future schema no longer has is left out rather than guessed at.
    /// </summary>
    private static readonly (string Column, Func<CopilotChatSession, string?> Read)[] Mapping =
    [
        ("cwd", session => session.WorkingDirectory),
        ("repository", session => session.Repository),
        ("host_type", session => session.HostType),
        ("branch", session => session.Branch),
        ("summary", session => session.Name),
        ("created_at", session => Format(session.CreatedAt)),
        ("updated_at", session => Format(session.UpdatedAt))
    ];

    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public CopilotSessionStore(string databasePath, string? backupDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = backupDirectory ?? Path.Combine(LocalConfig.GetDefaultBackupDirectory(), "visualstudio");
    }

    /// <summary>Index used by the sessions under <paramref name="sessionStateRoot"/>.</summary>
    public static string GetDefaultPath(string sessionStateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionStateRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(sessionStateRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return parent is { Length: > 0 }
            ? Path.Combine(parent, FileName)
            : Path.Combine(sessionStateRoot, FileName);
    }

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Inserts a row for every session Visual Studio does not already know about.
    /// Sessions it already lists are left untouched, so this is safe to run on every sync.
    /// </summary>
    public CopilotSessionRegistration Register(IReadOnlyCollection<CopilotChatSession> sessions, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return new CopilotSessionRegistration(0);
        }

        if (!File.Exists(_databasePath))
        {
            return new CopilotSessionRegistration(
                0,
                "Visual Studio has not created its chat index on this PC yet; it will pick these chats up once it has.");
        }

        try
        {
            // Read-only first: opening for writing lets SQLite fold the write-ahead log
            // into the main file, which would change the index before it has been copied
            // aside. Nothing here touches it until there is something to add.
            HashSet<string> columns;
            HashSet<string> known;
            HashSet<string> alreadyListed;
            using (var connection = Open(SqliteOpenMode.ReadOnly))
            {
                columns = ReadColumns(connection, SessionsTable);
                known = columns.Contains(IdColumn) ? ReadIds(connection, SessionsTable, IdColumn) : [];
                alreadyListed = ReadIds(connection, TurnsTable, "session_id");
            }

            if (!columns.Contains(IdColumn))
            {
                return new CopilotSessionRegistration(
                    0,
                    "Visual Studio's chat index does not have the expected layout on this PC, so it was left untouched.");
            }

            // A session Visual Studio has already recorded a turn for is left completely
            // alone: it is listed, and its exchanges are the tool's to manage.
            var pending = sessions
                .Where(session => session.Id is { Length: > 0 } && !alreadyListed.Contains(session.Id))
                .Select(session => (Session: session, NeedsRow: !known.Contains(session.Id), Turns: ReadTurns(session)))
                .Where(candidate => candidate.NeedsRow || candidate.Turns.Count > 0)
                .ToArray();

            if (pending.Length == 0 || dryRun)
            {
                return new CopilotSessionRegistration(pending.Length);
            }

            BackUp();

            using var writeConnection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = writeConnection.BeginTransaction();
            var written = 0;
            foreach (var (session, needsRow, turns) in pending)
            {
                if (needsRow)
                {
                    Insert(writeConnection, transaction, columns, session);
                }

                InsertTurns(writeConnection, transaction, session.Id, turns);
                written++;
            }

            transaction.Commit();

            return new CopilotSessionRegistration(written);
        }
        catch (SqliteException exception)
        {
            return new CopilotSessionRegistration(
                0,
                $"Visual Studio's chat index could not be updated, so the restored chats stay hidden for now: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CopilotSessionRegistration(
                0,
                $"Visual Studio's chat index could not be updated, so the restored chats stay hidden for now: {exception.Message}");
        }
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = mode,
            Pooling = false
        }.ToString());

        connection.Open();
        return connection;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    /// <summary>Distinct values of one column, or none when the table is not there.</summary>
    private static HashSet<string> ReadIds(SqliteConnection connection, string table, string column)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {column} FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    ids.Add(reader.GetString(0));
                }
            }
        }
        catch (SqliteException)
        {
            // An absent table means nothing is recorded there yet.
        }

        return ids;
    }

    private static IReadOnlyList<CopilotChatTurn> ReadTurns(CopilotChatSession session) =>
        CopilotTranscriptTurns.Read(
            Path.Combine(session.SessionDirectory, CopilotChatDiscovery.GetTranscriptRelativePath()));

    /// <summary>
    /// Writes the exchanges the chat list renders. Skipped silently when the schema no
    /// longer matches, since the session row alone is still worth having.
    /// </summary>
    private static void InsertTurns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        IReadOnlyList<CopilotChatTurn> turns)
    {
        if (turns.Count == 0)
        {
            return;
        }

        var columns = ReadColumns(connection, TurnsTable);
        string[] required = ["session_id", "turn_index", "user_message", "assistant_response", "timestamp"];
        if (!required.All(columns.Contains))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {TurnsTable} (session_id, turn_index, user_message, assistant_response, timestamp) "
            + "VALUES ($session, $index, $user, $assistant, $timestamp) "
            + "ON CONFLICT(session_id, turn_index) DO NOTHING";

        var session = command.Parameters.Add("$session", SqliteType.Text);
        var index = command.Parameters.Add("$index", SqliteType.Integer);
        var user = command.Parameters.Add("$user", SqliteType.Text);
        var assistant = command.Parameters.Add("$assistant", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        session.Value = sessionId;

        foreach (var turn in turns)
        {
            index.Value = turn.Index;
            user.Value = turn.UserMessage;
            assistant.Value = (object?)turn.AssistantResponse ?? DBNull.Value;
            timestamp.Value = (object?)turn.Timestamp ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static bool Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> columns,
        CopilotChatSession session)
    {
        var used = Mapping.Where(entry => columns.Contains(entry.Column)).ToArray();
        var names = string.Join(", ", used.Select(entry => entry.Column).Prepend(IdColumn));
        var parameters = string.Join(", ", used.Select(entry => "$" + entry.Column).Prepend("$" + IdColumn));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // A row added by Visual Studio between the read and the write wins: the chat is
        // then already listed and there is nothing to add.
        command.CommandText =
            $"INSERT INTO {SessionsTable} ({names}) VALUES ({parameters}) ON CONFLICT({IdColumn}) DO NOTHING";
        command.Parameters.AddWithValue("$" + IdColumn, session.Id);
        foreach (var (column, read) in used)
        {
            command.Parameters.AddWithValue("$" + column, (object?)read(session) ?? DBNull.Value);
        }

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Copies the index aside before it is changed, including the write-ahead log, which
    /// holds everything not yet folded into the main file.
    /// </summary>
    private void BackUp()
    {
        var directory = Path.Combine(
            _backupDirectory,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        foreach (var suffix in (string[])["", "-wal", "-shm"])
        {
            var source = _databasePath + suffix;
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: false);
            }
        }
    }

    private static string? Format(DateTimeOffset? timestamp) =>
        timestamp?.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
