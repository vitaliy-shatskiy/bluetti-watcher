using System.Text.Json;

namespace BluettiWatcher.Tests;

/// <summary>
/// The header's power group under modes.engine v2: painted from GET /api/modes (the panel's source), never from the legacy
/// /api/now fields; a poll with nothing changed touches no attribute; a read sent before a write never paints. Run in Jint.
/// </summary>
public sealed partial class PageI18nTests
{
    [Fact]
    public void ModeChoice_v2_selection_is_what_the_header_presses()
    {
        string page = Page();
        string script = $$"""
            eval({{Js(FunctionText(page, "modeChoice"))}});
            const view = (sel, extra) => ({ activeModeId: 'eco', switching: '', pending: false, ...extra, settings: { selection: sel } });
            console.log(JSON.stringify([
              modeChoice(view({ kind: 'manual', manualModeId: 'eco', autoModeId: 'normal' }), null),
              modeChoice(view({ kind: 'auto', manualModeId: null, autoModeId: 'eco' }), null),
              modeChoice(view({ kind: 'auto', autoModeId: 'eco' }), 'max'),
              modeChoice(view({ kind: 'manual', manualModeId: 'max', autoModeId: 'eco' }, { switching: 'max', pending: true }), null),
            ]));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => e.GetRawText()).ToArray();
        Assert.Equal("""{"chosen":"eco","applying":null,"autoTo":"normal"}""", got[0]);   // the owner's Eco click stays Eco
        Assert.Equal("""{"chosen":"auto","applying":null,"autoTo":"eco"}""", got[1]);
        Assert.Equal("""{"chosen":"max","applying":"max","autoTo":"eco"}""", got[2]);    // a press in flight: pressed and switching
        Assert.Equal("""{"chosen":"max","applying":"max","autoTo":"eco"}""", got[3]);    // a 202 still switching
    }

    /// <summary>A minimal DOM: elements with attributes, children and a count of every attribute write.</summary>
    private const string HeaderDom = """
        let writes = 0;
        function El(tag) { this.tagName = tag.toUpperCase(); this.attrs = {}; this.children = []; this.dataset = {}; this.hidden = false; this.textContent = ''; this.classList = { add() {} }; }
        El.prototype.setAttribute = function (k, v) { writes++; this.attrs[k] = String(v); };
        El.prototype.getAttribute = function (k) { return k in this.attrs ? this.attrs[k] : null; };
        El.prototype.hasAttribute = function (k) { return k in this.attrs; };
        El.prototype.removeAttribute = function (k) { writes++; delete this.attrs[k]; };
        El.prototype.replaceChildren = function (...c) { writes++; this.children = c; };
        El.prototype.append = function (...c) { this.children.push(...c); };
        El.prototype.querySelectorAll = function (s) { return this.children.filter((c) => c.tagName === s.toUpperCase()); };
        const document = { createElement: (t) => new El(t) };
        const box = new El('div'), els = { modeSeg: new El('div'), modeLine: new El('div'), planLabel: new El('span'), planSeg: new El('div') };
        els.modeSeg.parentNode = box;
        function $(id) { return els[id] || null; }
        function t(k, p) { return p ? k + JSON.stringify(p) : k; }
        """;

    [Fact]
    public void PaintModesV2_shows_every_mode_and_a_repaint_with_nothing_changed_writes_nothing()
    {
        string page = Page();
        string script = $$"""
            {{HeaderDom}}
            var lang = 'en', mpPress = null, selError = '';
            var mpView = { activeModeId: 'eco', switching: '', pending: false, settings: { selection: { kind: 'auto', autoModeId: 'eco' },
              modes: [{ id: 'normal', name: 'Normal', builtIn: true }, { id: 'eco', name: 'Eco', builtIn: true }, { id: 'max', name: 'Max', builtIn: true }, { id: 'night', name: 'Night', builtIn: false }] } };
            function mpModes() { return mpView.settings.modes; }
            const picked = []; function mpSelect(id) { picked.push(id); }
            {{Const(page, "MODE_BUILT_IN")}}
            {{Const(page, "MODE_SEG_MAX")}}
            eval({{Js(FunctionText(page, "modeChoice"))}});
            eval({{Js(FunctionText(page, "modeName"))}});
            eval({{Js(FunctionText(page, "setAttr"))}});
            eval({{Js(FunctionText(page, "paintModesV2"))}});
            paintModesV2();
            const seg = els.modeSeg, btn = () => seg.children.filter((c) => c.tagName === 'BUTTON');
            const menu = seg.children.find((c) => c.tagName === 'SELECT');
            const first = { ids: btn().map((b) => b.dataset.m), menu: menu ? menu.children.map((o) => o.value) : null, pressed: btn().filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.dataset.m),
              line: els.modeLine.textContent, planHidden: els.planSeg.hidden && els.planLabel.hidden };
            writes = 0; paintModesV2(); const again = writes;
            btn()[2].onclick();
            mpView.settings.selection = { kind: 'manual', manualModeId: 'eco', autoModeId: 'eco' };
            writes = 0; paintModesV2();
            console.log(JSON.stringify({ first, again, picked, changed: writes, pressed: btn().filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.dataset.m) }));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var r = doc.RootElement;
        Assert.Equal("""["auto","normal","eco","max"]""", r.GetProperty("first").GetProperty("ids").GetRawText());
        Assert.Equal("""["","night"]""", r.GetProperty("first").GetProperty("menu").GetRawText());   // the rest go to the menu, never a second row
        Assert.Equal("""["auto"]""", r.GetProperty("first").GetProperty("pressed").GetRawText());
        Assert.Equal("""modeAutoIs{"m":"modeEco"}""", r.GetProperty("first").GetProperty("line").GetString());   // "Auto · Eco"
        Assert.True(r.GetProperty("first").GetProperty("planHidden").GetBoolean());   // After eco is the legacy engine's
        Assert.Equal(0, r.GetProperty("again").GetInt32());   // a poll with the same view repaints nothing
        Assert.Equal("""["eco"]""", r.GetProperty("picked").GetRawText());   // the header goes through the panel's select
        Assert.Equal(2, r.GetProperty("changed").GetInt32());   // only the two buttons whose state changed
        Assert.Equal("""["eco"]""", r.GetProperty("pressed").GetRawText());
    }

    [Fact]
    public void MpRefresh_answer_sent_before_a_write_never_paints()
    {
        string page = Page();
        string script = $$"""
            var mpView = { activeModeId: 'auto-old' }, mpMsg = null, mpBusy = false, mpGen = 0, mpEngine = '';
            let answer; const painted = [];
            function fetch() { return new Promise((res) => { answer = () => res({ ok: true, status: 200, json: () => Promise.resolve({ activeModeId: 'stale' }) }); }); }
            function $() { return { hidden: true }; }
            function mpPaintTop() { painted.push(mpView.activeModeId); } function mpPaintList() {} function mpPaintRules() {}
            eval({{Js(FunctionText(page, "mpRefresh"))}});
            const p = mpRefresh();
            mpGen++;   // a select went out and came back while the read was on its way
            answer();
            p.then(() => console.log(JSON.stringify({ view: mpView.activeModeId, painted })));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        Assert.Equal("auto-old", doc.RootElement.GetProperty("view").GetString());
        Assert.Equal("[]", doc.RootElement.GetProperty("painted").GetRawText());
    }
}
