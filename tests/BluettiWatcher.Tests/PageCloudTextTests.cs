using System.Text.Json;

namespace BluettiWatcher.Tests;

/// <summary>The reading-source card words the cloud state itself: no server detail, sign-in message, path or ISO time reaches it.</summary>
public sealed partial class PageI18nTests
{
    [Fact]
    public void PaintCloud_never_shows_the_service_text_and_words_the_date_locally()
    {
        string page = Page();
        string script = $$"""
            const els = {}; function $(id) { return els[id] || (els[id] = { hidden: false, disabled: false, title: '', textContent: '', className: '', lastChild: {}, removeAttribute() {} }); }
            var document = { querySelectorAll: () => [] };
            function t(k, p) { return p ? k + JSON.stringify(p) : k; } function val(s) { return 'v_' + s; }
            let cloudNow, cloudErr = '', cloudBusy = false, cloudUrl = '';
            {{Const(page, "CLOUD_CHIP")}}
            {{Const(page, "CLOUD_TIP")}}
            eval({{Js(FunctionText(page, "cloudDate"))}});
            eval({{Js(FunctionText(page, "paintCloud"))}});
            const until = new Date(2026, 11, 21, 21, 6).toISOString();
            const path = 'D:\\data\\bluetti-watcher\\cloud-token.json';
            const out = [];
            for (const [cloud, signIn] of [
              [{ state: 'ok', detail: 'signed in, not read yet; the access token is valid until 2026-12-21T19:06:57Z', until }, { state: 'signed-in', message: 'token saved to ' + path + ', expires 21.12.2026 22:49' }],
              [{ state: 'error', detail: 'the token file is unreadable at ' + path, until }, { state: 'failed', message: 'login failed: the login answered: access_denied' }],
              [{ state: 'sign-in-needed', detail: 'the token refresh was refused (HTTP 401): sign in again', until: null }, { state: 'idle', message: '' }]]) {
              cloudNow = { readingSource: 'auto', readingSourceFrom: 'default', cloud, signIn };
              paintCloud();
              out.push({ line: els.cloudLine.textContent, tip: els.cloudChip.title });
            }
            console.log(JSON.stringify(out));
            """;
        using var doc = JsonDocument.Parse(RunScript(script));
        var got = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("line").GetString()!, e.GetProperty("tip").GetString()!)).ToArray();
        Assert.Equal(("cloudTipOk{\"d\":\"21.12.2026 21:06\"}", "cloudTipOk{\"d\":\"21.12.2026 21:06\"}"), got[0]);
        Assert.Equal(("cloudSignInFailed", "cloudTipError{\"d\":\"21.12.2026 21:06\"}"), got[1]);
        Assert.Equal(("cloudTipSignInNeeded", "cloudTipSignInNeeded{\"d\":\"\"}"), got[2]);
        Assert.All(got, g => Assert.DoesNotContain("cloud-token", g.Item1 + g.Item2, StringComparison.Ordinal));
    }
}
