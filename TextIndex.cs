using System.Text.RegularExpressions;

namespace RnxRagService;

internal static partial class TextIndex
{
    private static readonly HashSet<string> StopWords = new(
        ("а без в во вот для до за и из к как какая какие каким какой каком какую ли мне мы на над о об обо " +
         "он она они от по под при про с со то у уже что чтобы где когда сколько чем чему чего чей чья " +
         "есть это этом можно нужно расскажи скажи найти согласно " +
         "a about an and are as at be by can could do does for from how i in is it me of on or " +
         "should tell that the this to was what when where which who will with would you").Split(),
        StringComparer.Ordinal);

    public static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    public static string[] QueryTerms(string text) =>
        Word().Matches(Normalize(text))
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word) && word.Length <= 64)
            .Select(word => RussianEnding().Replace(word, "$1"))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();

    public static int CountMatches(string text, string[] terms)
    {
        var words = Word().Matches(Normalize(text)).Select(match => match.Value).ToArray();
        return terms.Count(term => words.Any(word =>
            word.Equals(term, StringComparison.Ordinal) ||
            (term.Length >= 4 && word.StartsWith(term, StringComparison.Ordinal))));
    }

    public static string FtsQuery(string[] terms) => string.Join(" OR ",
        terms.Select(term => term.Length >= 4 ? $"\"{term}\"*" : $"\"{term}\""));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"^([а-я]{4,}?)[аеиоуыэюяьй]{1,2}$")]
    private static partial Regex RussianEnding();
}
