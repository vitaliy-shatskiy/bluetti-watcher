using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests;

/// <summary>
/// The Power modes panel run in Jint over a small recording DOM: the settings forms generated from a fake catalogue that uses
/// every schema keyword and UI hint, the mode and rule editors, the selector, the status chips and the hibernate toggle.
/// </summary>
public sealed partial class PageI18nTests
{
    private const string PanelStart = "// --- power modes panel ---";
    private const string PanelEnd = "// --- end of the power modes panel ---";

    /// <summary>A DOM that records: elements with children, attributes and properties; walk() finds them again.</summary>
    private const string FakeDom = """
        function El(tag) { this.tagName = String(tag).toUpperCase(); this.children = []; this.attrs = {}; this.style = {}; this.dataset = {}; this.hidden = false; this.disabled = false; this.checked = false; this.value = ''; }
        El.prototype.append = function (...n) { for (const x of n) this.children.push(typeof x === 'object' ? x : { text: String(x) }); };
        El.prototype.replaceChildren = function (...n) { this.children = []; this.append(...n); };
        El.prototype.setAttribute = function (k, v) { this.attrs[k] = String(v); };
        El.prototype.removeAttribute = function (k) { delete this.attrs[k]; };
        El.prototype.getAttribute = function (k) { return k in this.attrs ? this.attrs[k] : null; };
        El.prototype.addEventListener = function (ev, f) { this['on' + ev] = f; };
        Object.defineProperty(El.prototype, 'textContent', {
          get() { return this.children.map((c) => (c.text !== undefined ? c.text : c.textContent)).join(''); },
          set(v) { this.children = [{ text: String(v) }]; } });
        const __ids = {};
        var document = { hidden: false, createElement: (t) => new El(t), createElementNS: (ns, t) => new El(t), getElementById: (id) => __ids[id] || (__ids[id] = new El('div')) };
        function $(id) { return document.getElementById(id); }
        function walk(e, f, out = []) { if (e && e.tagName) { if (f(e)) out.push(e); e.children.forEach((c) => walk(c, f, out)); } return out; }
        const F = (root, key) => walk(root, (e) => e.attrs['data-f'] === key)[0];
        const A = (root, act, id) => walk(root, (e) => e.attrs['data-act'] === act && (id === undefined || e.attrs['data-id'] === String(id)))[0];
        const errOf = (root, key) => walk(root, (e) => e.attrs['data-err'] === key).map((e) => e.textContent).join('|');
        const labelOf = (root, c) => (walk(root, (e) => e.tagName === 'LABEL' && e.htmlFor === c.id)[0] || {}).textContent;
        const byId = (root, id) => walk(root, (e) => e.id === id)[0];
        var lang = 'en';
        function t(k, p) { return p ? k + JSON.stringify(p) : k; }
        const fetches = []; let answers = [];
        function fetch(url, o) {
          o = o || {};
          fetches.push({ url, method: o.method || 'GET', station: (o.headers || {})['X-Station'] || null, body: o.body === undefined ? null : o.body });
          const a = answers.shift() || { status: 200, body: VIEW };
          return Promise.resolve({ ok: a.status < 300, status: a.status, json: () => Promise.resolve(JSON.parse(JSON.stringify(a.body))),
            text: () => Promise.resolve(typeof a.body === 'string' ? a.body : JSON.stringify(a.body)) });
        }
        let loads = 0; function load() { loads++; return Promise.resolve(); }
        let confirmAnswer = true; const asks = []; function confirm(q) { asks.push(q); return confirmAnswer; }
        var POLL = 60000; function setInterval() { return 0; } function clearInterval() {}
        """;

    /// <summary>
    /// A catalogue covering every widget: a discovery select, a slider with units and order, number with help, enum select,
    /// text with maxLength, boolean with default, a multi-select of discovered ids, a free list, a multi-type field and a nested
    /// object with a dotted hint; one module unavailable.
    /// </summary>
    private const string Fixtures = """
        const CAT = { modules: [
          { descriptor: { typeId: 'test.plan', version: 1, title: 'Plan', restoration: 'reversible', needsAdmin: false, discoveryLists: ['plans'],
              settingsSchema: { type: 'object', properties: { plan: { type: 'string', title: 'Plan' } }, required: ['plan'], additionalProperties: false },
              uiHints: { plan: { widget: 'select', discovery: 'plans' } }, defaultSettings: { plan: 'p1' } },
            available: true, unavailableReason: null, discovery: { plans: [{ id: 'p1', label: 'Balanced' }, { id: 'p2', label: 'Saver' }] } },
          { descriptor: { typeId: 'test.all', version: 1, title: 'Everything', restoration: 'reversible', needsAdmin: true, discoveryLists: ['services'],
              settingsSchema: { type: 'object', required: ['mode'], properties: {
                mode: { type: 'string', enum: ['low', 'high'] },
                name: { type: 'string', maxLength: 20, title: 'Name' },
                on: { type: 'boolean', default: true },
                services: { type: 'array', items: { type: 'string' } },
                apps: { type: 'array', items: { type: 'string' } },
                limit: { type: ['string', 'number'] },
                inner: { type: 'object', properties: { depth: { type: 'integer', minimum: 1 } } },
                watts: { type: 'number', minimum: 0, maximum: 300, description: 'Board limit' },
                level: { type: 'integer', minimum: 10, maximum: 90, default: 50 } } },
              uiHints: { level: { widget: 'slider', units: '%', order: 1 }, watts: { units: 'W', order: 2 },
                services: { widget: 'multi', discovery: 'services', label: 'Services' }, 'inner.depth': { label: 'Depth' } },
              defaultSettings: { mode: 'low' } },
            available: true, unavailableReason: null, discovery: { services: [{ id: 's1', label: 'Search' }, { id: 's2', label: 'Spooler' }] } },
          { descriptor: { typeId: 'test.off', version: 1, title: 'Off here', settingsSchema: { type: 'object', properties: {} }, uiHints: {}, defaultSettings: {} },
            available: false, unavailableReason: 'no such device', discovery: {} } ] };
        const VIEW = { engine: 'v2', activeModeId: 'eco', reason: 'auto: rule 1 (mains off for 180 s)', emergency: false,
          actions: [{ index: 0, typeId: 'test.plan', status: 'applied', detail: '' }, { index: 1, typeId: 'test.all', status: 'failed', detail: 'access denied' },
            { index: 2, typeId: 'test.plan', status: 'relinquished', detail: 'changed by hand' }, { index: 3, typeId: 'x.gone', status: 'unsupported', detail: 'not registered' },
            { index: 4, typeId: 'test.plan', status: 'satisfied', detail: '' }],
          restores: [{ key: 'gpu/0/powerLimit', typeId: 'test.all', status: 'restore-failed', detail: '3 tries' }],
          problems: ['journal unreadable: moved aside'],
          settings: { version: 1, fallbackModeId: 'normal', autoHibernate: true, selection: { kind: 'auto', autoModeId: 'eco', autoRule: 0 },
            modes: [{ id: 'normal', name: 'Normal', flagWord: 'normal', builtIn: true, actions: [] },
              { id: 'eco', name: 'Eco', flagWord: 'eco', builtIn: true, actions: [{ typeId: 'test.plan', enabled: true, enforce: false, settings: { plan: 'gone-guid' } }] },
              { id: 'quiet', name: 'Quiet', flagWord: 'normal', builtIn: false, actions: [{ typeId: 'x.gone', enabled: true, enforce: true, settings: { a: 1, b: [2] } }] }],
            rules: [{ condition: { kind: 'mainsOffFor', seconds: 180, percent: 0, minutes: 0, from: '', to: '', hysteresis: 0 }, modeId: 'eco' },
              { condition: { kind: 'mainsOnFor', seconds: 180, percent: 0, minutes: 0, from: '', to: '', hysteresis: 0 }, modeId: 'normal' }] } };
        mpCat = JSON.parse(JSON.stringify(CAT)); mpView = JSON.parse(JSON.stringify(VIEW));
        """;

    private static string Panel(string page)
    {
        page = page.Replace("\r\n", "\n", StringComparison.Ordinal);
        int a = page.IndexOf(PanelStart, StringComparison.Ordinal), b = page.IndexOf(PanelEnd, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "the power modes panel block is not where the test reads it");
        return page[a..b];
    }

    /// <summary>The GPU limit's own editor: a block of its own outside the panel, which names no module.</summary>
    private static string GpuEditor()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        int a = page.IndexOf("// --- gpu limit editor ---", StringComparison.Ordinal), b = page.IndexOf("// --- end of the gpu limit editor ---", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "the gpu limit editor block is not where the test reads it");
        return page[a..b] + "\n";
    }

    /// <summary>The panel's script with the fake DOM, the stubs and the fixtures, then <paramref name="test"/>; its console.log output parsed.</summary>
    private static JsonElement RunPanel(string test)
    {
        string page = Page();
        using var doc = JsonDocument.Parse(RunScript(FakeDom + "\n" + Const(page, "fmt") + "\n" + Panel(page) + "\n" + Fixtures + "\n" + test));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_form_generator_gives_each_schema_keyword_and_hint_its_widget()
    {
        var r = RunPanel("""
            const e = mpModule('test.all'), s = { mode: 'low' }, root = new El('div');
            root.append(...mpForm(e.descriptor.settingsSchema, e.descriptor.uiHints, s, e, 'a0', {}, new Set(), ''));
            const d = (k) => F(root, 'a0.' + k), lab = (k) => labelOf(root, d(k));
            const help = byId(root, d('watts').attrs['aria-describedby']);
            console.log(JSON.stringify({
              order: walk(root, (x) => x.attrs['data-f'] && !x.attrs['data-f'].endsWith('[]')).map((x) => x.attrs['data-f']),
              level: [d('level').type, d('level').min, d('level').max, d('level').step, String(d('level').value), walk(root, (x) => x.tagName === 'OUTPUT')[0].textContent],
              watts: [d('watts').type, d('watts').min, d('watts').max, d('watts').step, help && help.textContent, walk(root, (x) => x.className === 'dim' && x.textContent === 'W').length],
              mode: [d('mode').tagName, d('mode').children.map((o) => o.value), d('mode').value, d('mode').attrs['aria-required'] || null, lab('mode')],
              name: [d('name').type, d('name').maxLength, lab('name')],
              on: [d('on').attrs.role, d('on').attrs['aria-checked']],
              services: [d('services').attrs.role, walk(root, (x) => x.attrs['data-f'] === 'a0.services[]').map((b) => b.value + '=' + walk(d('services'), (l) => l.tagName === 'LABEL' && l.children.includes(b))[0].textContent), byId(root, d('services').attrs['aria-labelledby']).textContent],
              apps: d('apps').tagName, limit: [d('limit').tagName, d('limit').type],
              inner: [d('inner').tagName, d('inner.depth').type, d('inner.depth').min, d('inner.depth').step, lab('inner.depth')],
              written: s }));
            """);
        Assert.Equal("""["a0.level","a0.watts","a0.mode","a0.name","a0.on","a0.services","a0.apps","a0.limit","a0.inner","a0.inner.depth"]""", r.GetProperty("order").GetRawText());
        Assert.Equal("""["range",10,90,1,"50","50 %"]""", r.GetProperty("level").GetRawText());
        Assert.Equal("""["number",0,300,"any","Board limit",1]""", r.GetProperty("watts").GetRawText());
        Assert.Equal("""["SELECT",["low","high"],"low","true","mode"]""", r.GetProperty("mode").GetRawText());   // required is said to assistive tech, never by an asterisk
        Assert.Equal("""["text",20,"Name"]""", r.GetProperty("name").GetRawText());
        Assert.Equal("""["switch","true"]""", r.GetProperty("on").GetRawText());
        Assert.Equal("""["group",["s1=Search","s2=Spooler"],"Services"]""", r.GetProperty("services").GetRawText());
        Assert.Equal("\"TEXTAREA\"", r.GetProperty("apps").GetRawText());
        Assert.Equal("""["INPUT","text"]""", r.GetProperty("limit").GetRawText());
        Assert.Equal("""["FIELDSET","number",1,1,"Depth"]""", r.GetProperty("inner").GetRawText());
        // what the form shows is what it saves: the defaults are written, nothing else is invented
        Assert.Equal("""{"mode":"low","level":50,"on":true,"inner":{}}""", r.GetProperty("written").GetRawText());
    }

    [Fact]
    public void Each_widget_writes_the_type_its_schema_wants()
    {
        var r = RunPanel("""
            const e = mpModule('test.all'), s = { mode: 'low' }, root = new El('div');
            root.append(...mpForm(e.descriptor.settingsSchema, e.descriptor.uiHints, s, e, 'a0', {}, new Set(), ''));
            const put = (k, v, ev) => { const c = F(root, 'a0.' + k); c.value = v; c[ev || 'oninput'](); };
            put('level', '70'); put('watts', '12,5'); const w1 = s.watts; put('watts', ''); const wGone = !('watts' in s);
            put('mode', 'high', 'onchange');
            F(root, 'a0.on').onclick();
            const boxes = walk(root, (x) => x.attrs['data-f'] === 'a0.services[]'); boxes[1].onclick();
            put('apps', 'a\n\n b '); put('limit', '42'); const l1 = s.limit; put('limit', 'min'); put('inner.depth', '3');
            console.log(JSON.stringify({ s, w1, wGone, l1, int: mpCoerce({ type: 'integer' }, '1.5'), bool: mpCoerce({ type: ['boolean', 'string'] }, 'true'), str: mpCoerce({ type: 'string' }, '42') }));
            """);
        var s = r.GetProperty("s");
        Assert.Equal(70, s.GetProperty("level").GetInt32());
        Assert.Equal(12.5, r.GetProperty("w1").GetDouble());
        Assert.True(r.GetProperty("wGone").GetBoolean());   // an emptied number is left out, never saved as 0
        Assert.Equal("high", s.GetProperty("mode").GetString());
        Assert.False(s.GetProperty("on").GetBoolean());
        Assert.Equal("""["s2"]""", s.GetProperty("services").GetRawText());
        Assert.Equal("""["a","b"]""", s.GetProperty("apps").GetRawText());
        Assert.Equal(42, r.GetProperty("l1").GetInt32());
        Assert.Equal("min", s.GetProperty("limit").GetString());
        Assert.Equal("""{"depth":3}""", s.GetProperty("inner").GetRawText());
        Assert.Equal("1.5", r.GetProperty("int").GetString());   // not an integer: sent as typed, the service names the field
        Assert.True(r.GetProperty("bool").GetBoolean());
        Assert.Equal("42", r.GetProperty("str").GetString());
    }

    [Fact]
    public void A_discovered_value_missing_here_stays_chosen_and_a_chosen_id_stays_listed()
    {
        var r = RunPanel("""
            const plan = mpModule('test.plan'), s1 = { plan: 'gone-guid' }, r1 = new El('div');
            r1.append(...mpForm(plan.descriptor.settingsSchema, plan.descriptor.uiHints, s1, plan, 'a0', {}, new Set(), ''));
            const all = mpModule('test.all'), s2 = { mode: 'low', services: ['s9', 's1'] }, r2 = new El('div');
            r2.append(...mpForm(all.descriptor.settingsSchema, all.descriptor.uiHints, s2, all, 'a1', {}, new Set(), ''));
            const sel = F(r1, 'a0.plan'), boxes = walk(r2, (x) => x.attrs['data-f'] === 'a1.services[]');
            const shown = boxes.map((b) => [b.value, b.attrs['aria-checked'] === 'true']);
            boxes[0].onclick(); boxes[0].onclick();
            console.log(JSON.stringify({ options: sel.children.map((o) => [o.value, o.textContent]), value: sel.value, s1,
              boxes: shown, s2: s2.services }));
            """);
        Assert.Equal("""[["p1","Balanced"],["p2","Saver"],["gone-guid","modesMissing{\"v\":\"gone-guid\"}"]]""", r.GetProperty("options").GetRawText());
        Assert.Equal("gone-guid", r.GetProperty("value").GetString());
        Assert.Equal("""{"plan":"gone-guid"}""", r.GetProperty("s1").GetRawText());
        Assert.Equal("""[["s1",true],["s2",false],["s9",true]]""", r.GetProperty("boxes").GetRawText());
        Assert.Equal("""["s1","s9"]""", r.GetProperty("s2").GetRawText());
    }

    [Fact]
    public void The_editor_has_no_browser_checkbox_no_asterisk_and_each_block_a_switch_in_its_header()
    {
        var r = RunPanel("""
            mpEditMode('eco');
            const ed = $('modesEdit');
            const blocks = walk(ed, (x) => x.tagName === 'FIELDSET' && /^a\d+$/.test(x.attrs['data-f'] || ''));
            console.log(JSON.stringify({
              native: walk(ed, (x) => x.tagName === 'INPUT' && x.type === 'checkbox').length,
              stars: walk(ed, (x) => x.tagName === 'LABEL' || x.tagName === 'LEGEND' || x.tagName === 'SPAN').filter((x) => /\*\s*$/.test(x.textContent)).length,
              heads: blocks.map((b) => { const h = b.children[0], sw = walk(h, (x) => x.attrs.role === 'switch')[0]; return [h.className, !!sw && sw.attrs['data-f'], !!sw && byId(ed, sw.attrs['aria-labelledby']) === walk(h, (x) => x.className === 't')[0]]; }) }));
            """);
        Assert.Equal(0, r.GetProperty("native").GetInt32());
        Assert.Equal(0, r.GetProperty("stars").GetInt32());
        Assert.Equal("""[["ah","a0.enabled",true]]""", r.GetProperty("heads").GetRawText());
    }

    [Fact]
    public void The_gpu_limit_is_one_choice_in_watts_and_an_old_percent_stays_as_it_is_until_edited()
    {
        var r = RunPanel(GpuEditor() + """
            const e = { descriptor: { typeId: 'gpu.powerLimit', settingsSchema: { type: 'object', properties: {} }, uiHints: {} }, available: true,
              discovery: { gpus: [{ id: '0', label: 'Demo GPU (150..400 W, default 320 W)', numbers: { min: 150, max: 400, default: 320 } }] } };
            const s = { gpu: '0', percent: 70 }, root = new El('div');
            root.append(...gpuLimitForm(s, e, 'a1', {}, new Set()));
            const pressed = () => ['gpu-min', 'gpu-own'].map((k) => A(root, k).attrs['aria-pressed']);
            const look = { selects: walk(root, (x) => x.tagName === 'SELECT').length, card: F(root, 'a1.gpu').textContent, min: A(root, 'gpu-min').textContent,
              pressed: pressed(), watts: F(root, 'a1.watts').value, hint: walk(root, (x) => x.className === 'help')[0].textContent, untouched: JSON.stringify(s) };
            A(root, 'gpu-more').onclick();
            const stepped = JSON.stringify(s);
            A(root, 'gpu-min').onclick();
            const lowest = [JSON.stringify(s), pressed(), !!F(root, 'a1.watts')];
            A(root, 'gpu-own').onclick();
            console.log(JSON.stringify({ look, stepped, lowest, own: [JSON.stringify(s), F(root, 'a1.watts').value] }));
            """);
        Assert.Equal("""{"selects":0,"card":"Demo GPU","min":"gpuMinW{\"w\":150}","pressed":["false","true"],"watts":"224","hint":"gpuRange{\"min\":150,\"max\":400,\"d\":320}","untouched":"{\"gpu\":\"0\",\"percent\":70}"}""", r.GetProperty("look").GetRawText());
        Assert.Equal("""{"gpu":"0","watts":234}""", r.GetProperty("stepped").GetString());
        Assert.Equal("""["{\"gpu\":\"0\",\"limit\":\"min\"}",["true","false"],false]""", r.GetProperty("lowest").GetRawText());
        Assert.Equal("""["{\"gpu\":\"0\",\"watts\":320}","320"]""", r.GetProperty("own").GetRawText());
    }

    [Fact]
    public void A_table_cell_whose_saved_value_is_the_only_choice_is_its_name_as_text_without_the_services_notes()
    {
        var r = RunPanel("""
            const e = { descriptor: { typeId: 'test.rows', settingsSchema: { type: 'object', properties: { monitors: { type: 'array', items: { type: 'object', properties: { monitor: { type: 'string' }, hz: { type: 'integer' } } } } } },
              uiHints: { monitors: { list: 'monitors' } } }, available: true, discovery: { monitors: [{ id: 'm1', label: 'LC27G7xT (2560x1440, 240 Hz; offers 60, 240 Hz)' }] } };
            const s = { monitors: [{ monitor: 'm1', hz: 120 }, { hz: 60 }] }, root = new El('div');
            root.append(...mpForm(e.descriptor.settingsSchema, e.descriptor.uiHints, s, e, 'a0', {}, new Set(), ''));
            console.log(JSON.stringify({ selects: walk(root, (x) => x.tagName === 'SELECT').length, plain: walk(root, (x) => x.className === 'plain').map((x) => x.textContent), s }));
            """);
        Assert.Equal("""{"selects":1,"plain":["LC27G7xT"],"s":{"monitors":[{"monitor":"m1","hz":120},{"hz":60}]}}""", r.GetRawText());   // the saved one as text; an empty row keeps its list; nothing written
    }

    [Fact]
    public void An_empty_value_with_one_choice_offered_stays_empty_and_keeps_its_list()
    {
        var r = RunPanel("""
            const e = { descriptor: { typeId: 'test.cmd', settingsSchema: { type: 'object', required: ['leave', 'enter'], properties: { leave: { type: 'string' }, enter: { type: 'string' } } },
              uiHints: { leave: { list: 'cmds' }, enter: { list: 'cmds' } } }, available: true, discovery: { cmds: [{ id: 'eco', label: 'eco (pwsh.exe)' }] } };
            const s = { leave: '', enter: 'eco' }, root = new El('div');
            root.append(...mpForm(e.descriptor.settingsSchema, e.descriptor.uiHints, s, e, 'a0', {}, new Set(), ''));
            console.log(JSON.stringify({ leave: F(root, 'a0.leave').tagName, enter: [F(root, 'a0.enter').tagName, F(root, 'a0.enter').textContent], s }));
            """);
        Assert.Equal("""{"leave":"SELECT","enter":["SPAN","eco"],"s":{"leave":"","enter":"eco"}}""", r.GetRawText());   // empty means none here: never filled in on its own
    }

    [Fact]
    public void Server_problems_map_to_their_fields()
    {
        var r = RunPanel("""
            console.log(JSON.stringify(mpProblems('actions[2].settings.apps[1]: longer than 5; actions[0]: missing; actions[1].settings: expected "object", got null; rules[3].condition.seconds: 0 to 86400; rules[1].modeId: no mode x; name: bad; odd')));
            """);
        Assert.Equal("""{"a2.apps":"longer than 5","a0":"missing","a1":"expected \"object\", got null","r3.seconds":"0 to 86400","r1.modeId":"no mode x","name":"bad","":"odd"}""", r.GetRawText());
    }

    [Fact]
    public void A_refused_mode_save_shows_each_problem_next_to_its_field_and_keeps_the_editor()
    {
        var r = RunPanel("""
            (async () => {
              mpEditMode('eco');
              const n = F($('modesEdit'), 'name'); n.value = ''; n.oninput();
              answers = [{ status: 400, body: 'actions[0].settings.plan: not one of the plans; name: 1 to 64 characters; flag: odd' }];
              await mpSaveMode();
              const ed = $('modesEdit'), plan = F(ed, 'a0.plan');
              console.log(JSON.stringify({ req: fetches[0], open: !ed.hidden && mpDraft !== null,
                plan: [plan.attrs['aria-invalid'], errOf(ed, 'a0.plan'), byId(ed, plan.attrs['aria-describedby']).textContent],
                name: errOf(ed, 'name'), rest: errOf(ed, 'flag'), msg: walk(ed, (x) => x.className === 'msg bad').map((x) => x.textContent) }));
            })();
            """);
        var req = r.GetProperty("req");
        Assert.Equal(("PUT", "/api/modes/eco", "1"), (req.GetProperty("method").GetString(), req.GetProperty("url").GetString(), req.GetProperty("station").GetString()));
        Assert.Equal("""{"id":"eco","name":"","flagWord":"eco","actions":[{"typeId":"test.plan","enabled":true,"enforce":false,"settings":{"plan":"gone-guid"}}]}""", req.GetProperty("body").GetString());
        Assert.True(r.GetProperty("open").GetBoolean());
        Assert.Equal("""["true","not one of the plans","not one of the plans"]""", r.GetProperty("plan").GetRawText());
        Assert.Equal("1 to 64 characters", r.GetProperty("name").GetString());
        Assert.Equal("flag: odd", r.GetProperty("rest").GetString());   // a problem with no field of its own is said at the top
        Assert.Equal("""["modesFix"]""", r.GetProperty("msg").GetRawText());
    }

    [Fact]
    public void A_new_mode_takes_its_id_from_the_name_its_actions_start_from_the_defaults_and_is_posted()
    {
        var r = RunPanel("""
            (async () => {
              mpNewMode();
              let ed = $('modesEdit');
              const n = F(ed, 'name'); n.value = 'Night Quiet!'; n.oninput();
              const id = F(ed, 'id').value;
              F(ed, 'add-type').value = 'test.plan'; A(ed, 'add-action').onclick();
              ed = $('modesEdit'); F(ed, 'add-type').value = 'test.all'; A(ed, 'add-action').onclick();
              ed = $('modesEdit');
              const p = F(ed, 'a0.plan'); p.value = 'p2'; p.onchange();
              F(ed, 'a1.enforce').onclick();
              F(ed, 'a0.enabled').onclick();
              answers = [{ status: 200, body: VIEW }];
              await mpSaveMode();
              console.log(JSON.stringify({ id, req: fetches[0], catDefault: mpModule('test.plan').descriptor.defaultSettings, closed: $('modesEdit').hidden && mpDraft === null, msg: mpMsg }));
            })();
            """);
        Assert.Equal("night-quiet", r.GetProperty("id").GetString());
        var req = r.GetProperty("req");
        Assert.Equal(("POST", "/api/modes", "1"), (req.GetProperty("method").GetString(), req.GetProperty("url").GetString(), req.GetProperty("station").GetString()));
        // the untouched nested object is left out; the defaults the form showed are sent
        Assert.Equal("""{"id":"night-quiet","name":"Night Quiet!","flagWord":"normal","actions":[{"typeId":"test.plan","enabled":false,"enforce":false,"settings":{"plan":"p2"}},{"typeId":"test.all","enabled":true,"enforce":true,"settings":{"mode":"low","level":50,"on":true}}]}""",
            req.GetProperty("body").GetString());
        Assert.Equal("""{"plan":"p1"}""", r.GetProperty("catDefault").GetRawText());   // the catalogue's defaults are copied, never edited
        Assert.True(r.GetProperty("closed").GetBoolean());
        Assert.Equal("""{"kind":"ok","text":"modesSaved"}""", r.GetProperty("msg").GetRawText());
    }

    [Fact]
    public void An_action_of_a_type_this_build_does_not_know_is_saved_unchanged()
    {
        var r = RunPanel("""
            (async () => {
              mpEditMode('quiet');
              const says = $('modesEdit').textContent.includes('modesUnknownType');
              await mpSaveMode();
              console.log(JSON.stringify({ says, body: fetches[0].body }));
            })();
            """);
        Assert.True(r.GetProperty("says").GetBoolean());
        Assert.Equal("""{"id":"quiet","name":"Quiet","flagWord":"normal","actions":[{"typeId":"x.gone","enabled":true,"enforce":true,"settings":{"a":1,"b":[2]}}]}""", r.GetProperty("body").GetString());
    }

    [Fact]
    public void A_mode_block_is_painted_only_once_the_catalogue_is_in_never_as_bare_action_names_first()
    {
        var r = RunPanel("""
            const lis = () => walk($('modesList'), (x) => x.tagName === 'LI').map((x) => x.textContent);
            const blocks = () => walk($('modesList'), (x) => x.className === 'mode').length;
            const cat = mpCat; mpCat = null;
            mpPaintList();
            const before = { blocks: blocks(), lines: lis() };
            mpCat = cat;
            mpPaintList();
            console.log(JSON.stringify({ before, after: blocks() }));
            """);
        Assert.Equal("""{"blocks":0,"lines":[]}""", r.GetProperty("before").GetRawText());   // the view came first: nothing yet, no names-then-lines swap
        Assert.True(r.GetProperty("after").GetInt32() > 0);
    }

    [Fact]
    public void The_first_data_round_at_load_asks_the_view_and_the_catalogue_together_and_paints_the_blocks()
    {
        var r = RunPanel("""
            (async () => {
              mpCat = null; mpView = null;
              const orig = fetch; fetch = (u, o) => (String(u).includes('catalogue') ? Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(JSON.parse(JSON.stringify(CAT))) }) : orig(u, o));
              await mpStart(); await mpFirstRound;
              await Promise.resolve();
              const lis = walk($('modesList'), (x) => x.tagName === 'LI').length;
              console.log(JSON.stringify({ blocks: walk($('modesList'), (x) => x.className === 'mode').length, lis }));
            })();
            """);
        Assert.True(r.GetProperty("blocks").GetInt32() > 0);   // painted in the first round, panel closed or open
        Assert.True(r.GetProperty("lis").GetInt32() > 0);
    }

    [Fact]
    public void The_notifications_switch_turns_all_off_with_one_put_and_the_groups_show_only_when_on_and_opened()
    {
        var r = RunPanel("""
            (async () => {
              const sw = (k) => walk($('modesHib'), (x) => x.attrs['data-sw'] === k)[0];
              mpNotif = null; mpPaintTop();
              const unread = !!sw('enabled');
              mpNotif = { enabled: true, mains: true, modeChanges: false, lowBattery: true, failures: true, faults: true, overTemperature: true };
              mpPaintTop();
              const on = { role: sw('enabled').attrs.role, checked: sw('enabled').attrs['aria-checked'], tag: sw('enabled').tagName, groups: !!sw('mains') };
              A($('modesHib'), 'notif-setup').onclick();
              // every switch is named by its row's label; no browser checkbox left in the block
              const named = walk($('modesHib'), (x) => x.attrs.role === 'switch').every((s) => walk($('modesHib'), (x) => x.tagName === 'LABEL' && x.htmlFor === s.id && x.id === s.attrs['aria-labelledby']).length === 1);
              const native = walk($('modesHib'), (x) => x.tagName === 'INPUT').length;
              const opened = ['mains', 'modeChanges'].map((k) => sw(k).attrs['aria-checked']);
              answers = [{ status: 200, body: { ...mpNotif, enabled: false } }];
              await sw('enabled').onclick();
              console.log(JSON.stringify({ unread, on, opened, named, native, put: fetches.at(-1), off: sw('enabled').attrs['aria-checked'], groupsOff: !!sw('mains') }));
            })();
            """);
        Assert.False(r.GetProperty("unread").GetBoolean());   // nothing before the service's answer: never a guessed state
        Assert.Equal("""{"role":"switch","checked":"true","tag":"BUTTON","groups":false}""", r.GetProperty("on").GetRawText());
        Assert.Equal("""["true","false"]""", r.GetProperty("opened").GetRawText());
        Assert.True(r.GetProperty("named").GetBoolean());
        Assert.Equal(0, r.GetProperty("native").GetInt32());
        var put = r.GetProperty("put");
        Assert.Equal(("PUT", "/api/settings/notifications", "1", """{"enabled":false}"""), (put.GetProperty("method").GetString(), put.GetProperty("url").GetString(), put.GetProperty("station").GetString(), put.GetProperty("body").GetString()));
        Assert.Equal("false", r.GetProperty("off").GetString());
        Assert.False(r.GetProperty("groupsOff").GetBoolean());
    }

    [Fact]
    public void Built_in_modes_cannot_be_deleted_and_a_user_mode_is_deleted_only_after_the_confirm()
    {
        var r = RunPanel("""
            (async () => {
              mpPaintList();
              const list = $('modesList'), has = ['normal', 'eco', 'quiet'].map((id) => !!A(list, 'delete', id));
              const tags = walk(list, (x) => x.className === 'tag').length, act = walk(list, (x) => x.className === 'act').map((x) => x.textContent);
              confirmAnswer = false; await A(list, 'delete', 'quiet').onclick();
              const afterNo = fetches.length;
              confirmAnswer = true; answers = [{ status: 409, body: 'a rule chooses quiet: change the rules first' }];
              await A($('modesList'), 'delete', 'quiet').onclick();
              console.log(JSON.stringify({ has, tags, act, afterNo, asks, req: fetches[0], msg: mpMsg }));
            })();
            """);
        Assert.Equal("[false,false,true]", r.GetProperty("has").GetRawText());
        Assert.Equal(0, r.GetProperty("tags").GetInt32());   // no "built in" pills
        Assert.Equal("""["modesActive"]""", r.GetProperty("act").GetRawText());   // the active mode as a status word
        Assert.Equal(0, r.GetProperty("afterNo").GetInt32());
        Assert.Equal("""["modesDeleteAsk{\"n\":\"Quiet\"}","modesDeleteAsk{\"n\":\"Quiet\"}"]""", r.GetProperty("asks").GetRawText());
        var req = r.GetProperty("req");
        Assert.Equal(("DELETE", "/api/modes/quiet", "1"), (req.GetProperty("method").GetString(), req.GetProperty("url").GetString(), req.GetProperty("station").GetString()));
        Assert.Equal("""{"kind":"bad","text":"modesNotSaved{\"e\":\"a rule chooses quiet: change the rules first\"}"}""", r.GetProperty("msg").GetRawText());
    }

    [Fact]
    public void Each_mode_says_what_it_does_in_plain_lines_and_opens_its_editor_in_place()
    {
        var r = RunPanel("""
            const WORDS = { af_test_plan_plan: 'Plan is {v}', dv_p2: 'Saver here', af_test_all_mode_low: 'Runs low' };
            t = (k, p) => { let s = WORDS[k] ?? k; if (p) for (const x of Object.keys(p)) s = s.split(`{${x}}`).join(String(p[x])); return s; };
            mpView.settings.modes[1].actions = [{ typeId: 'test.plan', enabled: true, settings: { plan: 'p2' } }, { typeId: 'test.all', enabled: true, settings: { mode: 'low' } },
              { typeId: 'test.plan', enabled: false, settings: { plan: 'p1' } }];
            mpPaintList();
            const lines = (id) => walk($('modesList'), (x) => x.tagName === 'LI' && walk($('modesList'), (b) => b.attrs['data-mode'] === id)[0].children.some((u) => u.children && u.children.includes(x))).map((x) => x.textContent);
            const before = { normal: lines('normal'), eco: lines('eco'), quiet: lines('quiet') };
            A($('modesList'), 'edit', 'eco').onclick();
            const block = walk($('modesList'), (b) => b.attrs['data-mode'] === 'eco')[0];
            console.log(JSON.stringify({ before, inPlace: block.children.includes($('modesEdit')), shown: !$('modesEdit').hidden, editHidden: !A(block, 'edit', 'eco') }));
            """);
        var before = r.GetProperty("before");
        Assert.Equal("""["modesPutsBack"]""", before.GetProperty("normal").GetRawText());   // no actions: everything back
        Assert.Equal("""["Plan is Saver here","Runs low"]""", before.GetProperty("eco").GetRawText());   // a switched-off action says nothing
        Assert.Equal("""["x.gone"]""", before.GetProperty("quiet").GetRawText());   // a type with no words: its name
        Assert.True(r.GetProperty("inPlace").GetBoolean());
        Assert.True(r.GetProperty("shown").GetBoolean());
        Assert.True(r.GetProperty("editHidden").GetBoolean());
    }

    [Fact]
    public void Rules_reorder_change_kind_and_save_as_one_ordered_list_with_problems_at_their_rows()
    {
        var r = RunPanel("""
            (async () => {
              mpRulesMore = true; mpPaintRules();
              let box = $('modesRules');
              const savedOff = A(box, 'save-rules').disabled;
              A(box, 'down', 0).onclick();
              box = $('modesRules');
              const k = F(box, 'r0.kind'); k.value = 'socBelow'; k.onchange();
              box = $('modesRules');
              const p = F(box, 'r0.percent'); p.value = '15,5'; p.oninput();
              A(box, 'add-rule').onclick();
              box = $('modesRules');
              const k2 = F(box, 'r2.kind'); k2.value = 'timeWindow'; k2.onchange();
              box = $('modesRules');
              const from = F(box, 'r2.from'); from.value = '22:30'; from.oninput();
              const m = F(box, 'r2.modeId'); m.value = 'quiet'; m.onchange();
              A(box, 'remove', 1).onclick();
              answers = [{ status: 400, body: 'rules[1].condition.from, to: HH:mm; rules[0].modeId: no mode normal' }];
              await A($('modesRules'), 'save-rules').onclick();
              box = $('modesRules');
              console.log(JSON.stringify({ savedOff, req: fetches[0], modeErr: errOf(box, 'r0.modeId'), rowErr: errOf(box, 'r1.from, to'),
                rowOf: walk(F(box, 'r1'), (x) => x.attrs['data-err'] === 'r1.from, to').length, saveOn: !A(box, 'save-rules').disabled, msg: mpRulesMsg }));
            })();
            """);
        Assert.True(r.GetProperty("savedOff").GetBoolean());   // nothing changed: nothing to save
        var req = r.GetProperty("req");
        Assert.Equal(("PUT", "/api/modes/rules", "1"), (req.GetProperty("method").GetString(), req.GetProperty("url").GetString(), req.GetProperty("station").GetString()));
        Assert.Equal("""[{"condition":{"kind":"socBelow","percent":15.5,"hysteresis":2},"modeId":"normal"},{"condition":{"kind":"timeWindow","from":"22:30","to":"07:00"},"modeId":"quiet"}]""",
            req.GetProperty("body").GetString());
        Assert.Equal("no mode normal", r.GetProperty("modeErr").GetString());
        Assert.Equal("from, to: HH:mm", r.GetProperty("rowErr").GetString());
        Assert.Equal(1, r.GetProperty("rowOf").GetInt32());   // said inside its own row
        Assert.True(r.GetProperty("saveOn").GetBoolean());    // the edit is kept for another try
        Assert.Equal("modesFix", r.GetProperty("msg").GetString());
    }

    [Fact]
    public void The_usual_auto_rules_read_as_one_sentence_in_minutes_and_save_in_seconds()
    {
        var r = RunPanel("""
            (async () => {
              mpPaintRules();
              const row = F($('modesRules'), 'r0'), s = F(row, 'r0.seconds');
              const shown = [s.type, s.value], more = walk(row, (x) => x.attrs['aria-label'] === 'modesMoreN')[0];
              more.onclick(); more.onclick();
              const typed = F($('modesRules'), 'r1.seconds'); typed.value = '1,5'; typed.oninput();
              await mpSaveRules();
              const body = JSON.parse(fetches[0].body);
              const list = !!A($('modesRules'), 'down', 0); A($('modesRules'), 'more-rules').onclick();
              console.log(JSON.stringify({ shown, after: s.value, seconds: body.map((x) => x.condition.seconds), text: row.textContent, list, more: !!A($('modesRules'), 'down', 0) }));
            })();
            """);
        Assert.Equal("""["text","3"]""", r.GetProperty("shown").GetRawText());   // 180 s as 3 min, no spinner
        Assert.Equal("5", r.GetProperty("after").GetString());
        Assert.Equal("[300,90]", r.GetProperty("seconds").GetRawText());
        Assert.StartsWith("modeEcoautoWhenOff", r.GetProperty("text").GetString(), StringComparison.Ordinal);   // «Еко, коли світла немає довше 5 хв»
        Assert.False(r.GetProperty("list").GetBoolean());   // no rule builder for the usual two rules
        Assert.True(r.GetProperty("more").GetBoolean());    // the full list behind «More rules»
        Assert.Contains("uMin", r.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_emptied_rule_value_is_refused_before_anything_is_sent()
    {
        var r = RunPanel("""
            (async () => {
              mpPaintRules();
              const s = F($('modesRules'), 'r0.seconds'); s.value = ''; s.oninput();
              await mpSaveRules();
              console.log(JSON.stringify({ sent: fetches.length, err: errOf($('modesRules'), 'r0.seconds') }));
            })();
            """);
        Assert.Equal(0, r.GetProperty("sent").GetInt32());   // never saved as 0 s
        Assert.Equal("modesNeedValue", r.GetProperty("err").GetString());
    }

    [Fact]
    public void Only_what_failed_is_said_as_one_line_with_what_to_do()
    {
        var r = RunPanel("""
            mpPaintTop();
            const first = walk($('modesStatus'), (x) => x.attrs['data-st']).map((c) => [c.attrs['data-st'], c.className, c.textContent]);
            const problems = walk($('modesStatus'), (x) => x.className === 'bad' && !x.attrs['data-st']).map((x) => x.textContent);
            mpView.emergency = true; mpView.actions = []; mpView.restores = []; mpPaintTop();
            const why = mpWhy('the broker refused: the request folder C:\\x cannot be written; next try at 22:34 UTC');
            console.log(JSON.stringify({ first, problems, second: $('modesStatus').textContent, why }));
            """);
        Assert.Equal("""[["failed","bad","fail_failed{\"a\":\"Everything\",\"r\":\"whyLog\"}"],["unsupported","warn","fail_unsupported{\"a\":\"x.gone\",\"r\":\"whyLog\"}"],["restore-failed","bad","fail_restore_failed{\"a\":\"Everything\",\"r\":\"whyLog\"}"]]""",
            r.GetProperty("first").GetRawText());
        Assert.Equal("""["modesProblem"]""", r.GetProperty("problems").GetRawText());   // the server's English stays in its log
        Assert.StartsWith("whyBroker; whyRetry{", r.GetProperty("why").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", r.GetProperty("why").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("C:", r.GetProperty("why").GetString(), StringComparison.Ordinal);
        Assert.Equal("modesEmergencymodesProblem", r.GetProperty("second").GetString());
    }

    [Fact]
    public void Editor_fields_and_enum_values_take_the_page_word_and_keep_the_catalogue_text_without_one()
    {
        var r = RunPanel("""
            t = (k, p) => ({ fl_windows_powerPlan_plan: 'Plan word', fv_service_state_services_startType_manual: 'by hand' })[k] || (p ? k + JSON.stringify(p) : k);
            const fields = mpFields({ properties: { plan: { type: 'string', title: 'Power plan' }, other: { type: 'string', title: 'Other' } } }, {}, '', 'windows.powerPlan');
            const opts = mpOptions({ enum: ['manual', 'automatic'] }, {}, { descriptor: { typeId: 'service.state' } }, 'services.startType');
            console.log(JSON.stringify({ labels: fields.map((f) => f.label), opts: opts.map((o) => o.label) }));
            """);
        Assert.Equal("""["Plan word","Other"]""", r.GetProperty("labels").GetRawText());
        Assert.Equal("""["by hand","automatic"]""", r.GetProperty("opts").GetRawText());
    }

    [Fact]
    public void A_pick_posts_once_and_a_second_press_while_one_is_out_sends_nothing()
    {
        var r = RunPanel("""
            (async () => {
              const MANUAL = JSON.parse(JSON.stringify(VIEW)); MANUAL.activeModeId = 'quiet'; MANUAL.settings.selection = { kind: 'manual', manualModeId: 'quiet', autoModeId: 'eco', autoRule: null };
              let release; const sent = [];
              fetch = (u, o) => { sent.push([u, o.method, o.headers['X-Station'], o.body]); return new Promise((res) => { release = () => res({ ok: true, status: 200, json: () => Promise.resolve(MANUAL) }); }); };
              const p = mpSelect('quiet');
              const again = mpSelect('normal');
              release(); await p; await again;
              console.log(JSON.stringify({ sent, loads, now: mpView.settings.selection.manualModeId }));
            })();
            """);
        Assert.Equal("""[["/api/modes/select","POST","1","{\"mode\":\"quiet\"}"]]""", r.GetProperty("sent").GetRawText());   // a second press while one is out sends nothing
        Assert.Equal(1, r.GetProperty("loads").GetInt32());   // the header follows at once
        Assert.Equal("quiet", r.GetProperty("now").GetString());
    }
    [Fact]
    public void The_hibernate_toggle_puts_the_setting_and_a_refused_change_springs_back()
    {
        var r = RunPanel("""
            (async () => {
              mpPaintTop();
              const box = () => walk($('modesHib'), (x) => x.attrs['data-sw'] === 'hibernate')[0];
              const on = () => box().attrs['aria-checked'] === 'true';
              const before = on();
              answers = [{ status: 500, body: 'modes.json not saved: disk full' }];
              await box().onclick();
              const refused = [on(), mpMsg.text];
              const OFF = JSON.parse(JSON.stringify(VIEW)); OFF.settings.autoHibernate = false;
              answers = [{ status: 200, body: OFF }];
              await box().onclick();
              console.log(JSON.stringify({ before, req: fetches[0], refused, after: [on(), mpMsg.text] }));
            })();
            """);
        Assert.True(r.GetProperty("before").GetBoolean());
        var req = r.GetProperty("req");
        Assert.Equal(("PUT", "/api/settings/hibernate", "1", """{"autoHibernate":false}"""),
            (req.GetProperty("method").GetString(), req.GetProperty("url").GetString(), req.GetProperty("station").GetString(), req.GetProperty("body").GetString()));
        Assert.Equal("""[true,"modesNotSaved{\"e\":\"modes.json not saved: disk full\"}"]""", r.GetProperty("refused").GetRawText());
        Assert.Equal("""[false,"modesHibOff"]""", r.GetProperty("after").GetRawText());
    }

    [Fact]
    public void The_panel_stays_hidden_while_the_engine_is_legacy()
    {
        var r = RunPanel("""
            (async () => {
              mpView = null; $('modesCard').hidden = true;
              answers = [{ status: 409, body: 'modes.engine is legacy' }];
              await mpRefresh();
              const legacy = [$('modesCard').hidden, mpView === null];
              answers = [{ status: 200, body: VIEW }];
              await mpRefresh();
              console.log(JSON.stringify({ legacy, v2: [$('modesCard').hidden, walk($('modesList'), (x) => x.attrs['data-act'] === 'edit').length] }));
            })();
            """);
        Assert.Equal("[true,true]", r.GetProperty("legacy").GetRawText());
        Assert.Equal("[false,3]", r.GetProperty("v2").GetRawText());
    }

    [Fact]
    public void The_panel_names_no_module_and_every_key_it_picks_exists_in_both_languages()
    {
        string page = Page().Replace("\r\n", "\n", StringComparison.Ordinal);
        string panel = Panel(page);
        // module ids, list names and setting fields all come from the catalogue
        Assert.DoesNotContain("windows.", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu", panel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plans", panel, StringComparison.Ordinal);
        var (en, uk) = Dictionaries(page);
        // keys the panel picks through a map or a variable (literal t('...') calls are covered by the whole-page test)
        string[] keys = ["fwNormal", "fwEco", "fwEmergency", "modesHyst", "modesFrom", "modesTo", "uPct", "uMin"];
        Assert.DoesNotContain(keys, k => !en.Contains(k) || !uk.Contains(k));
    }

    [Fact]
    public void The_status_and_rule_kind_codes_are_every_name_the_service_sends()
    {
        static IEnumerable<string> Names<T>() where T : struct, Enum =>
            typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()!.Name);
        Assert.Equal(Names<ActionStatus>().Order(StringComparer.Ordinal), PageCodes.ActionStatuses.Order(StringComparer.Ordinal));
        Assert.Equal(Names<ConditionKind>().Order(StringComparer.Ordinal), PageCodes.ConditionKinds.Order(StringComparer.Ordinal));
        Assert.Contains("st_restore_failed", PageCodes.Keys());
        Assert.Contains("ck_timeWindow", PageCodes.Keys());
    }
}
