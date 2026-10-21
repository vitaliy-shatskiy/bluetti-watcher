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
            var modeError = '', modePress = null, writeSeq = 0, lastNow = { manual: 'auto' }, painted = [];
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
            var modeError = '', modePress = null, writeSeq = 0, lastNow = null;
            function fetch() { return Promise.resolve({ ok: false, status: 409, json: () => Promise.resolve({ code: 'max-held', message: 'Max held back' }) }); }
            let settles = 0; function load() { return Promise.resolve(); } async function settle() { settles++; }
            function t(k) { return k; } function noteText(n) { return 'note:' + n.code; }
            function $() { return { hidden: true, lastChild: {} }; } function paintMode() {}
            const all = [{ dataset: { m: 'max' }, disabled: false, textContent: 'Max' }];
            eval({{Js(FunctionText(page, "pressMode"))}});
            pressMode(all[0], all).then(() => console.log(JSON.stringify({ modeError, settles })));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        Assert.Equal("note:max-held", doc.RootElement.GetProperty("modeError").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("settles").GetInt32());
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
}
