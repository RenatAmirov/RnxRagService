using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RnxRagService;

internal sealed partial class RagEngine(RagStore store, OllamaClient ollama, RagOptions options, ILogger<RagEngine> logger)
{
    private const string NoAnswer = "В загруженных документах недостаточно информации для ответа.";

    public async Task<(DocumentInfo Document, bool Created)> UploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl))
            throw new RagInputException("Укажите корректное имя файла длиной до 240 символов.");

        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!DocumentReader.Supports(extension))
            throw new RagInputException("Поддерживаются файлы PDF, TXT и MD.", 415);
        if (file.Length == 0)
            throw new RagInputException("Файл пуст.", 422);
        if (file.Length > DocumentReader.MaxBytes)
            throw new RagInputException("Максимальный размер файла — 20 МБ.", 413);

        var bytes = new byte[(int)file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(bytes, cancellationToken);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (store.FindByHash(hash) is { } existing)
            return (existing, false);

        var passages = DocumentReader.Read(bytes, extension, cancellationToken);
        var vectors = options.UseEmbeddings ? await IndexAsync(passages, cancellationToken) : null;
        return store.AddDocument(name, bytes.Length, hash, passages, options.UseEmbeddings ? options.EmbeddingModel : null,
            vectors, cancellationToken);
    }

    public async Task<bool> ReindexAsync(string id, CancellationToken cancellationToken)
    {
        if (!options.UseEmbeddings)
            throw new RagInputException("Модель эмбеддингов не настроена.", 409);
        var passages = store.GetPassages(id);
        if (passages.Length == 0)
            return false;

        var vectors = await IndexAsync(passages, cancellationToken);
        return store.SetEmbeddings(id, options.EmbeddingModel, passages, vectors, cancellationToken);
    }

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > 1000)
            throw new RagInputException("Введите вопрос длиной от 1 до 1000 символов.");
        if (request.TopK is < 1 or > 8)
            throw new RagInputException("Значение topK должно быть от 1 до 8.");
        if (request.DocumentId is not null && store.GetDocument(request.DocumentId) is null)
            throw new RagInputException("Документ не найден.", 404);

        var terms = TextIndex.QueryTerms(question);
        string? warning = null;
        float[]? vector = null;
        if (options.UseEmbeddings && store.HasEmbeddings(options.EmbeddingModel, request.DocumentId))
        {
            try
            {
                vector = (await ollama.EmbedAsync([question], cancellationToken))[0];
            }
            catch (OllamaException ex)
            {
                logger.LogWarning(ex, "Could not embed the question");
                warning = "Поиск по смыслу недоступен. Выполнен поиск по словам.";
            }
        }

        var hits = store.Search(terms, request.DocumentId, request.TopK, options.EmbeddingModel, vector,
            options.MinSimilarity, cancellationToken);
        if (hits.Count == 0)
            return new AskResponse(NoAnswer, "no-answer", [], warning);

        if (options.UseChat)
        {
            try
            {
                var generated = await ollama.AnswerAsync(question, hits, cancellationToken);
                if (!generated.Supported)
                    return new AskResponse(NoAnswer, "no-answer", [], warning);

                var answer = generated.Answer?.Trim() ?? "";
                var citations = Citation().Matches(answer)
                    .Select(match => int.TryParse(match.Groups[1].Value, out var number) ? number : 0)
                    .Distinct().ToArray();
                if (answer.Length > 0 && citations.Length > 0 && citations.All(number => number >= 1 && number <= hits.Count))
                {
                    return new AskResponse(answer, "generated",
                        citations.Select(number => ToSource(number, hits[number - 1])).ToArray(), warning);
                }

                throw new OllamaException("Ответ модели не содержит корректных ссылок.");
            }
            catch (OllamaException ex)
            {
                logger.LogWarning(ex, "Could not generate an answer with citations");
                warning = (warning is null ? "" : warning + " ") + "Генерация недоступна. Показаны цитаты из документов.";
            }
        }
        return ExtractAnswer(hits, terms, vector is not null, warning);
    }

    private async Task<float[][]> IndexAsync(IEnumerable<Passage> passages, CancellationToken cancellationToken)
    {
        try
        {
            return await ollama.EmbedAsync(passages.Select(passage => passage.Text).ToArray(), cancellationToken);
        }
        catch (OllamaException ex)
        {
            logger.LogWarning(ex, "Could not index document embeddings");
            throw new RagInputException("Индексация недоступна. Проверьте, что Ollama запущена и модель эмбеддингов загружена.",
                503, ex);
        }
    }

    private static AskResponse ExtractAnswer(List<SearchHit> hits, string[] terms, bool semantic, string? warning)
    {
        var matches = hits.SelectMany((hit, index) => SentenceBoundary().Split(hit.Passage.Text)
            .Select(sentence => Whitespace().Replace(sentence.Trim(), " "))
            .Where(sentence => sentence.Length >= 3)
            .Select(sentence => (Hit: index, Sentence: sentence, Matches: TextIndex.CountMatches(sentence, terms))))
            .Where(item => item.Matches > 0)
            .ToArray();
        var bodyMatches = matches.Where(item => !MarkdownHeading().IsMatch(item.Sentence)).ToArray();
        var snippets = (bodyMatches.Length > 0 ? bodyMatches : matches)
            .OrderByDescending(item => item.Matches)
            .DistinctBy(item => TextIndex.Normalize(item.Sentence))
            .Take(3)
            .ToArray();
        if (snippets.Length == 0 && semantic)
            snippets = [(0, Excerpt(hits[0].Passage.Text, 460), 0)];
        if (snippets.Length == 0)
            return new AskResponse(NoAnswer, "no-answer", [], warning);

        var sources = new Dictionary<int, Source>();
        var answer = new List<string>();
        foreach (var snippet in snippets)
        {
            if (!sources.TryGetValue(snippet.Hit, out var source))
            {
                source = ToSource(sources.Count + 1, hits[snippet.Hit]);
                sources.Add(snippet.Hit, source);
            }
            answer.Add($"«{Excerpt(snippet.Sentence, 460)}» [{source.Number}]");
        }
        return new AskResponse(string.Join("\n\n", answer), "extractive", sources.Values.ToArray(), warning);
    }

    private static Source ToSource(int number, SearchHit hit) => new(
        number, hit.DocumentId, hit.DocumentName, hit.Passage.Number, hit.Passage.Page,
        Excerpt(hit.Passage.Text, 230), $"/api/documents/{hit.DocumentId}/chunks/{hit.Passage.Number}");

    private static string Excerpt(string text, int limit)
    {
        var singleLine = Whitespace().Replace(text, " ").Trim();
        if (singleLine.Length <= limit)
            return singleLine;
        var boundary = singleLine.LastIndexOf(' ', limit);
        if (boundary <= limit / 2)
            boundary = char.IsLowSurrogate(singleLine[limit]) ? limit - 1 : limit;
        return singleLine[..boundary] + "…";
    }

    [GeneratedRegex(@"\[([0-9]+)\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"^#{1,6} ")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
