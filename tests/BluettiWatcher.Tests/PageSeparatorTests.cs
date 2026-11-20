using System.Buffers;

namespace BluettiWatcher.Tests;

/// <summary>
/// The page text joins no facts with a separator: no middle dot outside an energy unit ("кВт·год", "Вт·год") and no text
/// arrow anywhere, in either language, in strings, markup or comments (an arrow on a control is an SVG icon).
/// </summary>
public sealed partial class PageI18nTests
{
    private static readonly SearchValues<char> Separators = SearchValues.Create("·→←↑↓⇒");

    [Fact]
    public void Page_has_no_middle_dot_outside_a_unit_and_no_text_arrow()
    {
        string page = Page().Replace("кВт·год", "", StringComparison.Ordinal).Replace("Вт·год", "", StringComparison.Ordinal);
        var hits = page.Split('\n').Select((line, i) => (line, n: i + 1))
            .Where(x => x.line.AsSpan().IndexOfAny(Separators) >= 0)
            .Select(x => $"{x.n}: {x.line.Trim()[..Math.Min(120, x.line.Trim().Length)]}")
            .ToList();
        Assert.Empty(hits);
    }
}
