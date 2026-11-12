using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RnxRagService;

internal static class DocumentReader
{
    public const int MaxBytes = 20 * 1024 * 1024;
    private const int MaxCharacters = 1_000_000;
    private const int ChunkSize = 850;
    private const int Overlap = 110;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool Supports(string extension) => extension is ".txt" or ".md" or ".pdf";

    public static List<Passage> Read(byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        var passages = new List<Passage>();
        var characters = 0;

        void AddText(string text, int? page)
        {
            text = text.ReplaceLineEndings("\n").Trim();
            characters += text.Length;
            if (characters > MaxCharacters)
                throw new RagInputException("В документе больше 1 000 000 символов.", 413);

            var start = 0;
            while (start < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(text.Length, start + ChunkSize);
                if (end < text.Length)
                    end = FindBoundary(text, start + ChunkSize * 2 / 3, end);

                var part = text[start..end].Trim();
                if (part.Length > 0)
                    passages.Add(new Passage(passages.Count + 1, page, part));

                if (end == text.Length)
                    break;

                var overlapStart = end - Overlap;
                while (overlapStart < end && !char.IsWhiteSpace(text[overlapStart]))
                    overlapStart++;
                start = overlapStart;
            }
        }

        if (extension == ".pdf")
        {
            try
            {
                using var pdf = PdfDocument.Open(bytes);
                if (pdf.NumberOfPages > 1_000)
                    throw new RagInputException("В PDF больше 1000 страниц.", 413);

                foreach (var page in pdf.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddText(ContentOrderTextExtractor.GetText(page), page.Number);
                }
            }
            catch (Exception ex) when (ex is not (RagInputException or OperationCanceledException or OutOfMemoryException))
            {
                throw new RagInputException("Не удалось прочитать PDF. Проверьте файл и снимите защиту паролем.", 422, ex);
            }
        }
        else
        {
            try
            {
                var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
                if (text.Contains('\0'))
                    throw new RagInputException("Загрузите текстовый файл в кодировке UTF-8.", 422);
                AddText(text, null);
            }
            catch (DecoderFallbackException ex)
            {
                throw new RagInputException("Загрузите текстовый файл в кодировке UTF-8.", 422, ex);
            }
        }

        if (passages.Count == 0)
            throw new RagInputException("В файле нет извлекаемого текста. Для PDF-сканов сначала распознайте текст.", 422);

        return passages;
    }

    private static int FindBoundary(string text, int minimum, int maximum)
    {
        for (var index = maximum; index > minimum; index--)
        {
            if (text[index - 1] == '\n' || (char.IsWhiteSpace(text[index]) && text[index - 1] is '.' or '!' or '?'))
                return index;
        }
        for (var index = maximum; index > minimum; index--)
        {
            if (char.IsWhiteSpace(text[index]))
                return index;
        }
        return char.IsLowSurrogate(text[maximum]) ? maximum - 1 : maximum;
    }
}
