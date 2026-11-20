using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tests;

/// <summary>GET and PUT /api/settings/page: the page's own settings, kept by the service in page-settings.json (never config.json).</summary>
public sealed class PageSettingsTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-page-settings-").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, PageSettingsStore.FileName);

    private static HttpRequest Req(string method, string body = "", bool guarded = true)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        if (guarded) headers["X-Station"] = "1";
        return new HttpRequest(method, PageSettingsStore.Route, new Dictionary<string, string>(), headers, Encoding.UTF8.GetBytes(body));
    }

    [Fact]
    public void Put_HideSourcePanel_IsSavedAndReadBackAfterARestart()
    {
        var store = new PageSettingsStore(FilePath, _log.Add);
        Assert.False(JsonElement.Parse(store.Handle(Req("GET"), Port).Body).GetProperty("hideSourcePanel").GetBoolean());

        var r = store.Handle(Req("PUT", """{"hideSourcePanel":true}"""), Port);
        Assert.Equal(200, r.Status);
        Assert.True(JsonElement.Parse(r.Body).GetProperty("hideSourcePanel").GetBoolean());

        var restarted = new PageSettingsStore(FilePath, _log.Add);
        Assert.True(JsonElement.Parse(restarted.Handle(Req("GET"), Port).Body).GetProperty("hideSourcePanel").GetBoolean());
        Assert.Empty(_log);
    }

    [Fact]
    public void Put_Lang_IsSavedReadBackAfterARestartAndKeepsTheOtherField()
    {
        var store = new PageSettingsStore(FilePath, _log.Add);
        Assert.Equal("en", store.Lang);
        Assert.Equal(200, store.Handle(Req("PUT", """{"hideSourcePanel":true}"""), Port).Status);

        var r = store.Handle(Req("PUT", """{"lang":"uk"}"""), Port);
        Assert.Equal(200, r.Status);
        Assert.Equal("uk", store.Lang);
        var body = JsonElement.Parse(r.Body);
        Assert.Equal(("uk", true), (body.GetProperty("lang").GetString(), body.GetProperty("hideSourcePanel").GetBoolean()));

        Assert.Equal(200, store.Handle(Req("PUT", """{"hideSourcePanel":false}"""), Port).Status);
        var restarted = new PageSettingsStore(FilePath, _log.Add);
        Assert.Equal("uk", restarted.Lang);
        Assert.False(JsonElement.Parse(restarted.Handle(Req("GET"), Port).Body).GetProperty("hideSourcePanel").GetBoolean());
        Assert.Empty(_log);
    }

    [Fact]
    public void Load_AnOlderFileWithoutLang_IsEnglishAndKeepsTheBoolean()
    {
        File.WriteAllText(FilePath, """{"hideSourcePanel":true}""");
        var store = new PageSettingsStore(FilePath, _log.Add);
        var body = JsonElement.Parse(store.Handle(Req("GET"), Port).Body);
        Assert.Equal(("en", true), (body.GetProperty("lang").GetString(), body.GetProperty("hideSourcePanel").GetBoolean()));
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData("PUT", """{"hideSourcePanel":true}""", false, 403)]
    [InlineData("PUT", """{"lang":"uk"}""", false, 403)]
    [InlineData("PUT", """{"hideSourcePanel":"yes"}""", true, 400)]
    [InlineData("PUT", """{"other":true}""", true, 400)]
    [InlineData("PUT", """{"lang":"ru"}""", true, 400)]
    [InlineData("PUT", """{"lang":"UK"}""", true, 400)]
    [InlineData("PUT", """{"lang":1}""", true, 400)]
    [InlineData("PUT", """{"lang":"uk","other":true}""", true, 400)]
    [InlineData("PUT", "{}", true, 400)]
    [InlineData("PUT", "[]", true, 400)]
    [InlineData("PUT", "not json", true, 400)]
    [InlineData("POST", """{"hideSourcePanel":true}""", true, 405)]
    public void Put_Refused_ChangesNothing(string method, string body, bool guarded, int status)
    {
        var store = new PageSettingsStore(FilePath, _log.Add);
        Assert.Equal(status, store.Handle(Req(method, body, guarded), Port).Status);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Load_UnreadableFile_IsLoggedAndShowsThePanel()
    {
        File.WriteAllText(FilePath, "{ broken");
        var store = new PageSettingsStore(FilePath, _log.Add);
        Assert.False(JsonElement.Parse(store.Handle(Req("GET"), Port).Body).GetProperty("hideSourcePanel").GetBoolean());
        Assert.Single(_log);
    }
}
