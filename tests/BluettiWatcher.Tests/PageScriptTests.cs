using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Tests;

/// <summary>The page's inline scripts parse: a syntax error (a duplicate let, a stray brace) stops the whole page at load.</summary>
public sealed partial class PageScriptTests
{
    private static string Page([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "BluettiWatcher", "Web", "station.html")));

    /// <summary>An inline classic script: a script element with no src and no type, or a JavaScript type.</summary>
    [GeneratedRegex(@"<script(?![^>]*\bsrc\s*=)(?:\s+type=""(?:text/javascript|application/javascript)"")?\s*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    /// <summary>The syntax errors of each inline script, as "station.html line N: message"; empty when every script parses.</summary>
    private static List<string> ParseErrors(string page)
    {
        page = page.Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = InlineScript().Matches(page);
        Assert.True(blocks.Count > 0, "no inline script found in the page: the test reads nothing");
        var errors = new List<string>();
        foreach (Match b in blocks)
        {
            int firstLine = page.AsSpan(0, b.Groups[1].Index).Count('\n') + 1;
            try
            {
                Jint.Engine.PrepareScript(b.Groups[1].Value, "station.html");
            }
            catch (Jint.ScriptPreparationException e) when (e.InnerException is Acornima.ParseErrorException p)
            {
                errors.Add($"station.html line {firstLine + p.LineNumber - 1}: {p.Description}");
            }
        }
        return errors;
    }

    [Fact]
    public void Every_inline_script_of_the_page_parses()
    {
        Assert.Empty(ParseErrors(Page()));
        // control: the check sees the error that stopped the page once, a let declared twice in one scope
        var twice = ParseErrors("<p>\n<script>\nlet grid = 1;\nlet grid = 2;\n</script>");
        Assert.Equal("station.html line 4: Identifier 'grid' has already been declared", Assert.Single(twice));
    }
}
