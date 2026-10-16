// The poll flow (connect, subscribe to notifications, one read request after another, parse, disconnect) follows
// bluetti-bt-lib 0.1.8 (bluetooth/device_reader.py), https://github.com/Patrick762/bluetti-bt-lib, under this notice:
//
// MIT License
//
// Copyright (c) 2025 Patrick762
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;
using System.Threading.Channels;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io.Ble;

/// <summary>One open GATT connection to the station: notifications of characteristic ff01, read requests to ff02.</summary>
internal interface IBleLink : IAsyncDisposable
{
    /// <summary>Each notification's bytes, in arrival order; completed (with the cause) when the link drops.</summary>
    ChannelReader<byte[]> Notifications { get; }

    /// <summary>Writes one read request; only a <see cref="ReadRequest"/> can be sent. Throws when the write fails.</summary>
    Task WriteAsync(ReadRequest request, CancellationToken ct);
}

/// <summary>The link, or why there is none (not found, GATT unreachable, no ff01/ff02...).</summary>
internal sealed record BleOpen(IBleLink? Link, string? Failure);

internal interface IBleConnector
{
    Task<BleOpen> OpenAsync(ulong address, CancellationToken ct);
}

/// <summary>
/// The Bluetooth reader, IoT protocol V1 and V2 stations, unencrypted only (an encrypted station's key exchange ends the poll with no reading). READ ONLY, FOREVER: the fields of a <see cref="Reading"/> in a
/// few batched function-0x03 reads, nothing else written. A station that cannot be reached, or does not answer in time,
/// is no reading (null), never an exception: the cloud fallback then answers. An answer that timed out or came back
/// invalid ends its connection, and the next request goes over a new one; as a reconnect may reuse the radio link, an
/// answer that an earlier unanswered request (of this poll or an earlier one) could have sent is set aside: a late answer never fills another
/// request. One read at a time: a poll while an abandoned read is alive is a miss.
/// </summary>
internal sealed partial class NativeBleReader(StationConfig config, IBleConnector connector, Action<string> log,
    TimeSpan? commandTimeout = null, TimeSpan? hardMargin = null, TimeProvider? time = null) : IStationReader
{
    /// <summary>The library's per-command wait (device_reader.py: wait_for(future, 5)).</summary>
    private readonly TimeSpan _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5);
    /// <summary>A read still running this long past readerTimeoutSec (a Bluetooth call that ignored its cancellation) is given up.</summary>
    private readonly TimeSpan _hardMargin = hardMargin ?? TimeSpan.FromSeconds(2);
    /// <summary>New connections one poll may open after dropping one.</summary>
    private const int MaxReconnects = 2;
    /// <summary>Times one request is asked again after an answer was set aside as an earlier request's late one.</summary>
    private const int MaxReasks = 2;
    private string? _lastMiss;
    /// <summary>What the last note was, its byte counts left out: a repeat of the same miss is not logged again.</summary>
    private string? _lastKey;
    /// <summary>The station opened the encrypted handshake once: it is not connected to again until the service restarts.</summary>
    private volatile bool _encrypted;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    /// <summary>The shortest wait for an abandoned read before it is given up (4 x the read's hard budget when longer).</summary>
    private static readonly TimeSpan MinStallCap = TimeSpan.FromMinutes(2);
    private readonly Lock _flightLock = new();
    /// <summary>The generation of the last read started; each read has its own, so a read given up never touches a newer one's state.</summary>
    private long _generation;
    /// <summary>The generation of the read a poll waits for (0 = none): no other starts until it ends or is given up at the cap.</summary>
    private long _running;
    private DateTimeOffset _runningSince;
    /// <summary>The generation abandoned at the hard bound and still alive: Bluetooth is stalled.</summary>
    private long _abandoned;
    /// <summary>The generation of the read running on this async flow (0 outside a read); a superseded read logs nothing.</summary>
    private static readonly AsyncLocal<long> FlowGeneration = new();
    /// <summary>
    /// Requests written whose answer has not come, oldest first, carried across polls: a reconnect may reuse the radio link,
    /// so their answers may still arrive. Owned by the one live read; a read given up at the cap keeps its own.
    /// </summary>
    private List<ReadRequest> _unanswered = [];
    /// <summary>An abandoned read may log when it finally ends, beside the next poll's line.</summary>
    private readonly Lock _noteLock = new();
    private const string EncryptedMiss = EncryptedStationException.Cause + "; not connecting to it again until the service restarts";
    private static readonly System.Text.RegularExpressions.Regex ByteCounts = ByteCountsPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\(\d+ of \d+ bytes\)|(?<=bytes )\(\d+\)")]
    private static partial System.Text.RegularExpressions.Regex ByteCountsPattern();

    /// <summary>True once the station opened the encrypted handshake: it is read through the cloud only until the service restarts.</summary>
    public bool StationEncrypted => _encrypted;

    /// <summary>True while a read abandoned at the hard bound is still alive and waited for: no Bluetooth read starts until it ends or the cap gives it up.</summary>
    public bool BluetoothStalled
    {
        get { lock (_flightLock) return _running != 0 && _abandoned == _running; }
    }

    /// <summary>
    /// One read, bounded as a whole by readerTimeoutSec plus a margin, whatever the Bluetooth stack does. Single flight: while
    /// an earlier read is still alive (abandoned at that bound, a Bluetooth call ignoring its cancellation), no other starts;
    /// the poll is a miss and the cloud fallback answers. The abandoned read releases its link when it ends; one still alive
    /// after max(4 x the hard budget, 2 minutes) is given up, and the next poll reads again.
    /// </summary>
    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(1, config.ReaderTimeoutSec)) + _hardMargin;
        long generation = 0;
        string? gaveUp = null;
        lock (_flightLock)
        {
            var now = _time.GetUtcNow();
            var cap = TimeSpan.FromTicks(Math.Max(4 * limit.Ticks, MinStallCap.Ticks));
            if (_running != 0 && now - _runningSince >= cap)
            {
                gaveUp = string.Create(CultureInfo.InvariantCulture,
                    $"gave up waiting for a Bluetooth read still running after {cap.TotalSeconds:0} s (a Bluetooth call that never returned); reading again");
                _running = 0;
                _abandoned = 0;
                _unanswered = [];   // the given-up read keeps its own list
            }
            if (_running == 0)
            {
                generation = ++_generation;
                _running = generation;
                _runningSince = now;
            }
        }
        if (generation == 0)
            return Miss("the previous Bluetooth read is still running (a Bluetooth call ignored its timeout); no new one starts until it ends");
        if (gaveUp is not null) Note(gaveUp);
        var read = SingleFlightAsync(generation, ct);
        try
        {
            return await read.WaitAsync(limit, ct);
        }
        catch (TimeoutException)
        {
            lock (_flightLock) if (_running == generation) _abandoned = generation;
            // The abandoned read ends on its own, or never; its outcome is dropped (observed, so it never surfaces).
            _ = read.ContinueWith(static t => t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Miss(string.Create(CultureInfo.InvariantCulture,
                $"the Bluetooth stack did not return within {limit.TotalSeconds:0.#} s (readerTimeoutSec {config.ReaderTimeoutSec} + {_hardMargin.TotalSeconds:0.#} s)"));
        }
    }

    /// <summary>The read itself; its generation is released only when it has really ended (its link released), and only if no newer read took over.</summary>
    private async Task<Reading?> SingleFlightAsync(long generation, CancellationToken ct)
    {
        FlowGeneration.Value = generation;
        try
        {
            return await ReadOnceAsync(ct);
        }
        finally
        {
            lock (_flightLock)
            {
                if (_running == generation) { _running = 0; _abandoned = 0; }
            }
        }
    }

    private async Task<Reading?> ReadOnceAsync(CancellationToken ct)
    {
        if (!BleModels.TryGet(config.Type, out var model)) return Miss($"type \"{config.Type}\" is not a model the Bluetooth reader knows");
        if (!BleAddress.TryParse(config.Mac, out ulong address)) return Miss($"mac \"{config.Mac}\" is not a Bluetooth address");
        if (!model.HasAcInput) return Miss($"this model ({model.Type}) reports battery % only over Bluetooth; mains detection needs the cloud fallback");
        if (_encrypted) return Miss(EncryptedMiss);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.ReaderTimeoutSec)));
        KeyExchangeGuardLink? link = null;
        int opens = 0;
        bool stopped = false;
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var problems = new List<string>();
        List<ReadRequest> unanswered;
        lock (_flightLock) unanswered = _unanswered;

        // A request whose answer was lost (its connection dropped, timed out or garbled) is asked once more over a new connection,
        // so a spurious disconnect never costs its fields; the first answer stands when no new connection opens.
        async Task<ResponseAssembler?> NextAsync(int start, int quantity)
        {
            var answer = await AskAsync(start, quantity);
            return answer?.State == AssemblyState.Invalid ? await AskAsync(start, quantity) ?? answer : answer;
        }

        // One request over the current connection, opening one first when there is none; null = no further request this poll.
        async Task<ResponseAssembler?> AskAsync(int start, int quantity)
        {
            if (stopped) return null;
            if (link is null)
            {
                if (opens > MaxReconnects) { problems.Add(string.Create(CultureInfo.InvariantCulture, $"stopped after {opens} connections")); stopped = true; return null; }
                var open = await connector.OpenAsync(address, budget.Token);
                opens++;
                if (open.Link is null) { problems.Add(open.Failure ?? "no connection"); stopped = true; return null; }
                // Owned by this poll: disposed when the connection is dropped, or in the finally below.
                // lift-verified 2026-10-14 CA2000 fires without it
#pragma warning disable CA2000
                link = new KeyExchangeGuardLink(open.Link);
#pragma warning restore CA2000
            }
            var answer = await ExchangeAsync(link, ModbusRead.Request(start, quantity), unanswered, budget.Token);
            if (answer.State == AssemblyState.Invalid)
            {
                // Timed out or malformed: whatever is still on its way belongs to this request, so this connection ends here.
                var dropped = link;
                link = null;
                await dropped.DisposeAsync();
            }
            return answer;
        }

        try
        {
            foreach (var range in model.Plan)
            {
                var answer = await NextAsync(range.Start, range.Quantity);
                if (answer is null) break;
                if (answer.State == AssemblyState.Exception && range.Quantity > 1)
                {
                    // A station that refuses a batch (a register in the gap it does not serve) still answers the fields one by one, as the library asks.
                    foreach (var f in model.ReadingFields.Where(f => f.Start >= range.Start && f.Start < range.Start + range.Quantity))
                    {
                        var single = await NextAsync(f.Start, f.Quantity);
                        if (single is null) break;
                        Take(single, f.Start, model, values, problems);
                    }
                    continue;
                }
                Take(answer, range.Start, model, values, problems);
            }
            var reading = V1Models.ToReading(values, deriveMode: !model.HasOutputMode);
            if (reading is null) return Miss(problems.Count > 0 ? string.Join("; ", problems) : "no field answered");
            if (problems.Count > 0) Note($"partial reading: {string.Join("; ", problems)}");
            else Recovered();
            return reading;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Miss(string.Create(CultureInfo.InvariantCulture, $"no reading within readerTimeoutSec ({config.ReaderTimeoutSec} s)"));
        }
        catch (EncryptedStationException)
        {
            _encrypted = true;
            return Miss(EncryptedMiss);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A radio, driver or WinRT failure is one missed poll, never an exception into the poll loop.
            return Miss($"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (link is not null) await link.DisposeAsync();
        }
    }

    private static void Take(ResponseAssembler answer, int start, BleModel model, Dictionary<string, FieldValue> values, List<string> problems)
    {
        if (answer.State == AssemblyState.Complete && answer.Body is { } body)
        {
            foreach (var (k, v) in model.Decode(start, body)) values[k] = v;
        }
        else
        {
            problems.Add(answer.Error ?? "no answer");
        }
    }

    /// <summary>
    /// One request and its answer, reassembled from as many notifications as it takes, within the per-command timeout.
    /// A Modbus answer does not name its registers, and the station answers in order: a whole answer is this request's
    /// only when no earlier unanswered request of <paramref name="unanswered"/> could have sent it (same byte count, or any
    /// request for an exception reply); otherwise it is taken as that earlier request's late answer, set aside, and the
    /// wait goes on. So a late one-register answer (all of them have byte count 2) is never another register's value.
    /// </summary>
    private async Task<ResponseAssembler> ExchangeAsync(KeyExchangeGuardLink link, ReadRequest request, List<ReadRequest> unanswered, CancellationToken ct)
    {
        // What came since the last answer is late: never read as this request's. Its whole answers settle their requests.
        var late = new List<byte>();
        while (link.Notifications.TryRead(out var note)) late.AddRange(note);
        SettleLate(late, unanswered);
        Expect(unanswered, request);   // until its answer is in; a failed write may still have reached the station
        var answer = new ResponseAssembler(request);
        ReadRequest? setAside = null;
        int asks = 0;
        if (!await TryWriteAsync(link, request, answer, ct)) return answer;
        // Each write gets its own per-command timeout: at most 1 + MaxReasks of them, inside the poll's budget (ct).
        var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(_commandTimeout);
        try
        {
            while (true)
            {
                while (answer.Feed(await link.Notifications.ReadAsync(wait.Token)) == AssemblyState.NeedMore) { }
                if (answer.State == AssemblyState.Invalid) break;
                int from = Sender(unanswered, answer.State == AssemblyState.Exception ? null : request.Quantity);
                var sender = unanswered[from];
                // In order: every request before the sender is lost; its own answer is in.
                unanswered.RemoveRange(0, from + 1);
                // This request, or an earlier one for the same registers (this one's answer then stays expected): their own value either way.
                if (sender.Start == request.Start && sender.Quantity == request.Quantity) break;
                setAside = sender;
                answer = new ResponseAssembler(request);
                if (asks++ < MaxReasks)
                {
                    // The set-aside answer may have been this request's own (the earlier one then lost): ask the same registers
                    // again, so an answer that can only be theirs follows. Still the one read request, nothing else.
                    Expect(unanswered, request);
                    if (!await TryWriteAsync(link, request, answer, ct)) return answer;
                    wait.Dispose();
                    wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(_commandTimeout);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string aside = setAside is null ? "" : string.Create(CultureInfo.InvariantCulture,
                $"; an answer that may be the late one to registers {setAside.Start}+{setAside.Quantity} was set aside");
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture,
                $"no complete answer to registers {request.Start}+{request.Quantity} within {_commandTimeout.TotalSeconds:0.#} s ({answer.Received} of {request.ExpectedLength} bytes){aside}"));
        }
        catch (Exception e) when (e is IOException or ChannelClosedException)
        {
            // The link dropped under the request (the station disconnected): the same, a new connection for the next one.
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture,
                $"the link dropped before the answer to registers {request.Start}+{request.Quantity} ({answer.Received} of {request.ExpectedLength} bytes): {e.Message}"));
        }
        finally
        {
            wait.Dispose();
        }
        return answer;
    }

    /// <summary>The most unanswered requests remembered; older ones are taken as lost, so a station that keeps garbling never grows the list.</summary>
    private const int MaxUnanswered = 16;

    private static void Expect(List<ReadRequest> unanswered, ReadRequest request)
    {
        unanswered.Add(request);
        if (unanswered.Count > MaxUnanswered) unanswered.RemoveRange(0, unanswered.Count - MaxUnanswered);
    }

    /// <summary>Writes the request; false (the answer invalid, naming why) when the write fails.</summary>
    private static async Task<bool> TryWriteAsync(KeyExchangeGuardLink link, ReadRequest request, ResponseAssembler answer, CancellationToken ct)
    {
        try
        {
            await link.WriteAsync(request, ct);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A failed write is a lost request, as a timeout: this connection ends, the next request goes over a new one.
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture, $"the request for registers {request.Start}+{request.Quantity} could not be written ({e.GetType().Name}: {e.Message})"));
            return false;
        }
    }

    /// <summary>
    /// The earliest unanswered request that could have sent a whole answer: with <paramref name="quantity"/> registers (a
    /// data answer's byte count) or, for an exception reply (null), any. The request just written is last and always fits.
    /// </summary>
    private static int Sender(List<ReadRequest> unanswered, int? quantity) =>
        quantity is { } q ? unanswered.FindIndex(u => u.Quantity == q) : 0;

    /// <summary>Settles the requests whose answers are whole in <paramref name="late"/> (bytes that arrived between two requests); stops at the first bytes that are not a whole, valid answer.</summary>
    private static void SettleLate(List<byte> late, List<ReadRequest> unanswered)
    {
        int p = 0;
        while (unanswered.Count > 0 && late.Count - p >= 5 && late[p] == ModbusRead.UnitId)
        {
            byte function = late[p + 1];
            int length = function == (ModbusRead.ReadHoldingRegisters | 0x80) ? 5
                : function == ModbusRead.ReadHoldingRegisters && late[p + 2] % 2 == 0 ? late[p + 2] + 5 : -1;
            if (length < 0 || late.Count - p < length) return;
            var frame = late.GetRange(p, length).ToArray();
            ushort crc = ModbusCrc.Compute(frame.AsSpan(0, length - 2));
            if (frame[^2] != (byte)crc || frame[^1] != (byte)(crc >> 8)) return;
            int from = Sender(unanswered, function == ModbusRead.ReadHoldingRegisters ? frame[2] / 2 : null);
            if (from < 0) return;
            unanswered.RemoveRange(0, from + 1);
            p += length;
        }
    }

    /// <summary>No reading; the cause is logged when it changes, so a station off for hours is one line.</summary>
    private Reading? Miss(string why)
    {
        Note($"no reading: {why}");
        return null;
    }

    private void Note(string text)
    {
        // The first line keeps its byte counts; a repeat that differs only in them is the same line.
        string key = ByteCounts.Replace(text, "(bytes)");
        if (Superseded()) return;
        lock (_noteLock)
        {
            if (string.Equals(key, _lastKey, StringComparison.Ordinal)) return;
            _lastKey = key;
            _lastMiss = text;
            log($"native BLE: {text}");
        }
    }

    /// <summary>True on the flow of a read given up at the cap after a newer one started: it leaves the log state alone.</summary>
    private bool Superseded()
    {
        long mine = FlowGeneration.Value;
        return mine != 0 && mine != Interlocked.Read(ref _generation);
    }

    private void Recovered()
    {
        if (Superseded()) return;
        lock (_noteLock)
        {
            if (_lastMiss is null) return;
            log("native BLE: reading again");
            _lastMiss = null;
            _lastKey = null;
        }
    }
}
