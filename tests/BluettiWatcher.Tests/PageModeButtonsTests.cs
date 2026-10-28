using System.Text.Json;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Tests;

/// <summary>The mode buttons' feedback: the press in flight, the line under the buttons and the order of answers, run in Jint.</summary>
public sealed partial class PageI18nTests
{
    private static string Const(string page, string name)
    {
        var m = Regex.Match(page.Replace("\r\n", "\n", StringComparison.Ordinal), $@"^const {name} = .*;$", RegexOptions.Multiline);
        Assert.True(m.Success, $"const {name} is not where the test reads it");
        return m.Value;
    }

    private static string Js(string s) => JsonSerializer.Serialize(s);

    [Fact]
    public void A_mode_press_marks_at_once_disables_the_group_and_a_second_click_sends_nothing()
    {
        string page = Page();
        string script = $$"""
            var modeError = '', modeErrorCode = '', modePress = null, writeSeq = 0, lastNow = { manual: 'auto' }, painted = [];
            const fetches = []; let release;
            function fetch(u, o) { fetches.push(u + ' ' + o.body); return new Promise((r) => { release = () => r({ ok: true, status: 202, json: () => Promise.resolve({ ok: true }) }); }); }
            let loads = 0, settles = 0;
            function load() { loads++; return Promise.resolve(); }
            async function settle() { settles++; }
            function t(k) { return k; }
            function noteText(n) { return n.text; }
            function $() { return { hidden: true, lastChild: {} }; }
            function paintMode() { painted.push(modePress); }
            const mk = (m) => ({ dataset: { m }, disabled: false, textContent: m });
            const all = [mk('auto'), mk('eco'), mk('max')];
            eval({{Js(FunctionText(page, "pressMode"))}});
            const p1 = pressMode(all[2], all), p2 = pressMode(all[1], all);
            const during = { fetches: fetches.length, disabled: all.map((b) => b.disabled), press: modePress, painted: painted.slice(), loads };
            release();
            Promise.all([p1, p2]).then(() => console.log(JSON.stringify({ during, fetches, disabled: all.map((b) => b.disabled), press: modePress, loads, settles, writeSeq })));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var r = doc.RootElement;
        var during = r.GetProperty("during");
        Assert.Equal(1, during.GetProperty("fetches").GetInt32());   // the double click sent one POST
        Assert.All(during.GetProperty("disabled").EnumerateArray(), d => Assert.True(d.GetBoolean()));
        Assert.Equal("max", during.GetProperty("press").GetString());
        Assert.Equal("max", during.GetProperty("painted")[0].GetString());   // marked before the reply
        Assert.Equal(0, during.GetProperty("loads").GetInt32());
        Assert.Equal("/api/mode {\"mode\":\"max\"}", r.GetProperty("fetches")[0].GetString());
        Assert.All(r.GetProperty("disabled").EnumerateArray(), d => Assert.False(d.GetBoolean()));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("press").ValueKind);
        Assert.Equal(1, r.GetProperty("loads").GetInt32());
        Assert.Equal(1, r.GetProperty("settles").GetInt32());   // 202: followed until applied
        Assert.Equal(2, r.GetProperty("writeSeq").GetInt32());   // reads sent across the press are dropped
    }

    [Fact]
    public void A_refused_press_settles_nothing_and_keeps_the_reason()
    {
        string page = Page();
        string script = $$"""
            var modeError = '', modeErrorCode = '', modePress = null, writeSeq = 0, lastNow = null;
            function fetch() { return Promise.resolve({ ok: false, status: 409, json: () => Promise.resolve({ code: 'max-held', message: 'Max held back' }) }); }
            let settles = 0; function load() { return Promise.resolve(); } async function settle() { settles++; }
            function t(k) { return k; } function noteText(n) { return 'note:' + n.code; }
            function $() { return { hidden: true, lastChild: {} }; } function paintMode() {}
            const all = [{ dataset: { m: 'max' }, disabled: false, textContent: 'Max' }];
            eval({{Js(FunctionText(page, "pressMode"))}});
            pressMode(all[0], all).then(() => console.log(JSON.stringify({ modeError, modeErrorCode, settles })));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        Assert.Equal("note:max-held", doc.RootElement.GetProperty("modeError").GetString());
        Assert.Equal("max-held", doc.RootElement.GetProperty("modeErrorCode").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("settles").GetInt32());
    }

    /// <summary>
    /// The whole page script, booted in Jint over a stub DOM (every element a recording node), with the page's own start
    /// (the first load) cut off: the test sets the state and calls the page's functions itself.
    /// </summary>
    private static string WholePage(string page, string test)
    {
        string p = page.Replace("\r\n", "\n", StringComparison.Ordinal);
        var m = Regex.Match(p, @"<script>\n(.*)</script></body></html>", RegexOptions.Singleline);
        Assert.True(m.Success, "the page script is not where the test reads it");
        const string boot = "load().then(() => { settle(); schedule(); });";
        Assert.Contains(boot, m.Groups[1].Value, StringComparison.Ordinal);
        return """
            var __els = {};
            function __node(name) {
              const store = {};
              return new Proxy(function () {}, {
                get(t, k) {
                  if (Object.prototype.hasOwnProperty.call(store, k)) return store[k];
                  if (k === Symbol.toPrimitive) return () => '';
                  if (k === 'querySelectorAll' || k === 'getElementsByTagName') return () => [];
                  if (k === 'getBoundingClientRect') return () => ({ left: 0, top: 0, width: 1140, height: 300 });
                  if (typeof k === 'symbol') return undefined;
                  return (store[k] = __node(name + '.' + k));
                },
                set(t, k, v) { store[k] = v; return true; },
                apply() { return __node(name + '()'); },
              });
            }
            var document = { getElementById: (id) => __els[id] || (__els[id] = __node(id)), querySelectorAll: () => [], querySelector: (s) => __node(s),
              createElement: (n) => __node(n), addEventListener() {}, title: '', hidden: false, documentElement: __node('html'), body: __node('body') };
            var window = { addEventListener() {}, devicePixelRatio: 1 };
            var navigator = { language: 'en-US', languages: ['en-US'] };
            var localStorage = { getItem: () => null, setItem() {} };
            function setTimeout() { return 0; } function clearTimeout() {} function setInterval() { return 0; } function clearInterval() {}
            function fetch() { return new Promise(() => {}); }
            function getComputedStyle(e) { return __node('style'); }
            """ + "\n" + m.Groups[1].Value.Replace(boot, "", StringComparison.Ordinal) + "\n" + test;
    }

    [Fact]
    public void A_refused_mode_press_is_shown_under_the_buttons_and_never_on_the_note_chip()
    {
        // show() is the note chip's only writer: a state carrying a mode refusal (the service's and the press's own) leaves it empty
        string test = """
            const at = new Date(Date.now() - 30000).toISOString();
            lastRows = [{ at, soc: 40, outAc: 100, outDc: 0, inAc: 0, inDc: 0, mains: false, volt: 230, hz: 50 }];
            lastNow = { type: 'AC200PL', capacityWh: 2048, efficiency: 0.85, mode: '', modeCode: '', manual: 'auto', modePending: null,
              modeError: 'Max held back: battery emergency', modeErrorNote: { code: 'max-held', text: 'Max held back: battery emergency', args: [] },
              returnPlan: 'balanced', powerMode: 'normal', powerPlan: { id: 'balanced', name: 'Balanced' }, level: 'emergency', cut: true,
              grid: 'off', silent: false, note: '', notes: [], dryRun: false, hoursLeft: 2, ratePerHour: 10, thresholds: { stopAt: 10, emergencyAt: 20 },
              dtekWatcher: { state: 'off', notes: [] } };
            modeError = 'Max held back: battery emergency'; modeErrorCode = 'max-held';
            show();
            const nc = $('noteChip'), line = $('modeLine');
            console.log(JSON.stringify({ chipHidden: nc.hidden, chipText: nc.lastChild.textContent, line: line.textContent, lineHidden: line.hidden }));
            """;
        using var doc = JsonDocument.Parse(RunScript(WholePage(Page(), test)));
        var r = doc.RootElement;
        Assert.True(r.GetProperty("chipHidden").GetBoolean());   // the refusal has one place: the line under the buttons
        Assert.Equal("", r.GetProperty("chipText").GetString());
        Assert.False(r.GetProperty("lineHidden").GetBoolean());
        Assert.Equal("Max held back: battery emergency", r.GetProperty("line").GetString());
    }

    [Fact]
    public void A_mode_refusal_outlives_an_after_eco_press_and_goes_at_the_next_mode_press()
    {
        string page = Page();
        string script = $$"""
            var modeError = '', modeErrorCode = '', modePress = null, planPress = null, planError = '', writeSeq = 0, lastNow = null;
            let answers = [];
            function fetch() { const a = answers.shift(); return Promise.resolve({ ok: a.status < 300, status: a.status, json: () => Promise.resolve(a.body) }); }
            function load() { return Promise.resolve(); } async function settle() {}
            function t(k) { return k; } function noteText(n) { return 'note:' + n.code; }
            const lines = []; function paintMode() { lines.push([modeError, modeErrorCode, planError]); }
            const mk = (k, v) => ({ dataset: { [k]: v }, disabled: false, textContent: v, setAttribute() {}, removeAttribute() {} });
            eval({{Js(FunctionText(page, "pressMode"))}});
            eval({{Js(FunctionText(page, "pressPlan"))}});
            answers = [{ status: 409, body: { code: 'max-held', message: 'Max held back' } }, { status: 200, body: { ok: true } },
              { status: 500, body: { message: 'disk full' } }, { status: 202, body: { ok: true } }];
            pressMode(mk('m', 'max'), [mk('m', 'max')])
              .then(() => pressPlan(mk('p', 'high'), [mk('p', 'high')]))
              .then(() => pressPlan(mk('p', 'balanced'), [mk('p', 'balanced')]))
              .then(() => pressMode(mk('m', 'auto'), [mk('m', 'auto')]))
              .then(() => console.log(JSON.stringify(lines)));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => e.GetRawText()).ToArray();
        // one paint per reply (no /api/now answer yet, so a mode press paints nothing at its start)
        Assert.Equal(4, got.Length);
        Assert.Equal("[\"note:max-held\",\"max-held\",\"\"]", got[0]);                       // 409 on Max
        Assert.Equal("[\"note:max-held\",\"max-held\",\"\"]", got[1]);                       // After eco saved: the refusal stays
        Assert.Equal("[\"note:max-held\",\"max-held\",\"afterEco: balanced: disk full\"]", got[2]);   // its own failure beside it
        Assert.Equal("[\"\",\"\",\"\"]", got[3]);                                            // the next mode press clears both
    }

    [Fact]
    public void The_line_shows_a_mode_refusal_and_an_after_eco_failure_together()
    {
        string page = Page();
        string script = $$"""
            var modeError = 'Max held back', modeErrorCode = 'max-held', modePress = null, planError = '';
            function t(k, p) { return p ? k + JSON.stringify(p) : k; } function noteText(n) { return 'note:' + n.code; }
            {{Const(page, "MODE_KEY")}}
            {{Const(page, "ACTIVE_PLAN_KEY")}}
            {{Const(page, "HELD_CODES")}}
            const els = {}; function $(id) { return els[id] || (els[id] = { hidden: true, lastChild: {} }); }
            var document = { querySelectorAll: () => [] };
            eval({{Js(FunctionText(page, "modeLine"))}});
            eval({{Js(FunctionText(page, "paintMode"))}});
            const D = { modeError: '', modePending: null, powerMode: 'normal', powerPlan: { id: 'balanced', name: 'Balanced' } };
            paintMode(D); const a = [els.modeLine.textContent, els.modeLine.className];
            planError = 'afterEco: high: disk full'; paintMode(D); const b = [els.modeLine.textContent, els.modeLine.className];
            console.log(JSON.stringify([a, b]));
            """;
        Assert.Equal("[[\"note:max-held\",\"hint warn\"],[\"note:max-held · afterEco: high: disk full\",\"hint bad\"]]", RunScript(script));
    }

    [Fact]
    public void The_line_under_the_buttons_says_applying_held_failed_or_what_is_in_force()
    {
        string page = Page();
        string script = $$"""
            function t(k, p) { return p ? k + JSON.stringify(p) : k; }
            function noteText(n) { return 'note:' + n.code; }
            {{Const(page, "MODE_KEY")}}
            {{Const(page, "ACTIVE_PLAN_KEY")}}
            {{Const(page, "HELD_CODES")}}
            eval({{Js(FunctionText(page, "modeLine"))}});
            const base = { modeError: '', modePending: null, powerMode: 'normal', powerPlan: { id: 'balanced', name: 'Balanced' } };
            console.log(JSON.stringify([
              modeLine(base, 'max'),
              modeLine({ ...base, modePending: 'eco' }, null),
              modeLine({ ...base, modeError: 'Max held back', modeErrorNote: { code: 'max-held' } }, null),
              modeLine({ ...base, modeError: 'Eco: exit 2', modeErrorNote: { code: 'mode-failed' }, modePending: 'eco' }, null),
              modeLine(base, null),
              modeLine({ ...base, powerMode: 'eco', powerPlan: { id: 'other', name: 'My plan' } }, null),
              modeLine({ ...base, powerMode: 'unreadable', powerPlan: { id: '', name: '' } }, null),
              modeLine(base, null, 'Max held back', 'max-held'),
              modeLine({ ...base, modePending: 'eco' }, null, 'Eco: service not answering', ''),
            ]));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.GetProperty("text").GetString())).ToArray();
        Assert.Equal(("pend", "modeApplying{\"m\":\"modeMax\"}"), got[0]);
        Assert.Equal(("pend", "modeApplying{\"m\":\"modeEco\"}"), got[1]);
        Assert.Equal(("warn", "note:max-held"), got[2]);
        Assert.Equal(("bad", "note:mode-failed"), got[3]);   // a failure beats a pending retry
        Assert.Equal(("", "modeNowPlan{\"m\":\"pmNormal\",\"p\":\"activeBalanced\"}"), got[4]);
        Assert.Equal(("", "modeNowPlan{\"m\":\"pmEco\",\"p\":\"My plan\"}"), got[5]);
        Assert.Equal(("", "modeNow{\"m\":\"pmUnknown\"}"), got[6]);
        Assert.Equal(("warn", "Max held back"), got[7]);   // a 409 at once, never saved by the service
        Assert.Equal(("bad", "Eco: service not answering"), got[8]);   // the own failure beats a stale pending
    }

    [Fact]
    public void An_answer_older_than_the_shown_one_or_sent_before_a_write_is_dropped()
    {
        string page = Page();
        string script = $$"""
            var shownSeq = 0, writeSeq = 0;
            eval({{Js(FunctionText(page, "takeAnswer"))}});
            const out = [takeAnswer(1, 0), takeAnswer(1, 0), takeAnswer(3, 0), takeAnswer(2, 0)];
            writeSeq = 1;
            out.push(takeAnswer(4, 0), takeAnswer(5, 1));
            console.log(JSON.stringify(out));
            """;
        Assert.Equal("[true,false,true,false,false,true]", RunScript(script));
    }

    [Fact]
    public void Every_mode_line_key_exists_in_both_languages()
    {
        var (en, uk) = Dictionaries(Page().Replace("\r\n", "\n", StringComparison.Ordinal));
        string[] keys = ["modeApplying", "modeNow", "modeNowPlan", "pmNormal", "pmEco", "pmEmergency", "pmUnknown", "activeHigh", "activeBalanced", "activeSaver"];
        Assert.DoesNotContain(keys, k => !en.Contains(k) || !uk.Contains(k));
    }
    [Fact]
    public void An_after_eco_press_shows_at_once_and_a_second_click_sends_nothing()
    {
        string page = Page();
        string script = $$"""
            var modeError = '', modeErrorCode = '', planPress = null, writeSeq = 0, lastNow = null;
            const fetches = []; let release;
            function fetch(u, o) { fetches.push(o.body); return new Promise((r) => { release = () => r({ ok: true, status: 200, json: () => Promise.resolve({ ok: true }) }); }); }
            let loads = 0; function load() { loads++; return Promise.resolve(); }
            function t(k) { return k; } function $() { return { hidden: true, lastChild: {} }; } function paintMode() {}
            const mk = (p) => { const a = {}; return { dataset: { p }, disabled: false, textContent: p, attrs: a, setAttribute: (k, v) => { a[k] = v; }, removeAttribute: (k) => { delete a[k]; } }; };
            const all = [mk('balanced'), mk('high'), mk('previous')];
            eval({{Js(FunctionText(page, "pressPlan"))}});
            const p1 = pressPlan(all[2], all), p2 = pressPlan(all[0], all);
            const during = { fetches: fetches.length, disabled: all.map((b) => b.disabled), pressed: all.map((b) => b.attrs['aria-pressed']), pending: all.map((b) => 'data-pending' in b.attrs) };
            release();
            Promise.all([p1, p2]).then(() => console.log(JSON.stringify({ during, body: fetches[0], disabled: all.map((b) => b.disabled), pending: all.map((b) => 'data-pending' in b.attrs), loads })));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var r = doc.RootElement; var during = r.GetProperty("during");
        Assert.Equal(1, during.GetProperty("fetches").GetInt32());
        Assert.Equal("[true,true,true]", during.GetProperty("disabled").GetRawText());
        Assert.Equal("[\"false\",\"false\",\"true\"]", during.GetProperty("pressed").GetRawText());
        Assert.Equal("[false,false,true]", during.GetProperty("pending").GetRawText());
        Assert.Equal("{\"returnPlan\":\"previous\"}", r.GetProperty("body").GetString());
        Assert.Equal("[false,false,false]", r.GetProperty("disabled").GetRawText());
        Assert.Equal("[false,false,false]", r.GetProperty("pending").GetRawText());
        Assert.Equal(1, r.GetProperty("loads").GetInt32());
    }
}