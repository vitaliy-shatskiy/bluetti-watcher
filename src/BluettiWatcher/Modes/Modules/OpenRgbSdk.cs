using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <see cref="IOpenRgb"/> over OpenRGB's SDK server on 127.0.0.1:<see cref="Port"/>. When none answers, a write starts one
/// headless, windowless instance under the watcher's user (<c>OpenRGB.exe --server --noautoconnect</c>: no GUI, no tray
/// icon) and leaves it running, so the device detection that resets some controllers runs once, not on every mode change.
/// Why the SDK and not the command line: OpenRGB.exe prints only to a console it inherits, so a windowless child's output
/// is lost and a CLI write cannot be read back; each CLI call also redoes the detection (3-4 s, and the reset).
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class OpenRgbSdk(string configuredPath, TimeProvider time) : IOpenRgb
{
    public const int Port = 6742;
    /// <summary>How long a started server may take to listen and finish detecting its devices.</summary>
    public static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);
    /// <summary>The longest one exchange with a running server may take.</summary>
    public static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private const string ClientName = "Bluetti Watcher";

    public string? ExePath => Candidates().FirstOrDefault(File.Exists);

    public IReadOnlyList<string>? Profiles()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", "profiles");
        try
        {
            return Directory.Exists(dir) ? [.. Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>?> RunningDevicesAsync(CancellationToken ct)
    {
        try
        {
            using var s = await Session.TryOpenAsync(ct);
            return s is null ? null : [.. (await s.DevicesAsync(ct)).Select(d => d.Name)];
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or TimeoutException)
        {
            return null;
        }
    }

    public Task<string?> TurnOffAsync(IReadOnlyList<string> devices, CancellationToken ct) => WithServerAsync(devices, async (s, all) =>
    {
        var targets = devices.Count == 0 ? all : [.. all.Where(d => devices.Contains(d.Name, StringComparer.Ordinal))];
        foreach (var d in targets)
        {
            await s.SendAsync((uint)d.Index, OpenRgbPackets.SetCustomMode, [], ct);
            if (d.Colors.Count > 0) await s.SendAsync((uint)d.Index, OpenRgbPackets.UpdateLeds, OpenRgbPackets.BlackLeds(d.Colors.Count), ct);
        }
        // The controller data goes through the same per-device queue as the colours: its answer is the read-back.
        var lit = new List<string>();
        foreach (var d in targets)
            if (!(await s.DeviceAsync(d.Index, ct)).IsOff) lit.Add(d.Name);
        return lit.Count == 0 ? null : $"OpenRGB still shows {string.Join(", ", lit)} lit after turning it off";
    }, ct);

    public Task<string?> LoadProfileAsync(string profile, CancellationToken ct) => WithServerAsync([], async (s, _) =>
    {
        if (!(await s.ProfilesAsync(ct)).Contains(profile, StringComparer.Ordinal)) return $"OpenRGB has no profile '{profile}'";
        await s.SendAsync(0, OpenRgbPackets.LoadProfile, OpenRgbPackets.Text(profile), ct);
        await s.ProfilesAsync(ct);   // answered by the same queue after the load: the load has run when it comes
        return null;
    }, ct);

    private IEnumerable<string> Candidates()
    {
        if (configuredPath.Length > 0) yield return configuredPath;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe");
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a server, starting one when none answers. <paramref name="need"/> are device names
    /// that must be listed: a server just started is polled until its list holds them and stops growing.
    /// </summary>
    private async Task<string?> WithServerAsync(IReadOnlyList<string> need, Func<Session, IReadOnlyList<OpenRgbDevice>, Task<string?>> work, CancellationToken ct)
    {
        try
        {
            var s = await Session.TryOpenAsync(ct);
            bool started = false;
            if (s is null)
            {
                if (Start() is { } error) return error;
                started = true;
                s = await WaitForServerAsync(ct);
                if (s is null) return $"OpenRGB's server did not answer within {StartBudget.TotalSeconds:0} s of starting it";
            }
            using (s)
            {
                var all = await s.DevicesAsync(ct);
                var deadline = time.GetUtcNow() + StartBudget;
                while (started && time.GetUtcNow() < deadline && (all.Count == 0 || Missing(need, all).Count > 0))
                {
                    await Task.Delay(Poll, time, ct);
                    var again = await s.DevicesAsync(ct);
                    if (again.Count > 0 && again.Count == all.Count && Missing(need, again).Count == 0) { all = again; break; }
                    all = again;
                }
                if (Missing(need, all) is { Count: > 0 } missing)
                    return $"OpenRGB does not list {string.Join(", ", missing)} (it lists {string.Join(", ", all.Select(d => d.Name))})";
                return await work(s, all);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or TimeoutException)
        {
            return $"OpenRGB's server: {e.Message}";
        }
    }

    private static List<string> Missing(IReadOnlyList<string> need, IReadOnlyList<OpenRgbDevice> all) =>
        [.. need.Where(n => !all.Any(d => d.Name.Equals(n, StringComparison.Ordinal)))];

    /// <summary>Starts the headless server; null = started, else why not.</summary>
    private string? Start()
    {
        if (ExePath is not { } exe) return "OpenRGB is not installed";
        // Shell execute: CreateProcess with UseShellExecute=false hands the child this process's inheritable handles (a
        // redirected stdout pipe), and a server that outlives us would hold them open. Headless OpenRGB opens no window.
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add("--noautoconnect");
        try
        {
            using var p = Process.Start(psi);   // left running: the handle is released, the server is not
            return p is null ? "OpenRGB could not be started" : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return $"OpenRGB could not be started: {e.Message}";
        }
    }

    private async Task<Session?> WaitForServerAsync(CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + StartBudget;
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(Poll, time, ct);
            if (await Session.TryOpenAsync(ct) is { } s) return s;
        }
        return null;
    }

    /// <summary>One connection, past the version handshake. Every exchange is bounded by <see cref="CallBudget"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private Session(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        /// <summary>A session with a server that answers; null when nothing listens on the port.</summary>
        public static async Task<Session?> TryOpenAsync(CancellationToken ct)
        {
            var client = new TcpClient();
            try
            {
                using (var cts = Budget(ct)) await client.ConnectAsync(IPAddress.Loopback, Port, cts.Token);
            }
            catch (SocketException)
            {
                client.Dispose();
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                client.Dispose();
                return null;
            }
            var s = new Session(client);
            try
            {
                await s.SendAsync(0, OpenRgbPackets.ProtocolVersion, OpenRgbPackets.U32(OpenRgbPackets.Protocol), ct);
                await s.ReceiveAsync(OpenRgbPackets.ProtocolVersion, ct);
                await s.SendAsync(0, OpenRgbPackets.ClientName, OpenRgbPackets.Text(ClientName), ct);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        public async Task<IReadOnlyList<OpenRgbDevice>> DevicesAsync(CancellationToken ct)
        {
            await SendAsync(0, OpenRgbPackets.ControllerCount, [], ct);
            int count = OpenRgbPackets.Count(await ReceiveAsync(OpenRgbPackets.ControllerCount, ct));
            var list = new List<OpenRgbDevice>(count);
            for (int i = 0; i < count; i++) list.Add(await DeviceAsync(i, ct));
            return list;
        }

        public async Task<OpenRgbDevice> DeviceAsync(int index, CancellationToken ct)
        {
            await SendAsync((uint)index, OpenRgbPackets.ControllerData, OpenRgbPackets.U32(OpenRgbPackets.Protocol), ct);
            return OpenRgbPackets.Device(index, await ReceiveAsync(OpenRgbPackets.ControllerData, ct));
        }

        public async Task<IReadOnlyList<string>> ProfilesAsync(CancellationToken ct)
        {
            await SendAsync(0, OpenRgbPackets.ProfileList, [], ct);
            return OpenRgbPackets.Profiles(await ReceiveAsync(OpenRgbPackets.ProfileList, ct));
        }

        public async Task SendAsync(uint device, uint id, byte[] payload, CancellationToken ct)
        {
            using var cts = Budget(ct);
            try
            {
                await _stream.WriteAsync(OpenRgbPackets.Packet(device, id, payload), cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"OpenRGB request {id} could not be sent within {CallBudget.TotalSeconds:0} s");
            }
        }

        /// <summary>The payload of the next packet with <paramref name="id"/>; packets the server pushes meanwhile are skipped.</summary>
        public async Task<byte[]> ReceiveAsync(uint id, CancellationToken ct)
        {
            using var cts = Budget(ct);
            var header = new byte[OpenRgbPackets.HeaderSize];
            try
            {
                while (true)
                {
                    await _stream.ReadExactlyAsync(header, cts.Token);
                    var (got, size) = OpenRgbPackets.ReadHeader(header);
                    var payload = new byte[size];
                    await _stream.ReadExactlyAsync(payload, cts.Token);
                    if (got == id) return payload;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no answer to OpenRGB request {id} within {CallBudget.TotalSeconds:0} s");
            }
            catch (EndOfStreamException e)
            {
                throw new IOException("the server closed the connection", e);
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }

        private static CancellationTokenSource Budget(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallBudget);
            return cts;
        }
    }
}
