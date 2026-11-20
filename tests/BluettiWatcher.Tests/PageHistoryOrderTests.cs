namespace BluettiWatcher.Tests;

/// <summary>The rows the charts draw are kept in time order, one per time: a row out of order never draws a line back.</summary>
public sealed partial class PageI18nTests
{
    [Fact]
    public void MergeRows_out_of_order_and_repeated_rows_come_out_sorted_and_once()
    {
        string page = Page();
        string script = $$"""
            eval({{Js(FunctionText(page, "mergeRows"))}});
            const at = (m) => new Date(Date.UTC(2026, 10, 20, 20, m)).toISOString();
            const kept = [{ at: at(0), soc: 57 }, { at: at(1), soc: 57 }, { at: at(2), soc: 58 }];
            // the newest first, an old one again with a new value, one older than the span
            const fresh = [{ at: at(5), soc: 68 }, { at: at(1), soc: 99 }, { at: at(3), soc: 60 }, { at: new Date(Date.UTC(2026, 10, 19, 0, 0)).toISOString(), soc: 1 }];
            const out = mergeRows(kept, fresh, Date.UTC(2026, 10, 20, 0, 0));
            console.log(JSON.stringify(out.map((r) => new Date(r.at).getUTCMinutes() + ':' + r.soc)));
            """;
        Assert.Equal("""["0:57","1:99","2:58","3:60","5:68"]""", RunScript(script));
    }
}
