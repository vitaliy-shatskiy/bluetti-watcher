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
/// invalid ends its connection, and the next request goes over a new one: a late answer can never fill another request.
/// </summary>
internal sealed partial class NativeBleReader(StationConfig config, IBleConnector connector, Action<string> log,
    TimeSpan? commandTimeout = null, TimeSpan? hardMargin = null) : IStationReader
{
    /// <summary>The library's per-command wait (device_reader.py: wait_for(future, 5)).</summary>
    private readonly TimeSpan _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5);
    /// <summary>A read still running this long past readerTimeoutSec (a Bluetooth call that ignored its cancellation) is given up.</summary>
    private readonly TimeSpan _hardMargin = hardMargin ?? TimeSpan.FromSeconds(2);
    /// <summary>New connections one poll may open after dropping one.</summary>
    private const int MaxReconnects = 2;
    private string? _lastMiss;
    /// <summary>What the last note was, its byte counts left out: a repeat of the same miss is not logged again.</summary>
    private string? _lastKey;
    /// <summary>The station opened the encrypted handshake once: it is not connected to again until the service restarts.</summary>
    private volatile bool _encrypted;
    private const string EncryptedMiss = EncryptedStationException.Cause + "; not connecting to it again until the service restarts";
    private static readonly System.Text.RegularExpressions.Regex ByteCounts = ByteCountsPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\(\d+ of \d+ bytes\)|(?<=bytes )\(\d+\)")]
    private static partial System.Text.RegularExpressions.Regex ByteCountsPattern();

    /// <summary>One read, bounded as a whole by readerTimeoutSec plus a margin, whatever the Bluetooth stack does.</summary>
    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(1, config.ReaderTimeoutSec)) + _hardMargin;
        var read = ReadOnceAsync(ct);
        try
        {
            return await read.WaitAsync(limit, ct);
        }
        catch (TimeoutException)
        {
            // The abandoned read ends on its own, or never; its outcome is dropped (observed, so it never surfaces).
            _ = read.ContinueWith(static t => t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Miss(string.Create(CultureInfo.InvariantCulture,
                $"the Bluetooth stack did not return within {limit.TotalSeconds:0.#} s (readerTimeoutSec {config.ReaderTimeoutSec} + {_hardMargin.TotalSeconds:0.#} s)"));
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

        // One request over the current connection, opening one first when there is none; null = no further request this poll.
        async Task<ResponseAssembler?> NextAsync(int start, int quantity)
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
            var answer = await ExchangeAsync(link, ModbusRead.Request(start, quantity), budget.Token);
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
            else if (_lastMiss is not null) { log("native BLE: reading again"); _lastMiss = null; _lastKey = null; }
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

    /// <summary>One request and its answer, reassembled from as many notifications as it takes, within the per-command timeout.</summary>
    private async Task<ResponseAssembler> ExchangeAsync(KeyExchangeGuardLink link, ReadRequest request, CancellationToken ct)
    {
        // An answer that came after its own request timed out must not be read as the next one's.
        while (link.Notifications.TryRead(out _)) { }
        var answer = new ResponseAssembler(request);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(_commandTimeout);
        try
        {
            await link.WriteAsync(request, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A failed write is a lost request, as a timeout: this connection ends, the next request goes over a new one.
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture, $"the request for registers {request.Start}+{request.Quantity} could not be written ({e.GetType().Name}: {e.Message})"));
            return answer;
        }
        try
        {
            while (answer.Feed(await link.Notifications.ReadAsync(wait.Token)) == AssemblyState.NeedMore) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture,
                $"no complete answer to registers {request.Start}+{request.Quantity} within {_commandTimeout.TotalSeconds:0.#} s ({answer.Received} of {request.ExpectedLength} bytes)"));
        }
        catch (Exception e) when (e is IOException or ChannelClosedException)
        {
            // The link dropped under the request (the station disconnected): the same, a new connection for the next one.
            answer.TimedOut(string.Create(CultureInfo.InvariantCulture,
                $"the link dropped before the answer to registers {request.Start}+{request.Quantity} ({answer.Received} of {request.ExpectedLength} bytes): {e.Message}"));
        }
        return answer;
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
        if (string.Equals(key, _lastKey, StringComparison.Ordinal)) return;
        _lastKey = key;
        _lastMiss = text;
        log($"native BLE: {text}");
    }
}
