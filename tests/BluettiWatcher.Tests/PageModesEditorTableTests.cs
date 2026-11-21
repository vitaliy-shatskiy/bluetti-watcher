namespace BluettiWatcher.Tests;

/// <summary>
/// A setting that is a list of objects (Windows services, scheduled tasks, per-monitor values) is edited as rows, one control
/// per property: never shown as "[object Object]", and a save with nothing touched writes the same settings back.
/// </summary>
public sealed partial class PageI18nTests
{
    private const string ObjectListCatalogue = """
        mpCat.modules.push({ descriptor: { typeId: 'test.svc', version: 1, title: 'Services', restoration: 'reversible', discoveryLists: ['monitors'],
            settingsSchema: { type: 'object', additionalProperties: false, properties: {
              services: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['name', 'running'],
                properties: { name: { type: 'string' }, running: { type: 'boolean' }, startType: { type: 'string', enum: ['unchanged', 'manual', 'automatic'] } } } },
              monitors: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['monitor', 'hz'],
                properties: { monitor: { type: 'string' }, hz: { type: 'integer', minimum: 1, maximum: 1000 } } } } } },
            uiHints: { services: { widget: 'table', label: 'Services' }, monitors: { widget: 'list', list: 'monitors', label: 'Refresh rate per monitor' } },
            defaultSettings: {} },
          available: true, unavailableReason: null, discovery: { monitors: [{ id: 'DISPLAY1', label: 'Main monitor' }] } });
        const SETTINGS = { services: [{ name: 'WSearch', running: false, startType: 'manual' }, { name: 'Spooler', running: true }],
          monitors: [{ monitor: 'DISPLAY1', hz: 60 }, { monitor: 'GONE', hz: 75 }] };
        mpView.settings.modes.push({ id: 'cool', name: 'Cool', flagWord: 'normal', builtIn: false,
          actions: [{ typeId: 'test.svc', enabled: true, enforce: false, settings: JSON.parse(JSON.stringify(SETTINGS)) }] });
        """;

    [Fact]
    public void Editor_list_of_objects_shows_each_property_and_never_object_text()
    {
        var r = RunPanel(ObjectListCatalogue + """
            mpEditMode('cool');
            const ed = $('modesEdit');
            const values = walk(ed, (e) => e.tagName === 'INPUT' || e.tagName === 'TEXTAREA' || e.tagName === 'SELECT').map((e) => e.type === 'checkbox' ? String(e.checked) : e.value);
            const text = ed.textContent + ' ' + values.join(' ');
            console.log(JSON.stringify({ object: text.includes('[object Object]'), names: ['WSearch', 'Spooler', 'GONE'].map((n) => values.includes(n)), only: ed.textContent.includes('Main monitor'), hz: values.includes('75') }));
            """);
        Assert.False(r.GetProperty("object").GetBoolean());
        Assert.Equal("[true,true,true]", r.GetProperty("names").GetRawText());
        Assert.True(r.GetProperty("only").GetBoolean());   // the one monitor found here: its name as text, no list of one
        Assert.True(r.GetProperty("hz").GetBoolean());
    }

    [Fact]
    public void Editor_save_with_nothing_touched_writes_the_same_list_of_objects()
    {
        var r = RunPanel(ObjectListCatalogue + """
            (async () => {
              mpEditMode('cool');
              await mpSaveMode();
              const body = JSON.parse(fetches[0].body);
              console.log(JSON.stringify({ same: JSON.stringify(body.actions[0].settings) === JSON.stringify(SETTINGS), sent: body.actions[0].settings }));
            })();
            """);
        Assert.True(r.GetProperty("same").GetBoolean(), r.GetProperty("sent").GetRawText());
    }
}
