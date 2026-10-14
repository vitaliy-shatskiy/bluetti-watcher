using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BluettiWatcher.Http;

internal sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>An answer. <see cref="ContentEncoding"/> set = the body is already encoded; <see cref="VaryEncoding"/> = it depends on Accept-Encoding.</summary>
internal sealed record HttpResponse(int Status, string ContentType, byte[] Body)
{
    public string CacheControl { get; init; } = "no-store";
    public string? ETag { get; init; }
    public string? ContentEncoding { get; init; }
    public bool VaryEncoding { get; init; }

    public static HttpResponse Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text + "\n"));
    public static HttpResponse Json(int status, byte[] body) => new(status, "application/json; charset=utf-8", body);
}

/// <summary>Brotli or gzip by Accept-Encoding; anything else is sent as it is.</summary>
internal static class HttpEncoding
{
    /// <summary>Bodies under this size go as they are: they fit one packet either way.</summary>
    public const int MinSize = 1400;

    /// <summary>"br", "gzip" or null (identity). Brotli wins when both are acceptable; q=0 refuses a coding; "*" stands for any not named.</summary>
    public static string? Pick(string? acceptEncoding)
    {
        if (string.IsNullOrEmpty(acceptEncoding)) return null;
        bool? br = null, gzip = null, star = null;
        foreach (var item in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(';', StringSplitOptions.TrimEntries);
            bool ok = true;
            foreach (var p in parts.Skip(1))
                if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(p[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double q))
                    ok = q > 0;
            if (parts[0].Equals("br", StringComparison.OrdinalIgnoreCase)) br = ok;
            else if (parts[0].Equals("gzip", StringComparison.OrdinalIgnoreCase)) gzip = ok;
            else if (parts[0] == "*") star = ok;
        }
        if (br ?? star ?? false) return "br";
        if (gzip ?? star ?? false) return "gzip";
        return null;
    }

    /// <summary>The body encoded; <paramref name="best"/> = the smallest output (once, for content that never changes), else a fast level.</summary>
    public static byte[] Encode(string encoding, ReadOnlySpan<byte> body, bool best)
    {
        if (encoding == "br")
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(body.Length));
            try
            {
                // The window just covers the body (10..22 bits): a smaller window is a smaller encoder state to set up per call.
                int window = Math.Clamp(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)body.Length), 10, 22);
                if (!BrotliEncoder.TryCompress(body, buf, out int written, best ? 11 : 4, window)) throw new InvalidOperationException("brotli failed");
                return buf.AsSpan(0, written).ToArray();
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        using var ms = new MemoryStream(body.Length / 4 + 64);
        using (var z = new GZipStream(ms, best ? CompressionLevel.SmallestSize : CompressionLevel.Fastest, leaveOpen: true)) z.Write(body);
        return ms.ToArray();
    }

    /// <summary>A compressible type: the page and JSON (plain-text answers are short errors).</summary>
    public static bool Compressible(string contentType) =>
        contentType.StartsWith("application/json", StringComparison.Ordinal) || contentType.StartsWith("text/html", StringComparison.Ordinal);
}

/// <summary>
/// Content fixed for the life of the process (the page): encoded once per coding on first use, a weak ETag of its bytes,
/// "no-cache" so every load revalidates and an unchanged page is a 304 with no body.
/// </summary>
internal sealed class StaticAsset(byte[] body, string contentType)
{
    private byte[]? _br, _gzip;
    public string ETag { get; } = "W/\"" + Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 12)) + "\"";

    public HttpResponse Respond(HttpRequest r)
    {
        if (r.Headers.TryGetValue("if-none-match", out var inm) && Matches(inm, ETag))
            return new HttpResponse(304, contentType, []) { CacheControl = "no-cache", ETag = ETag, VaryEncoding = true };
        string? enc = HttpEncoding.Pick(r.Headers.GetValueOrDefault("accept-encoding"));
        byte[] bytes = enc switch
        {
            "br" => _br ??= HttpEncoding.Encode("br", body, best: true),
            "gzip" => _gzip ??= HttpEncoding.Encode("gzip", body, best: true),
            _ => body,
        };
        return new HttpResponse(200, contentType, bytes) { CacheControl = "no-cache", ETag = ETag, ContentEncoding = enc, VaryEncoding = true };
    }

    /// <summary>If-None-Match: "*" or a list of tags, compared weakly (the W/ prefix ignored on both sides).</summary>
    internal static bool Matches(string header, string etag)
    {
        string bare = etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag;
        foreach (var t in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (t == "*" || (t.StartsWith("W/", StringComparison.Ordinal) ? t[2..] : t) == bare) return true;
        return false;
    }
}

/// <summary>
/// A minimal HTTP/1.1 server on a TcpListener: one request per connection (Connection: close), headers up to 16 KB, a body
/// up to 4 KB, the request head within 5 s (an idle connection is closed), 10 s per connection, at most 32 at a time; a
/// handler exception is answered 400 (bad input) or 500 and logged. No URL ACL and no admin rights: it binds 127.0.0.1 only.
/// </summary>
internal sealed class MiniHttpServer(IPAddress address, int port, Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler, Action<string> log,
    TimeSpan? headTimeout = null) : IDisposable
{
    public void Dispose()
    {
        _listener?.Dispose();
        _slots.Dispose();
    }

    private const int MaxHead = 16 * 1024;
    private const int MaxBody = 4 * 1024;
    private const int Slots = 32;
    private readonly SemaphoreSlim _slots = new(Slots, Slots);
    private readonly TimeSpan _headTimeout = headTimeout ?? TimeSpan.FromSeconds(5);
    private TcpListener? _listener;

    /// <summary>Binds; throws SocketException when the port is taken.</summary>
    public void Start()
    {
        _listener = new TcpListener(address, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(16);
    }

    public int BoundPort => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => _listener?.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            await _slots.WaitAsync(ct);
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, ct); }
                finally { _slots.Release(); client.Dispose(); }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stream = client.GetStream();
            HttpRequest? req;
            HttpResponse? bad;
            // An idle connection holds a slot only until the header timeout, then it is closed.
            using (var head = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                head.CancelAfter(_headTimeout);
                (req, bad) = await ReadRequestAsync(stream, head.Token);
            }
            HttpResponse resp;
            try { resp = bad ?? await handler(req!, cts.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Every handler failure is answered: bad input 400, anything else 500; both logged.
                bool input = e is ArgumentException or FormatException or OverflowException;
                log($"http: {req!.Method} {req.Path} failed with {(input ? 400 : 500)}: {e.GetType().Name}: {e.Message}");
                resp = input ? HttpResponse.Text(400, "bad request") : HttpResponse.Text(500, "internal error");
            }
            if (req is not null) resp = Negotiate(req, resp);
            await WriteAsync(stream, resp, cts.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            log($"http: connection dropped ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// A 200 the handler left unencoded, of a compressible type and at least <see cref="HttpEncoding.MinSize"/> bytes, is
    /// encoded by the request's Accept-Encoding at a fast level (the page comes pre-encoded from <see cref="StaticAsset"/>).
    /// </summary>
    internal static HttpResponse Negotiate(HttpRequest req, HttpResponse resp)
    {
        if (resp.ContentEncoding is not null || resp.Status != 200 || resp.Body.Length < HttpEncoding.MinSize || !HttpEncoding.Compressible(resp.ContentType))
            return resp;
        string? enc = HttpEncoding.Pick(req.Headers.GetValueOrDefault("accept-encoding"));
        return enc is null ? resp with { VaryEncoding = true } : resp with { Body = HttpEncoding.Encode(enc, resp.Body, best: false), ContentEncoding = enc, VaryEncoding = true };
    }

    internal static async Task<(HttpRequest? Request, HttpResponse? Bad)> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(MaxHead);
        try
        {
            int len = 0, headEnd = -1;
            while (headEnd < 0)
            {
                if (len >= MaxHead) return (null, HttpResponse.Text(431, "headers too large"));
                int n = await stream.ReadAsync(buf.AsMemory(len, MaxHead - len), ct);
                if (n == 0) return (null, HttpResponse.Text(400, "incomplete request"));
                len += n;
                headEnd = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            }
            var (req, bad) = ParseHead(Encoding.ASCII.GetString(buf, 0, headEnd));
            if (bad is not null) return (null, bad);
            int bodyLen = 0;
            if (req!.Headers.TryGetValue("content-length", out var cl) && (!int.TryParse(cl, out bodyLen) || bodyLen < 0))
                return (null, HttpResponse.Text(400, "bad content-length"));
            if (bodyLen > MaxBody) return (null, HttpResponse.Text(413, "body too large"));
            if (bodyLen == 0) return (req, null);
            int have = len - (headEnd + 4);
            var body = new byte[bodyLen];
            Array.Copy(buf, headEnd + 4, body, 0, Math.Min(have, bodyLen));
            while (have < bodyLen)
            {
                int n = await stream.ReadAsync(body.AsMemory(have, bodyLen - have), ct);
                if (n == 0) return (null, HttpResponse.Text(400, "incomplete body"));
                have += n;
            }
            return (req with { Body = body }, null);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>The request line and headers. A repeated Host, Origin or X-Station header is a bad request.</summary>
    internal static (HttpRequest? Request, HttpResponse? Bad) ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return (null, HttpResponse.Text(400, "bad request line"));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return (null, HttpResponse.Text(400, "bad header"));
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (headers.ContainsKey(name))
            {
                if (name.Equals("host", StringComparison.OrdinalIgnoreCase) || name.Equals("origin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-station", StringComparison.OrdinalIgnoreCase) || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    return (null, HttpResponse.Text(400, $"repeated {name}"));
                headers[name] += ", " + value;
            }
            else headers[name] = value;
        }
        string target = parts[1];
        int q = target.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (var kv in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=', StringComparison.Ordinal);
                string k = Uri.UnescapeDataString(eq < 0 ? kv : kv[..eq]);
                query[k] = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
            }
        }
        return (new HttpRequest(parts[0], path, query, headers, []), null);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 304 => "Not Modified", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
        413 => "Payload Too Large", 431 => "Request Header Fields Too Large", 500 => "Internal Server Error", 502 => "Bad Gateway",
        _ => "Status",
    };

    private const string FixedHeaders = "X-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\n"
        + "Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'\r\n"
        + "Connection: close\r\n\r\n";

    /// <summary>The head and the body in one write (one send). A 304 carries no body, no Content-Length and no Content-Type.</summary>
    internal static async Task WriteAsync(Stream stream, HttpResponse r, CancellationToken ct)
    {
        var sb = new StringBuilder(512);
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        if (r.Status != 304) sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\nContent-Length: ").Append(r.Body.Length).Append("\r\n");
        if (r.ContentEncoding is not null) sb.Append("Content-Encoding: ").Append(r.ContentEncoding).Append("\r\n");
        if (r.VaryEncoding) sb.Append("Vary: Accept-Encoding\r\n");
        if (r.ETag is not null) sb.Append("ETag: ").Append(r.ETag).Append("\r\n");
        sb.Append("Cache-Control: ").Append(r.CacheControl).Append("\r\n").Append(FixedHeaders);
        string head = sb.ToString();
        int headLen = head.Length;   // ASCII: one byte per char
        int bodyLen = r.Status == 304 ? 0 : r.Body.Length;
        byte[] buf = ArrayPool<byte>.Shared.Rent(headLen + bodyLen);
        try
        {
            Encoding.ASCII.GetBytes(head, buf);
            r.Body.AsSpan(0, bodyLen).CopyTo(buf.AsSpan(headLen));
            await stream.WriteAsync(buf.AsMemory(0, headLen + bodyLen), ct);
            await stream.FlushAsync(ct);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}
