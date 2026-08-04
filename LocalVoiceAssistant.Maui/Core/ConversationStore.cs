using Microsoft.Data.Sqlite;

namespace LocalVoiceAssistant.Maui.Core;

public interface IConversationStore
{
    Task AppendAsync(ConversationEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationEntry>> GetRecentAsync(int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationEntry>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteConversationStore(string? databasePath = null)
    {
        var root = databasePath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalVoiceAssistant",
                "lva")
            : Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(root))
            Directory.CreateDirectory(root);

        var path = databasePath ?? Path.Combine(root!, "conversation-history.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task AppendAsync(ConversationEntry entry, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages (id, role, text, timestamp, plugin_id, is_error)
                VALUES ($id, $role, $text, $timestamp, $plugin_id, $is_error);
                """;
            AddEntryParameters(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var ftsCommand = connection.CreateCommand())
        {
            ftsCommand.Transaction = transaction;
            ftsCommand.CommandText = "INSERT INTO messages_fts (id, text) VALUES ($id, $text);";
            ftsCommand.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
            ftsCommand.Parameters.AddWithValue("$text", entry.Text);
            await ftsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, text, timestamp, plugin_id, is_error
            FROM messages
            ORDER BY timestamp DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", Math.Max(0, count));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationEntry>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.role, m.text, m.timestamp, m.plugin_id, m.is_error
            FROM messages_fts f
            JOIN messages m ON m.id = f.id
            WHERE messages_fts MATCH $query
            ORDER BY m.timestamp DESC;
            """;
        command.Parameters.AddWithValue("$query", $"\"{query.Replace("\"", "\"\"")}\"");
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS messages (
                    id TEXT PRIMARY KEY,
                    role TEXT NOT NULL,
                    text TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    plugin_id TEXT NULL,
                    is_error INTEGER NOT NULL
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                    id UNINDEXED,
                    text
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddEntryParameters(SqliteCommand command, ConversationEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
        command.Parameters.AddWithValue("$role", entry.Role);
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$plugin_id", (object?)entry.PluginId ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_error", entry.IsError ? 1 : 0);
    }

    private static async Task<IReadOnlyList<ConversationEntry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<ConversationEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ConversationEntry(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) != 0));
        }

        return result;
    }
}
