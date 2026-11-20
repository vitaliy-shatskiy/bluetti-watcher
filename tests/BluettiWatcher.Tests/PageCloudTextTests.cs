using System.Text.Json;

namespace BluettiWatcher.Tests;

/// <summary>
/// The data-source card words the cloud state itself (no server detail, sign-in message, path or ISO time reaches it), is one
/// line while nothing needs the owner and opens by itself when something does.
/// </summary>
public sealed partial class PageI18nTests
{
    private static string CloudScript(string page, string body) => $$"""
        const els = {}; function $(id) { return els[id] || (els[id] = { hidden: false, disabled: false, title: '', textContent: '', className: '', lastChild: {}, attrs: {},
          setAttribute(k, v) { this.attrs[k] = v; }, removeAttribute() {} }); }
        var document = { querySelectorAll: () => [] };
        function t(k, p) { return p ? k + JSON.stringify(p) : k; } function val(s) { return 'v_' + s; }
        let cloudNow, cloudErr = '', cloudBusy = false, cloudUrl = '', pageSettings = {}, srcPeek = false, srcDismissed = '', srcStored = null;
        {{Const(page, "CLOUD_CHIP")}}
        eval({{Js(FunctionText(page, "cloudDate"))}});
        eval({{Js(FunctionText(page, "srcProblem"))}});
        eval({{Js(FunctionText(page, "srcIsOpen"))}});
        eval({{Js(FunctionText(page, "paintCloud"))}});
        {{body}}
        """;

    [Fact]
    public void PaintCloud_never_shows_the_service_text_and_words_the_date_locally()
    {
        string page = Page();
        string script = CloudScript(page, """
            const until = new Date(2026, 11, 21, 21, 6).toISOString();
            const path = 'D:\\data\\bluetti-watcher\\cloud-token.json';
            const out = [];
            for (const [cloud, signIn] of [
              [{ state: 'ok', detail: 'signed in, not read yet; the access token is valid until 2026-12-21T19:06:57Z', until }, { state: 'signed-in', message: 'token saved to ' + path + ', expires 21.12.2026 22:49' }],
              [{ state: 'error', detail: 'the token file is unreadable at ' + path, until }, { state: 'failed', message: 'login failed: the login answered: access_denied' }],
              [{ state: 'sign-in-needed', detail: 'the token refresh was refused (HTTP 401): sign in again', until: null }, { state: 'idle', message: '' }]]) {
              cloudNow = { readingSource: 'auto', readingSourceFrom: 'default', cloud, signIn };
              srcStored = '1';
              paintCloud();
              out.push({ line: els.cloudLine.textContent, chip: els.cloudChip.lastChild.textContent + els.cloudChip.title });
            }
            console.log(JSON.stringify(out));
            """);
        using var doc = JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("line").GetString()!, e.GetProperty("chip").GetString()!)).ToArray();
        Assert.Equal("cloudTipOk{\"d\":\"21.12.2026 21:06\"}", got[0].Item1);
        Assert.Equal("cloudSignInFailed", got[1].Item1);
        Assert.Equal("cloudTipSignInNeeded", got[2].Item1);
        Assert.All(got, g => Assert.DoesNotContain("cloud-token", g.Item1 + g.Item2, StringComparison.Ordinal));
        Assert.All(got, g => Assert.DoesNotContain("2026-12", g.Item1 + g.Item2, StringComparison.Ordinal));
    }

    [Fact]
    public void Source_card_is_one_line_when_fine_and_opens_itself_on_a_problem()
    {
        string page = Page();
        string script = CloudScript(page, """
            const ok = { readingSource: 'auto', readingSourceFrom: 'default', cloud: { state: 'ok', until: null }, signIn: { state: 'idle' } };
            const out = {};
            cloudNow = ok; paintCloud(); out.fine = els.srcBody.hidden;
            srcStored = '1'; paintCloud(); out.keptOpen = !els.srcBody.hidden;
            srcStored = '0';
            cloudNow = { ...ok, signIn: { state: 'timed-out' } }; paintCloud(); out.problem = !els.srcBody.hidden; out.line = els.cloudLine.textContent;
            cloudNow = { ...ok, signIn: { state: 'waiting' } }; paintCloud(); out.cancel = !els.cloudCancel.hidden;
            // hidden by the setting: a problem surfaces in the header, nothing else shows
            pageSettings = { hideSourcePanel: true }; paintCloud(); out.hiddenCard = els.srcCard.hidden; out.alert = !els.srcAlert.hidden;
            cloudNow = ok; paintCloud(); out.alertGone = els.srcAlert.hidden; out.showLink = !els.srcShow.hidden;
            console.log(JSON.stringify(out));
            """);
        using var doc = JsonDocument.Parse(RunScript(script));
        Assert.Equal("""{"fine":true,"keptOpen":true,"problem":true,"line":"cloudTimedOut","cancel":true,"hiddenCard":true,"alert":true,"alertGone":true,"showLink":true}""", doc.RootElement.GetRawText());
    }
}
