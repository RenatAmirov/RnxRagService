namespace RnxRagService;

public sealed class RagOptions
{
    public string DatabasePath { get; set; } = "data/rag.db";
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string ChatModel { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public double MinSimilarity { get; set; } = 0.55;

    public bool UseChat => !string.IsNullOrWhiteSpace(ChatModel);
    public bool UseEmbeddings => !string.IsNullOrWhiteSpace(EmbeddingModel);
}

public sealed record DocumentInfo(
    string Id, string Name, long Size, DateTimeOffset UploadedAt, int ChunkCount, string? EmbeddingModel);

public sealed record AskRequest(string? Question, int TopK = 5, string? DocumentId = null);

public sealed record Source(
    int Number, string DocumentId, string DocumentName, int Chunk, int? Page, string Excerpt, string Url);

public sealed record AskResponse(string Answer, string Mode, IReadOnlyList<Source> Sources, string? Warning = null);

internal sealed record Passage(int Number, int? Page, string Text);
internal sealed record SearchHit(long Id, string DocumentId, string DocumentName, Passage Passage);

internal sealed class RagInputException(string message, int statusCode = 400, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}
