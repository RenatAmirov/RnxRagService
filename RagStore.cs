using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace RnxRagService;

internal sealed class RagStore
{
    private const string DocumentColumns = "id, name, size, uploaded_at, chunk_count, embedding_model";
    private readonly string _connectionString;

    public RagStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 10
        }.ToString();

        using var connection = Open();
        using (var wal = Command(connection, null, "PRAGMA journal_mode = WAL"))
            wal.ExecuteScalar();

        using var transaction = connection.BeginTransaction();
        using var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL UNIQUE,
                uploaded_at INTEGER NOT NULL, chunk_count INTEGER NOT NULL, embedding_model TEXT
            );
            CREATE INDEX IF NOT EXISTS documents_embedding_model ON documents(embedding_model);
            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL, page INTEGER, content TEXT NOT NULL, search_text TEXT NOT NULL,
                embedding BLOB, UNIQUE(document_id, ordinal)
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                search_text, content='chunks', content_rowid='id', tokenize='unicode61'
            );
            CREATE TRIGGER IF NOT EXISTS chunks_insert AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts(rowid, search_text) VALUES (new.id, new.search_text);
            END;
            CREATE TRIGGER IF NOT EXISTS chunks_delete AFTER DELETE ON chunks BEGIN
                INSERT INTO chunks_fts(chunks_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
            END;
            """);
        schema.ExecuteNonQuery();
        transaction.Commit();
    }

    public List<DocumentInfo> ListDocuments()
    {
        using var connection = Open();
        using var command = Command(connection, null,
            $"SELECT {DocumentColumns} FROM documents ORDER BY uploaded_at DESC, rowid DESC");
        using var reader = command.ExecuteReader();
        var documents = new List<DocumentInfo>();
        while (reader.Read())
            documents.Add(ReadDocument(reader));
        return documents;
    }

    public DocumentInfo? GetDocument(string id)
    {
        using var connection = Open();
        return FindDocument(connection, null, "id", id);
    }

    public DocumentInfo? FindByHash(string hash)
    {
        using var connection = Open();
        return FindDocument(connection, null, "sha256", hash);
    }

    public (DocumentInfo Document, bool Created) AddDocument(
        string name, int size, string hash, List<Passage> passages, string? model, float[][]? vectors,
        CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = Guid.NewGuid().ToString("N");
        using var insert = Command(connection, transaction, """
            INSERT INTO documents (id, name, size, sha256, uploaded_at, chunk_count, embedding_model)
            VALUES ($id, $name, $size, $hash, $uploaded, $count, $model)
            ON CONFLICT (sha256) DO NOTHING
            """, ("$id", id), ("$name", name), ("$size", size), ("$hash", hash), ("$uploaded", timestamp),
            ("$count", passages.Count), ("$model", model));
        if (insert.ExecuteNonQuery() == 0)
            return (FindDocument(connection, transaction, "sha256", hash)!, false);

        using var chunk = Command(connection, transaction, """
            INSERT INTO chunks (document_id, ordinal, page, content, search_text, embedding)
            VALUES ($id, $ordinal, $page, $content, $search, $embedding)
            """, ("$id", id));
        var ordinal = chunk.Parameters.Add("$ordinal", SqliteType.Integer);
        var page = chunk.Parameters.Add("$page", SqliteType.Integer);
        var content = chunk.Parameters.Add("$content", SqliteType.Text);
        var search = chunk.Parameters.Add("$search", SqliteType.Text);
        var embedding = chunk.Parameters.Add("$embedding", SqliteType.Blob);

        for (var index = 0; index < passages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var passage = passages[index];
            ordinal.Value = passage.Number;
            page.Value = (object?)passage.Page ?? DBNull.Value;
            content.Value = passage.Text;
            search.Value = TextIndex.Normalize(passage.Text);
            embedding.Value = vectors is null ? DBNull.Value : Encode(vectors[index]);
            chunk.ExecuteNonQuery();
        }

        transaction.Commit();
        return (new DocumentInfo(id, name, size, DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            passages.Count, model), true);
    }

    public bool DeleteDocument(string id)
    {
        using var connection = Open();
        using var command = Command(connection, null, "DELETE FROM documents WHERE id = $id", ("$id", id));
        return command.ExecuteNonQuery() > 0;
    }

    public Passage[] GetPassages(string id)
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT ordinal, page, content FROM chunks WHERE document_id = $id ORDER BY ordinal", ("$id", id));
        using var reader = command.ExecuteReader();
        var passages = new List<Passage>();
        while (reader.Read())
            passages.Add(ReadPassage(reader, 0));
        return passages.ToArray();
    }

    public SearchHit? GetPassage(string id, int ordinal)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT c.id, d.id, d.name, c.ordinal, c.page, c.content
            FROM chunks c JOIN documents d ON d.id = c.document_id
            WHERE d.id = $id AND c.ordinal = $ordinal
            """, ("$id", id), ("$ordinal", ordinal));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHit(reader) : null;
    }

    public bool SetEmbeddings(string id, string model, Passage[] passages, float[][] vectors, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var document = Command(connection, transaction,
            "UPDATE documents SET embedding_model = $model WHERE id = $id", ("$model", model), ("$id", id));
        if (document.ExecuteNonQuery() == 0)
            return false;

        using var update = Command(connection, transaction, """
            UPDATE chunks SET embedding = $embedding WHERE document_id = $id AND ordinal = $ordinal
            """, ("$id", id));
        var ordinal = update.Parameters.Add("$ordinal", SqliteType.Integer);
        var embedding = update.Parameters.Add("$embedding", SqliteType.Blob);
        for (var index = 0; index < passages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal.Value = passages[index].Number;
            embedding.Value = Encode(vectors[index]);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public bool HasEmbeddings(string model, string? documentId)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT 1 FROM documents WHERE embedding_model = $model AND ($doc IS NULL OR id = $doc) LIMIT 1
            """, ("$model", model), ("$doc", documentId));
        return command.ExecuteScalar() is not null;
    }

    public List<SearchHit> Search(string[] terms, string? documentId, int limit, string? model, float[]? vector,
        double minSimilarity, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var candidates = Math.Max(20, limit * 3);
        var lexical = terms.Length == 0
            ? []
            : LexicalIds(connection, transaction, TextIndex.FtsQuery(terms), documentId, candidates);
        var semantic = vector is null
            ? []
            : SemanticIds(connection, transaction, model!, vector, documentId, candidates, minSimilarity, cancellationToken);

        var ranks = new Dictionary<long, double>();
        AddRanks(lexical, ranks);
        AddRanks(semantic, ranks);

        using var command = Command(connection, transaction, """
            SELECT c.id, d.id, d.name, c.ordinal, c.page, c.content
            FROM chunks c JOIN documents d ON d.id = c.document_id WHERE c.id = $id
            """);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var results = new List<SearchHit>();
        foreach (var (chunkId, _) in ranks.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(limit))
        {
            id.Value = chunkId;
            using var reader = command.ExecuteReader();
            if (reader.Read())
                results.Add(ReadHit(reader));
        }
        return results;
    }

    private static List<long> LexicalIds(SqliteConnection connection, SqliteTransaction transaction, string query,
        string? documentId, int limit)
    {
        using var command = Command(connection, transaction, """
            SELECT chunks_fts.rowid
            FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid
            WHERE chunks_fts MATCH $query AND ($doc IS NULL OR c.document_id = $doc)
            ORDER BY rank, chunks_fts.rowid LIMIT $limit
            """, ("$query", query), ("$doc", documentId), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static List<long> SemanticIds(SqliteConnection connection, SqliteTransaction transaction, string model,
        float[] query, string? documentId, int limit, double minSimilarity, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, """
            SELECT c.id, c.embedding FROM chunks c JOIN documents d ON d.id = c.document_id
            WHERE d.embedding_model = $model AND c.embedding IS NOT NULL AND ($doc IS NULL OR d.id = $doc)
            """, ("$model", model), ("$doc", documentId));
        using var reader = command.ExecuteReader();
        var best = new PriorityQueue<long, (double Score, long Tie)>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = (byte[])reader[1];
            if (bytes.Length != query.Length * sizeof(float))
                continue;
            double score = 0;
            for (var index = 0; index < query.Length; index++)
                score += (double)query[index] * BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
            if (score < minSimilarity)
                continue;

            var id = reader.GetInt64(0);
            best.Enqueue(id, (score, -id));
            if (best.Count > limit)
                best.Dequeue();
        }
        return best.UnorderedItems.OrderByDescending(item => item.Priority).Select(item => item.Element).ToList();
    }

    private static void AddRanks(List<long> ids, Dictionary<long, double> ranks)
    {
        for (var index = 0; index < ids.Count; index++)
            ranks[ids[index]] = ranks.GetValueOrDefault(ids[index]) + 1.0 / (21 + index);
    }

    private static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        return bytes;
    }

    private static DocumentInfo? FindDocument(SqliteConnection connection, SqliteTransaction? transaction, string column, string value)
    {
        using var command = Command(connection, transaction,
            $"SELECT {DocumentColumns} FROM documents WHERE {column} = $value", ("$value", value));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDocument(reader) : null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static DocumentInfo ReadDocument(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private static Passage ReadPassage(SqliteDataReader reader, int offset) => new(
        reader.GetInt32(offset), reader.IsDBNull(offset + 1) ? null : reader.GetInt32(offset + 1),
        reader.GetString(offset + 2));

    private static SearchHit ReadHit(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ReadPassage(reader, 3));
}
