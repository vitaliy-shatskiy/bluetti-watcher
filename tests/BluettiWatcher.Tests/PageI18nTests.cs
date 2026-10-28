using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Tests;

/// <summary>The page's language switch: every text key the page uses exists in English and in Ukrainian.</summary>
public sealed partial class PageI18nTests
{
    private static string Page([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "BluettiWatcher", "Web", "station.html")));

    [GeneratedRegex(@"^\s+(\w+):\s""", RegexOptions.Multiline)]
    private static partial Regex DictKey();

    [GeneratedRegex(@"\bt\('(\w+)'")]
    private static partial Regex ScriptKey();

    [GeneratedRegex(@"\bt\([^()]*?\?\s*'(\w+)'\s*:\s*'(\w+)'")]
    private static partial Regex ChosenKey();

    [GeneratedRegex(@"data-i18n(?:-title|-aria)?=""(\w+)""")]
    private static partial Regex MarkupKey();

    [GeneratedRegex(@">([^<]*)<")]
    private static partial Regex TextNode();

    /// <summary>The page's I18N block split at its Ukrainian dictionary: (start, uk, end) offsets into a LF-normalised page.</summary>
    private static (int Start, int Uk, int End) Blocks(string page)
    {
        int start = page.IndexOf("const I18N = {", StringComparison.Ordinal);
        int uk = page.IndexOf("\nuk: {", Math.Max(0, start), StringComparison.Ordinal);
        int end = uk < 0 ? -1 : page.IndexOf("\n};", uk, StringComparison.Ordinal);
        Assert.True(start >= 0 && uk > start && end > uk, "the I18N block is not where the test reads it");
        return (start, uk, end);
    }

    private static (HashSet<string> En, HashSet<string> Uk) Dictionaries(string page)
    {
        var (start, uk, end) = Blocks(page);
        HashSet<string> Keys(string block) => DictKey().Matches(block).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return (Keys(page[start..uk]), Keys(page[uk..end]));
    }

    [Fact]
    public void Every_key_the_page_uses_exists_in_both_languages()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (en, uk) = Dictionaries(page);
        Assert.True(en.Count > 50, $"only {en.Count} English keys read");
        var used = ScriptKey().Matches(page).Select(m => m.Groups[1].Value)
            .Concat(ChosenKey().Matches(page).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }))
            .Concat(MarkupKey().Matches(page).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("fromMeasured", used);   // the reader sees the script's calls
        Assert.Contains("modeAutoTip", used);    // and the markup's attributes
        Assert.Empty(used.Where(k => !en.Contains(k)).Order());
        Assert.Empty(used.Where(k => !uk.Contains(k)).Order());
        Assert.Equal(en.Order(), uk.Order());
    }

    [Fact]
    public void Every_code_the_service_sends_has_a_key_in_both_languages()
    {
        var (en, uk) = Dictionaries(Page().Replace("\r\n", "\n", StringComparison.Ordinal));
        var keys = PageCodes.Keys().ToList();
        Assert.Contains("n_max_held", keys);
        Assert.Contains("om_INVERTER_OUTPUT", keys);
        Assert.DoesNotContain(keys, k => !en.Contains(k));
        Assert.DoesNotContain(keys, k => !uk.Contains(k));
        // the list the keys come from carries every code constant
        var consts = typeof(PageCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal);
        Assert.Equal(consts, PageCodes.Notes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Max held back: battery emergency", "max-held", "")]
    [InlineData("Eco held back: battery emergency", "eco-held", "")]
    [InlineData("Auto: eco not lifted: battery emergency", "auto-held", "")]
    [InlineData("Eco dropped: mains came back before it ran", "mode-dropped-back", "Eco")]
    [InlineData("Max dropped: mains was lost before it ran", "mode-dropped-lost", "Max")]
    [InlineData("Max dropped: something new", "mode-dropped", "Max|something new")]
    [InlineData("Auto: the power-mode flag is unreadable - nothing lifted", "flag-unreadable", "Auto")]
    [InlineData("Eco: exit 1: boom", "mode-failed", "Eco|exit 1: boom")]
    [InlineData("anything else", "mode-other", "anything else")]
    public void A_saved_mode_error_gets_its_code(string error, string code, string args)
    {
        var n = Http.StationApi.ModeErrorNote(error);
        Assert.NotNull(n);
        Assert.Equal((code, error, args), (n.Code, n.Text, string.Join('|', n.Args)));
        Assert.Contains(code, PageCodes.Notes);
        Assert.Null(Http.StationApi.ModeErrorNote(""));
    }

    [GeneratedRegex(@"^\s+(\w+):\s""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline)]
    private static partial Regex DictEntry();

    /// <summary>
    /// Rough width of a 12 px Segoe UI label in pixels: per-character advances, a little generous so a label that passes
    /// fits the browser's rendering.
    /// </summary>
    internal static double LabelWidth(string text)
    {
        double w = 0;
        foreach (char ch in text)
        {
            w += ch switch
            {
                ' ' => 3.3, '.' or ',' or ':' or '·' => 3.0, '(' or ')' => 3.8, '-' => 4.0, '%' => 9.6, >= '0' and <= '9' => 6.7,
                'i' or 'j' or 'l' => 2.8, 'f' or 't' or 'r' => 4.0, 's' => 5.2, 'm' => 9.7, 'w' => 8.7, 'I' => 3.2, 'M' or 'W' => 10.5,
                'ж' or 'ш' or 'щ' or 'ю' or 'м' => 8.8, 'т' or 'г' => 5.4, 'Ж' or 'Ш' or 'Щ' or 'Ю' or 'М' => 10.5,
                >= 'a' and <= 'z' => 6.3, >= 'A' and <= 'Z' => 7.7, >= 'а' and <= 'я' => 6.6, >= 'А' and <= 'Я' => 8.0,
                _ => 7.0,
            };
        }
        return w;
    }

    // The labels of the DTEK and cycles rows (a 150 px column) and of the load statistics (110 px), in both languages.
    [Theory]
    [InlineData(150, "queue,schedule,sources,nextOutage,plannedReturn,battery,expectedReturn,plan,pastCuts,siteSays,cycBase,cycCount,cycAvgLen,cycAvgExtra,cycShare,cycNow")]
    [InlineData(110, "statBase,statTypical,statAvg,statPeak,statOnTop,statEnergy")]
    public void Row_labels_fit_their_column_in_both_languages(int column, string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (start, ukAt, end) = Blocks(page);
        Dictionary<string, string> Entries(string block) => DictEntry().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var en = Entries(page[start..ukAt]);
        var uk = Entries(page[ukAt..end]);
        var tooWide = keys.Split(',').SelectMany(k => new[] { en[k], uk[k] }).Where(text => LabelWidth(text) > column - 2)
            .Select(text => $"{text} ({LabelWidth(text):0} px)").ToList();
        Assert.Empty(tooWide);
        Assert.True(LabelWidth("Наступне планове відключення") > 150, "the estimate is blind to a label known to truncate");
    }

    /// <summary>The four Russian letters Ukrainian does not have, both cases, written as escapes so this file holds none.</summary>
    private static readonly System.Buffers.SearchValues<char> NotUkrainian = System.Buffers.SearchValues.Create("\u044B\u042B\u044D\u042D\u0451\u0401\u044A\u042A");

    [Fact]
    public void The_page_holds_no_letter_Ukrainian_lacks()
    {
        var lines = Page().Split('\n');
        var bad = lines.Select((line, i) => (line, n: i + 1)).Where(x => x.line.AsSpan().IndexOfAny(NotUkrainian) >= 0)
            .Select(x => $"line {x.n}: {x.line.Trim()[..Math.Min(60, x.line.Trim().Length)]}").ToList();
        Assert.Empty(bad);
        Assert.True("\u0438\u0437\u0020\u044D\u0442\u043E\u0433\u043E".AsSpan().IndexOfAny(NotUkrainian) >= 0, "the letter list finds nothing");
    }

    /// <summary>Russian words (as escapes: no, now, this, what, for, all) that Ukrainian spells differently or not at all.</summary>
    [GeneratedRegex("(?<!\\p{L})(\u043D\u0435\u0442|\u0441\u0435\u0439\u0447\u0430\u0441|\u044D\u0442\u043E|\u0447\u0442\u043E|\u0434\u043B\u044F|\u0432\u0441\u0451|\u0432\u0441\u0435)(?!\\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RussianWord();

    [Fact]
    public void The_Ukrainian_dictionary_holds_no_Russian_word()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        var (_, uk, end) = Blocks(page);
        var bad = RussianWord().Matches(page[uk..end]).Select(m => m.Value).ToList();
        Assert.Empty(bad);
        // controls: the pattern finds a Russian sentence and passes the Ukrainian words that look alike
        Assert.Equal(2, RussianWord().Count("\u044D\u0442\u043E \u0441\u0435\u0439\u0447\u0430\u0441, \u043D\u0435 \u0432\u0441\u0435\u0433\u043E"));
        Assert.Empty(RussianWord().Matches("\u0443\u0441\u0435 \u0449\u043E \u043D\u0456\u0447\u043E\u0433\u043E"));
    }

    /// <summary>
    /// Runs page script in the Jint engine (no Node), the browser's local time zone set to <paramref name="timeZone"/> (the
    /// machine's own when null). Returns what the script printed with console.log. An engine that cannot run the script
    /// (a missing API, a script error, a promise left pending) fails the test: nothing is skipped.
    /// </summary>
    private static string RunScript(string script, string? timeZone = null)
    {
        var printed = new System.Text.StringBuilder();
        using var engine = new Jint.Engine(o =>
        {
            if (timeZone is not null) o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        });
        engine.SetValue("__print", new Action<string>(s => printed.AppendLine(s)));
        // Jint ships Intl.DateTimeFormat (time zones, formatToParts) but no console and no URLSearchParams: the two stubs the page tests need.
        engine.Execute("var console = { log: function () { __print(Array.prototype.join.call(arguments, ' ')); } };");
        engine.Execute("""
            var URLSearchParams = function (s) {
              this.m = {};
              String(s).split('&').forEach(function (kv) {
                if (!kv) return;
                var i = kv.indexOf('=');
                this.m[decodeURIComponent(i < 0 ? kv : kv.slice(0, i))] = i < 0 ? '' : decodeURIComponent(kv.slice(i + 1));
              }, this);
            };
            URLSearchParams.prototype.get = function (k) { return Object.prototype.hasOwnProperty.call(this.m, k) ? this.m[k] : null; };
            """);
        try
        {
            engine.Execute(script.Replace("})();", "})().catch(e => console.log('ERR ' + e + ' ' + e.stack));", StringComparison.Ordinal));
            for (int i = 0; i < 10_000 && printed.Length == 0; i++) engine.Advanced.ProcessTasks();   // the async page functions (fetchAndShow...) run to the end
        }
        catch (Jint.Runtime.JavaScriptException e)
        {
            Assert.Fail($"the script failed under Jint: {e.Message} ({e.Location})");
        }
        string output = printed.ToString().Trim();
        Assert.False(output.StartsWith("ERR ", StringComparison.Ordinal), output);   // an async page function rejected
        Assert.False(output.Length == 0, "the script printed nothing: Jint did not run it to the end");
        return output;
    }

    /// <summary>The text of a top-level function of the page, from its first line to the closing brace at column 0.</summary>
    private static string FunctionText(string page, string name)
    {
        var m = Regex.Match(page.Replace("\r\n", "\n", StringComparison.Ordinal), $@"^(?:async )?function {name}\(.*?^\}}$", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, $"function {name} is not where the test reads it");
        return m.Value;
    }

    private sealed record LangCase(string? Stored, bool GetThrows, bool SetThrows, string? Browser, string ExpectLang, string? ExpectWritten);

    [Fact]
    public void The_language_defaults_to_the_browser_and_remembers_the_choice()
    {
        string page = Page();
        // two buttons, English and Ukrainian, and no Russian
        Assert.Contains("<button data-lang=\"en\" data-i18n=\"langEn\"></button><button data-lang=\"uk\" data-i18n=\"langUk\"></button>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-lang=\"ru\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("langRu", page, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem('station.lang'", page, StringComparison.Ordinal);   // the switch writes the choice

        // the page's own startLang, run with a fake localStorage and navigator
        var cases = new LangCase[]
        {
            new("ru", false, false, "en-US", "uk", "uk"),          // an older Russian choice becomes Ukrainian and is written back
            new("ru", false, true, "en-US", "uk", null),           // ... and a failing write changes nothing else
            new("en", false, false, "uk-UA", "en", null),          // a stored choice beats the browser
            new("uk", false, false, "en-US", "uk", null),
            new(null, false, false, "uk", "uk", null),
            new(null, false, false, "uk-UA", "uk", null),
            new(null, false, false, "UK-ua", "uk", null),
            new(null, false, false, "ru-RU", "en", null),          // Russian browsers get English
            new(null, false, false, "en-US", "en", null),
            new(null, false, false, null, "en", null),             // no language at all
            new("de", false, false, "uk", "uk", null),             // an unknown stored value is ignored
            new(null, true, false, "uk-UA", "uk", null),           // a throwing localStorage (private window)
            new(null, true, true, "en-US", "en", null),
            new("ru", true, true, "uk", "uk", null),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(cases.Select(c => new { stored = c.Stored, get = c.GetThrows, set = c.SetThrows, nav = c.Browser }));
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(FunctionText(page, "startLang"))}};
            const cases = {{json}};
            const out = cases.map((c) => {
              const writes = [];
              const ls = {
                getItem: () => { if (c.get) throw new Error('private'); return c.stored; },
                setItem: (k, v) => { if (c.set) throw new Error('private'); writes.push(k + '=' + v); },
              };
              const nav = c.nav === null ? {} : { language: c.nav };
              return { lang: new Function('localStorage', 'navigator', src + '; return startLang();')(ls, nav), written: writes.length ? writes.join(',') : null };
            });
            console.log(JSON.stringify(out));
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("lang").GetString(), e.GetProperty("written").GetString())).ToList();
        var want = cases.Select(c => (c.ExpectLang, c.ExpectWritten is null ? null : "station.lang=" + c.ExpectWritten)).ToList();
        for (int i = 0; i < cases.Length; i++)
            Assert.True(want[i] == got[i], $"case {i} {cases[i]}: expected {want[i]}, got {got[i]}");
    }

    [Fact]
    public void The_eco_countdown_shows_the_browser_clock()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("kyivHm(at)", page, StringComparison.Ordinal);
        string Line(string pattern)
        {
            var m = Regex.Match(page, pattern, RegexOptions.Multiline);
            Assert.True(m.Success, $"{pattern} is not where the test reads it");
            return m.Value;
        }
        // the page's own pad, hm and tick (the one the eco chip runs), with a fixed clock: 90 s before the switch at 18:30 UTC
        string pageCode = string.Join("\n", Line(@"^const pad = .*;$"), Line(@"^const hm = \(t\) => .*;$"), Line(@"^\s*const tick = \(\) => \{.*\};$"));
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(pageCode)}};
            const NOW = Date.UTC(2026, 9, 13, 18, 28, 30), AT = Date.UTC(2026, 9, 13, 18, 30, 0);
            const run = (now) => {
              const RealDate = Date;
              class FakeDate extends RealDate { constructor(...a) { if (a.length) super(...a); else super(now); } static now() { return now; } }
              const e = { lastChild: {} };
              const t = (k, p) => JSON.stringify([k, p]);
              new Function('e', 't', 'at', 'Date', src + '; tick();')(e, t, AT, FakeDate);
              return e.lastChild.textContent;
            };
            console.log(JSON.stringify([run(NOW), run(AT + 1000)]));
            """;
        foreach (var (zone, clock) in new[] { ("Asia/Tokyo", "03:30"), ("America/New_York", "14:30"), ("Europe/Kyiv", "21:30") })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(RunScript(script, zone));
            var shown = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal($$"""["ecoIn",{"m":1,"s":"30","at":"{{clock}}"}]""", shown[0]);
            Assert.Equal("""["ecoNext",null]""", shown[1]);   // the moment has passed: the next reading
        }
    }

    [Fact]
    public void The_schedule_rows_name_Kyiv_time_only_when_the_browser_clock_differs()
    {
        string page = Page();
        string code = FunctionText(page, "dtf") + "\n" + FunctionText(page, "kyivZone") + "\n" + FunctionText(page, "kyivOffsetDiffers");
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(code)}};
            const make = (intl) => new Function('Intl', src + '; return { kyivZone, kyivOffsetDiffers };')(intl);
            // a browser that knows only the former name, and one that knows neither
            const only = (known) => ({ DateTimeFormat: function (loc, o) { if (o && o.timeZone && !known.includes(o.timeZone)) throw new RangeError('unknown zone'); return new Intl.DateTimeFormat(loc, o); } });
            const real = make(Intl);
            const win = Date.UTC(2026, 0, 15, 12), sum = Date.UTC(2026, 9, 13, 12);
            const out = {
              zone: real.kyivZone(), old: make(only(['Europe/Kiev'])).kyivZone(), none: make(only([])).kyivZone(),
              noThrow: make(only([])).kyivOffsetDiffers(sum),
              winter: real.kyivOffsetDiffers(win), summer: real.kyivOffsetDiffers(sum),
              oldBrowser: make(only(['Europe/Kiev'])).kyivOffsetDiffers(sum),
            };
            console.log(JSON.stringify(out));
            """;
        string Result(string zone, string field) => System.Text.Json.JsonDocument.Parse(RunScript(script, zone)).RootElement.GetProperty(field).ToString();

        Assert.True(Result("Asia/Tokyo", "zone") is "Europe/Kyiv" or "Europe/Kiev");
        Assert.Equal("Europe/Kiev", Result("Asia/Tokyo", "old"));   // the old name is the fallback
        Assert.Equal("", Result("Asia/Tokyo", "none"));             // null: neither known
        Assert.Equal("False", Result("Asia/Tokyo", "noThrow"));     // and nothing throws out of it
        Assert.Equal("False", Result("Europe/Kyiv", "winter"));
        Assert.Equal("False", Result("Europe/Kyiv", "summer"));
        Assert.Equal("True", Result("Asia/Tokyo", "winter"));
        Assert.Equal("True", Result("Asia/Tokyo", "summer"));
        Assert.Equal("True", Result("Europe/Berlin", "summer"));    // one hour apart
        Assert.Equal("True", Result("Europe/Istanbul", "winter"));  // +3 all year: equal to Kyiv in summer only
        Assert.Equal("False", Result("Europe/Istanbul", "summer"));
        Assert.Equal("True", Result("Asia/Tokyo", "oldBrowser"));
        // the schedule rows carry the tag, and only through that check
        Assert.Contains("kyivOffsetDiffers(Date.now()) ?", page, StringComparison.Ordinal);
        Assert.Contains("from(w.today) + kyivTag", page, StringComparison.Ordinal);
        Assert.Contains("from(w.tomorrow) + kyivTag", page, StringComparison.Ordinal);
    }

    // Intl: a formatter is built once per zone and name; an unknown zone gives null, never a throw
    [Fact]
    public void Intl_formatters_are_built_once_and_an_unknown_zone_falls_back()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(FunctionText(page, "dtf") + "\n" + FunctionText(page, "kyivZone") + "\n" + FunctionText(page, "kyivOffsetDiffers"))}};
            let built = 0;
            const counting = (known) => ({ DateTimeFormat: function (loc, o) { built++; if (o && o.timeZone && !known.includes(o.timeZone)) throw new RangeError('unknown zone'); return new Intl.DateTimeFormat(loc, o); } });
            const make = (intl) => new Function('Intl', src + '; return { dtf, kyivZone, kyivOffsetDiffers };')(intl);
            const ms = Date.UTC(2026, 9, 13, 12);
            const a = make(counting(['Europe/Kyiv']));
            for (let i = 0; i < 100; i++) { a.kyivZone(); a.kyivOffsetDiffers(ms); a.dtf('Europe/Kyiv', 'hm', { hour: '2-digit' }); }
            const once = built;
            built = 0;
            const b = make(counting([]));
            const out = { once, zone: b.kyivZone(), unknown: b.dtf('Mars/Base', 'hm', {}), local: typeof b.dtf(null, 'hm', { hour: '2-digit' }).format, differs: b.kyivOffsetDiffers(ms) };
            for (let i = 0; i < 100; i++) b.kyivZone();
            out.tries = built;
            console.log(JSON.stringify(out));
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(RunScript(script, "Asia/Tokyo"));
        var r = doc.RootElement;
        Assert.Equal(3, r.GetProperty("once").GetInt32());            // zone probe, the parts formatter, hm: once each over 100 rounds
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r.GetProperty("zone").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r.GetProperty("unknown").ValueKind);
        Assert.Equal("function", r.GetProperty("local").GetString());  // the browser's own zone still formats
        Assert.False(r.GetProperty("differs").GetBoolean());
        Assert.Equal(4, r.GetProperty("tries").GetInt32());             // Kyiv, Kiev, Mars, local: a refused zone is not tried again
        Assert.DoesNotContain("new Intl.DateTimeFormat", page.Replace(FunctionText(page, "dtf"), "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // The page reads the 26 h once, then only the rows after the last one; nothing new = no repaint
    [Fact]
    public void The_page_reads_history_incrementally_and_skips_an_unchanged_repaint()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        string Line(string pattern)
        {
            var m = Regex.Match(page, pattern, RegexOptions.Multiline);
            Assert.True(m.Success, $"{pattern} is not where the test reads it");
            return m.Value;
        }
        string code = string.Join("\n", Line(@"^const SPAN = .*;$"), Line(@"^let nowText = .*;$"), FunctionText(page, "load"), Line(@"^var reqSeq = .*;$"), FunctionText(page, "takeAnswer"), FunctionText(page, "fetchAndShow"));
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(code)}};
            const MIN = 60000, T = Date.UTC(2026, 9, 13, 12, 0, 30);
            let clock = T, shows = 0, asked = [];
            const iso = (ms) => new Date(ms).toISOString();
            const rows = (from, to) => { const out = []; for (let m = Math.ceil(from / MIN) * MIN; m <= to; m += MIN) if (m <= T - 2 * MIN + (clock - T)) out.push({ at: iso(m), soc: 50 }); return out; };
            const fetch = async (url) => {
              asked.push(url);
              if (url === '/api/now') return { ok: true, status: 200, text: async () => '{"n":' + Math.floor((clock - T) / (2 * MIN)) + '}' };
              const q = new URLSearchParams(url.split('?')[1]);
              return { ok: true, status: 200, json: async () => ({ rows: rows(Number(q.get('from')), Number(q.get('to'))) }) };
            };
            const FakeDate = { now: () => clock, parse: (s) => Date.parse(s) };
            const page = new Function('fetch', 'Date', '$', 't', 'show', 'let lastNow = null, lastRows = null;\n' + src + '; return { load, rows: () => lastRows };')(fetch, FakeDate, () => ({ lastChild: {} }), (k) => k, () => { shows++; });
            (async () => {
              const out = [];
              await page.load(); out.push({ shows, n: page.rows().length, first: asked[1] });
              clock += MIN; asked = []; await page.load(); out.push({ shows, n: page.rows().length, from: Number(new URLSearchParams(asked[1].split('?')[1]).get('from')) });
              asked = []; await page.load(); out.push({ shows, n: page.rows().length });
              await page.load(true); out.push({ shows });
              clock += 31 * MIN; asked = []; await page.load(); out.push({ shows, from: Number(new URLSearchParams(asked[1].split('?')[1]).get('from')), n: page.rows().length, firstAt: page.rows()[0].at });
              console.log(JSON.stringify({ out, T, MIN }));
            })();
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(RunScript(script));
        var r = doc.RootElement;
        long t = r.GetProperty("T").GetInt64(), min = r.GetProperty("MIN").GetInt64();
        var o = r.GetProperty("out").EnumerateArray().ToList();
        Assert.Equal(1, o[0].GetProperty("shows").GetInt32());
        Assert.Equal(26 * 60 - 2, o[0].GetProperty("n").GetInt32());                         // the whole 26 h: 10:01 to 11:58 next day
        Assert.Contains($"from={t - 26 * 3600000}", o[0].GetProperty("first").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, o[1].GetProperty("shows").GetInt32());                               // a new row: a repaint
        Assert.Equal(26 * 60 - 2, o[1].GetProperty("n").GetInt32());                         // one in, the oldest out
        long lastRow = (t - 2 * min) / min * min;
        Assert.Equal(lastRow + 1000, o[1].GetProperty("from").GetInt64());                   // only after the last row held
        Assert.Equal(2, o[2].GetProperty("shows").GetInt32());                               // nothing new: no repaint
        Assert.Equal(3, o[3].GetProperty("shows").GetInt32());                               // forced: a repaint
        Assert.Equal(4, o[4].GetProperty("shows").GetInt32());
        Assert.Equal(t + 32 * min - 26 * 3600000, o[4].GetProperty("from").GetInt64());      // 30 minutes on: the whole 26 h again
    }

    [Fact]
    public void The_page_shows_queues_without_the_GPV_prefix()
    {
        string page = Page();
        Assert.Contains("const qd = (q) => String(q ?? '').split('+').map((x) => x.replace(/^GPV(?=\\d)/, '')).join(' + ');", page, StringComparison.Ordinal);
        foreach (string use in new[] { "esc(qd(w.queue))", "w.queues.map(qd)", "esc(qd(w.nextQueue))", "(set ? day : `${qd(q.queue)} · ${day}`)", "row(label(w.today)", "row(label(w.tomorrow)", "val(qd(a))", "qd(i.queues.join('+'))" })
            Assert.Contains(use, page, StringComparison.Ordinal);
        Assert.DoesNotContain("esc(w.queue)", page, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"const qd = \(q\) => String\(q \?\? ''\)\.split\('(.)'\)\.map\(\(x\) => x\.replace\(/(.+?)/, ''\)\)\.join\('(.+?)'\);")]
    private static partial Regex PageQd();

    [GeneratedRegex(@"const set = String\(q\.queue\)\.includes\('(.)'\);\s*const label = \(day\) => \(set \? day : `\$\{qd\(q\.queue\)\}(.+?)\$\{day\}`\);")]
    private static partial Regex PageLabel();

    /// <summary>The window-row label as the page builds it: its own qd (split, prefix pattern, join) and label code, read from station.html.</summary>
    private static string WindowLabel(string page, string queue, string day)
    {
        var qd = PageQd().Match(page);
        var label = PageLabel().Match(page);
        Assert.True(qd.Success && label.Success, "the page's qd or window-row label is not where the test reads it");
        string shown = string.Join(qd.Groups[3].Value, queue.Split(qd.Groups[1].Value[0]).Select(x => Regex.Replace(x, qd.Groups[2].Value, "")));
        return queue.Contains(label.Groups[1].Value[0], StringComparison.Ordinal) ? day : shown + label.Groups[2].Value + day;
    }

    // The rows a queue or a set of queues adds (150 px column), built by the page's own code. A set's rows carry the day alone.
    [Theory]
    [InlineData("GPV6.2", "6.2 · 08.10.2026")]
    [InlineData("GPV12.12", "12.12 · 08.10.2026")]
    [InlineData("GPV4.1+GPV6.2", "08.10.2026")]
    [InlineData("GPV12.12+GPV12.12+GPV12.12+GPV12.12", "08.10.2026")]
    public void Window_row_labels_of_a_queue_or_a_set_fit_their_column(string queue, string label)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(label);
        string page = Page();
        Assert.Equal(label, WindowLabel(page, queue, "08.10.2026"));
        Assert.True(LabelWidth(label) <= 148, $"{label} ({LabelWidth(label):0} px)");
        Assert.True(LabelWidth(WindowLabel(page, queue, "28.12.2026")) <= 148);
        // the page's qd of a set, as the queue row shows it
        var qd = PageQd().Match(page);
        Assert.Equal("4.1 + 6.2", string.Join(qd.Groups[3].Value, "GPV4.1+GPV6.2".Split(qd.Groups[1].Value[0]).Select(x => Regex.Replace(x, qd.Groups[2].Value, ""))));
    }

    [Fact]
    public void The_gap_finder_marks_exactly_where_the_chart_line_breaks()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        // the legend item and the grey band sit beside the grid-off ones; the band is drawn after the grid-off band, so it lies on top
        Assert.Contains("data-i18n=\"legendNoData\"", page, StringComparison.Ordinal);
        int off = page.IndexOf("fill=\"rgba(224,165,58,.10)\"", StringComparison.Ordinal), hatch = page.IndexOf("fill=\"url(#nodata)\"", StringComparison.Ordinal);
        Assert.True(off > 0 && hatch > off, "the no-data band must be drawn after the grid-off band");
        // draw() cannot run here (it needs a DOM), so the helper is tested with the arguments draw passes: all rows, the range start, NOW and the line's own GAP
        Assert.Contains("const gaps = gapsOf(rows, t0, NOW, GAP);", page, StringComparison.Ordinal);
        Assert.Contains("const GAP = 10 * 60000;", page, StringComparison.Ordinal);
        Assert.Contains("r.t - list[i - 1].t > GAP ? 'M' : 'L'", page, StringComparison.Ordinal);   // the line breaks on "> GAP"; the band rule is the same
        string script = $$"""
            const src = {{System.Text.Json.JsonSerializer.Serialize(FunctionText(page, "gapsOf"))}};
            const gapsOf = new Function(src + '; return gapsOf;')();
            const M = 60000, GAP = 10 * M, T0 = Date.UTC(2026, 9, 13, 18, 0, 0);
            const rows = (mins) => mins.map((m) => ({ t: T0 + m * M }));
            const every = (step, n, from = 0) => Array.from({ length: n }, (_, i) => from + i * step);
            const mins = (g) => g.map((x) => [(x.a - T0) / M, (x.b - T0) / M]);
            // the range starts at minute `from` and ends at minute `now` (default: the last row)
            const run = (m, from, now) => mins(gapsOf(rows(m), T0 + from * M, T0 + (now ?? m[m.length - 1]) * M, GAP));
            const out = {
              hole: run(every(1, 15).concat(every(1, 15, 31)), 0),                  // readings 0..14, then 31..45: a 17-min hole
              regular: run(every(1, 120), 0),
              atGap: run([0, 1, 2, 12, 13], 0),                                      // 10 min exactly (2 -> 12): the line is continuous, no band
              overGap: run([0, 1, 2, 13, 14], 0),                                    // 11 min: the line breaks, band
              fiveMinRows: run(every(5, 6).concat(every(5, 6, 39)), 0),              // 5-min rows, a 14-min hole (25 -> 39): breaks the line, so a band
              hourly: run(every(60, 5), 0),                                          // hourly rows: every step breaks the line, every step is a band
              one: run([0], 0),
              tail: run(every(1, 10), 0, 20),                                        // last reading 9, now 20: trailing hole
              tailAtGap: run(every(1, 10), 0, 19),                                   // 9 -> 19 is exactly GAP: none
              tailShort: run(every(1, 10), 0, 12),
              leading: run([30, 31, 32, 33], 0),                                     // nothing before the first row
              edge: run([-180, 50, 51, 52], 0),                                      // range from 0, readings stop at -180 and resume at 50: band starts at the edge
              edgeShort: run([-5, 1, 2, 3], 0),                                      // the row before the range is 6 min before the next one: no band
              edgeClipped: run([-180, 5, 6, 7], 0),                                  // resumes at 5: band 0..5
              emptyRange: run([-180, -170], 0, 60),                                  // no row in the range at all: one band from the range start to now
              emptyRecent: run([-180, -4], 0, 3),                                    // no row in the range, last one 7 min before now: no band
              noRows: mins(gapsOf([], T0, T0 + 60 * M, GAP)),
            };
            console.log(JSON.stringify(out));
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(RunScript(script));
        string J(string name) => doc.RootElement.GetProperty(name).GetRawText();
        Assert.Equal("[[14,31]]", J("hole"));   // no reading from minute 14 to 31: 17 min
        Assert.Equal("[]", J("regular"));
        Assert.Equal("[]", J("atGap"));
        Assert.Equal("[[2,13]]", J("overGap"));
        Assert.Equal("[[25,39]]", J("fiveMinRows"));
        Assert.Equal("[[0,60],[60,120],[120,180],[180,240]]", J("hourly"));
        Assert.Equal("[]", J("one"));
        Assert.Equal("[[9,20]]", J("tail"));
        Assert.Equal("[]", J("tailAtGap"));
        Assert.Equal("[]", J("tailShort"));
        Assert.Equal("[]", J("leading"));
        Assert.Equal("[[0,50]]", J("edge"));
        Assert.Equal("[]", J("edgeShort"));
        Assert.Equal("[[0,5]]", J("edgeClipped"));
        Assert.Equal("[[0,60]]", J("emptyRange"));
        Assert.Equal("[]", J("emptyRecent"));
        Assert.Equal("[]", J("noRows"));
    }

    [Fact]
    public void The_markup_holds_no_text_of_its_own()
    {
        string page = Page();
        int a = page.IndexOf("<main>", StringComparison.Ordinal), b = page.IndexOf("</main>", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        var texts = TextNode().Matches(page[a..(b + 1)]).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Any(char.IsLetter)).ToList();
        Assert.Empty(texts);
    }
}
