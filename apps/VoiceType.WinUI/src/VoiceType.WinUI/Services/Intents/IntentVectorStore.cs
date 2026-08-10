using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.WinUI.Services.Intents;

/// <summary>Persistent local store for normalized MiniLM intent embeddings.</summary>
public sealed class IntentVectorStore
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private static readonly nint SqliteTransient = new(-1);
    private readonly string _databasePath;

    public IntentVectorStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _databasePath = databasePath;
        Initialize();
    }

    public float[] GetOrAdd(string modelKey, string intentId, string exemplar, Func<float[]> create)
    {
        var exemplarHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exemplar)));
        using var database = OpenDatabase();
        using (var read = Prepare(database.Handle, """
            SELECT vector FROM intent_vectors
            WHERE model_key = ?1 AND intent_id = ?2 AND exemplar_hash = ?3;
            """))
        {
            BindText(read.Handle, 1, modelKey);
            BindText(read.Handle, 2, intentId);
            BindText(read.Handle, 3, exemplarHash);
            if (sqlite3_step(read.Handle) == SqliteRow)
            {
                var length = sqlite3_column_bytes(read.Handle, 0);
                var pointer = sqlite3_column_blob(read.Handle, 0);
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                return Deserialize(bytes);
            }
        }

        var embedding = create();
        using var insert = Prepare(database.Handle, """
            INSERT OR REPLACE INTO intent_vectors(model_key, intent_id, exemplar_hash, vector)
            VALUES (?1, ?2, ?3, ?4);
            """);
        BindText(insert.Handle, 1, modelKey);
        BindText(insert.Handle, 2, intentId);
        BindText(insert.Handle, 3, exemplarHash);
        BindBlob(insert.Handle, 4, Serialize(embedding));
        EnsureSuccess(sqlite3_step(insert.Handle), database.Handle);
        return embedding;
    }

    private void Initialize()
    {
        using var database = OpenDatabase();
        Execute(database.Handle, """
            CREATE TABLE IF NOT EXISTS intent_vectors (
                model_key TEXT NOT NULL,
                intent_id TEXT NOT NULL,
                exemplar_hash TEXT NOT NULL,
                vector BLOB NOT NULL,
                PRIMARY KEY (model_key, intent_id, exemplar_hash)
            );
            """);
    }

    private DatabaseHandle OpenDatabase()
    {
        EnsureSuccess(sqlite3_open16(_databasePath, out var database), database);
        return new DatabaseHandle(database);
    }

    private static StatementHandle Prepare(nint database, string sql)
    {
        EnsureSuccess(sqlite3_prepare16_v2(database, sql, -1, out var statement, nint.Zero), database);
        return new StatementHandle(statement);
    }

    private static void Execute(nint database, string sql)
    {
        EnsureSuccess(sqlite3_exec(database, sql, nint.Zero, nint.Zero, out var error), database);
        if (error != nint.Zero)
            sqlite3_free(error);
    }

    private static void BindText(nint statement, int index, string value) =>
        EnsureSuccess(sqlite3_bind_text16(statement, index, value, -1, SqliteTransient), nint.Zero);

    private static void BindBlob(nint statement, int index, byte[] value) =>
        EnsureSuccess(sqlite3_bind_blob(statement, index, value, value.Length, SqliteTransient), nint.Zero);

    private static void EnsureSuccess(int result, nint database)
    {
        if (result is SqliteOk or SqliteDone)
            return;

        var message = database == nint.Zero ? "SQLite initialization failed." : Marshal.PtrToStringUni(sqlite3_errmsg16(database));
        throw new InvalidOperationException(message ?? $"SQLite error {result}.");
    }

    private static byte[] Serialize(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] Deserialize(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException("Invalid vector payload in SQLite store.");

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private sealed class DatabaseHandle(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() => _ = sqlite3_close(Handle);
    }

    private sealed class StatementHandle(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() => _ = sqlite3_finalize(Handle);
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_open16(string filename, out nint database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sqlite3_prepare16_v2(nint database, string sql, int bytes, out nint statement, nint tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(nint statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(nint statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(nint database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text16(nint statement, int index, string value, int bytes, nint destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_blob(nint statement, int index, byte[] value, int bytes, nint destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_column_blob(nint statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(nint statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_errmsg16(nint database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int sqlite3_exec(nint database, string sql, nint callback, nint argument, out nint error);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(nint value);
}