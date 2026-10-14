using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BluettiWatcher.Http;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>Compression by Accept-Encoding, the page's ETag and 304, and the API staying no-store (api.md Caching).</summary>
public sealed class HttpCompressionTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly StationService _service;

    public HttpCompressionTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        _service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, new ManualClock(T0), _ => { });
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StationApi Api(int port = Port) => new(new StationConfig { DataDir = _dir, Port = port }, _service, _store, new ManualClock(T0), T0);

    private static HttpRequest Get(string path, params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        foreach (var (k, v) in headers) h[k] = v;
        return new HttpRequest("GET", path, new Dictionary<string, string>(), h, []);
    }

    private static byte[] Decode(string? encoding, byte[] body)
    {
        if (encoding is null) return body;
        using var input = new MemoryStream(body);
        using Stream z = encoding == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] PageFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        File.ReadAllBytes(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "BluettiWatcher", "Web", "station.html")));

    [Theory]
    [InlineData("gzip, deflate, br, zstd", "br")]   // Chrome
    [InlineData("gzip, deflate", "gzip")]
    [InlineData("GZIP", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("gzip;q=0.5, br;q=0.0", "gzip")]
    [InlineData("gzip;q=0", null)]
    [InlineData("identity", null)]
    [InlineData("deflate", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("*", "br")]
    [InlineData("br;q=0, *", "gzip")]
    public void The_encoding_is_picked_from_Accept_Encoding(string? header, string? want) => Assert.Equal(want, HttpEncoding.Pick(header));

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData(null)]
    public async Task The_page_is_served_compressed_and_revalidated(string? encoding)
    {
        var api = Api();
        var r = await api.HandleAsync(encoding is null ? Get("/") : Get("/", ("Accept-Encoding", encoding)), CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.Equal(encoding, r.ContentEncoding);
        Assert.Equal(PageFile(), Decode(encoding, r.Body));
        if (encoding is not null) Assert.True(r.Body.Length * 3 < PageFile().Length, $"{encoding}: {r.Body.Length} of {PageFile().Length} bytes");
        Assert.Equal("no-cache", r.CacheControl);
        Assert.True(r.VaryEncoding);
        Assert.StartsWith("W/\"", r.ETag, StringComparison.Ordinal);

        var again = await api.HandleAsync(Get("/", ("Accept-Encoding", encoding ?? ""), ("If-None-Match", r.ETag!)), CancellationToken.None);
        Assert.Equal(304, again.Status);
        Assert.Empty(again.Body);
        Assert.Equal(r.ETag, again.ETag);
        var list = await api.HandleAsync(Get("/index.html", ("If-None-Match", $"\"x\", {r.ETag}")), CancellationToken.None);
        Assert.Equal(304, list.Status);
        var other = await api.HandleAsync(Get("/", ("If-None-Match", "W/\"other\"")), CancellationToken.None);
        Assert.Equal(200, other.Status);
    }

    [Fact]
    public async Task A_foreign_Host_gets_403_even_with_the_page_ETag()
    {
        var api = Api();
        var page = await api.HandleAsync(Get("/"), CancellationToken.None);
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "evil.example", ["If-None-Match"] = page.ETag! };
        var r = await api.HandleAsync(new HttpRequest("GET", "/", new Dictionary<string, string>(), h, []), CancellationToken.None);
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public void Large_JSON_is_compressed_small_bodies_and_errors_are_not()
    {
        byte[] big = Encoding.UTF8.GetBytes("{\"rows\":[" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"{{\"at\":\"2026-10-13T12:{i % 60:00}:00+03:00\",\"soc\":{i % 100}}}")) + "]}");
        var req = Get("/api/history", ("Accept-Encoding", "gzip, br"));
        var r = MiniHttpServer.Negotiate(req, HttpResponse.Json(200, big));
        Assert.Equal("br", r.ContentEncoding);
        Assert.Equal("no-store", r.CacheControl);
        Assert.True(r.VaryEncoding);
        Assert.Equal(big, Decode("br", r.Body));
        Assert.True(r.Body.Length * 4 < big.Length, $"{r.Body.Length} of {big.Length}");

        Assert.Null(MiniHttpServer.Negotiate(req, HttpResponse.Json(200, "{\"ok\":true}"u8.ToArray())).ContentEncoding);
        Assert.Null(MiniHttpServer.Negotiate(req, HttpResponse.Text(400, new string('x', 4000))).ContentEncoding);
        Assert.Null(MiniHttpServer.Negotiate(Get("/api/history"), HttpResponse.Json(200, big)).ContentEncoding);
    }

    [Fact]
    public async Task Over_a_socket_the_page_is_gzip_then_304_and_the_security_headers_stay()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var server = new MiniHttpServer(IPAddress.Loopback, port, Api(port).HandleAsync, _ => { });
        server.Start();
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        (string Head, byte[] Body) Send(string extra)
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            var s = c.GetStream();
            s.Write(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{extra}\r\n"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] all = ms.ToArray();
            int end = all.AsSpan().IndexOf("\r\n\r\n"u8);
            return (Encoding.ASCII.GetString(all, 0, end), all[(end + 4)..]);
        }

        var (head, body) = Send("Accept-Encoding: gzip\r\n");
        string etag = head.Split("\r\n").Single(l => l.StartsWith("ETag: ", StringComparison.Ordinal))["ETag: ".Length..];
        var (head304, body304) = Send($"Accept-Encoding: gzip\r\nIf-None-Match: {etag}\r\n");
        await cts.CancelAsync();
        await run;

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
        Assert.Contains("\r\nContent-Encoding: gzip", head, StringComparison.Ordinal);
        Assert.Contains("\r\nVary: Accept-Encoding", head, StringComparison.Ordinal);
        Assert.Contains("\r\nCache-Control: no-cache", head, StringComparison.Ordinal);
        Assert.Contains($"\r\nContent-Length: {body.Length}", head, StringComparison.Ordinal);
        Assert.Equal(PageFile(), Decode("gzip", body));
        Assert.StartsWith("HTTP/1.1 304 Not Modified\r\n", head304, StringComparison.Ordinal);
        Assert.Empty(body304);
        Assert.Contains($"\r\nETag: {etag}", head304, StringComparison.Ordinal);
        foreach (var h in new[] { head, head304 })
        {
            Assert.Contains("\r\nX-Frame-Options: DENY", h, StringComparison.Ordinal);
            Assert.Contains("\r\nContent-Security-Policy: ", h, StringComparison.Ordinal);
            Assert.Contains("\r\nX-Content-Type-Options: nosniff", h, StringComparison.Ordinal);
            Assert.DoesNotContain("Access-Control-Allow", h, StringComparison.OrdinalIgnoreCase);
        }
    }
}
