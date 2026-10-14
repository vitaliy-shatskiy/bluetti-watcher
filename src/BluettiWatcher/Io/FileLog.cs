using System.Globalization;
using System.Text;

namespace BluettiWatcher.Io;

/// <summary>
/// service.log in the data directory, one line per event; rolled to service.log.1 past 1 MB, so two files of 1 MB at
/// most. A line identical to the one before is counted instead of written: the count reaches the file as one line
/// ("repeated N times since HH:mm:ss: text", stamped with the last repeat) before the next different line, and at least
/// every 15 minutes while the line keeps repeating. A single repeat is written as itself. The first occurrence of a line
/// is always written at once; a pending count is written by a timer at most <see cref="RepeatFlush"/> after it began, so a
/// killed process loses the count of the last repeats only, never a distinct line. Nothing is flushed to disk beyond the OS cache.
/// </summary>
internal sealed class FileLog : IDisposable
{
    /// <summary>The longest a repeated line waits unwritten: the log's last line is never older than this while it runs.</summary>
    internal static readonly TimeSpan RepeatFlush = TimeSpan.FromMinutes(15);

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly long _rollBytes;
    private readonly Lock _gate = new();
    private string? _lastText;
    private DateTimeOffset _lastWritten;
    private int _repeats;
    private DateTimeOffset _firstRepeat;
    private DateTimeOffset _lastRepeat;
    private readonly ITimer _timer;
    private readonly EventHandler _onExit;
    private bool _disposed;

    public FileLog(string path, TimeProvider? time = null, long rollBytes = 1_000_000)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _rollBytes = rollBytes;
        _timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // A clean exit writes a pending count; a killed process (no exit event) loses the repeats of the last RepeatFlush only.
        _onExit = (_, _) => Flush();
        AppDomain.CurrentDomain.ProcessExit += _onExit;
    }

    /// <summary>Stops the timer, leaves the process-exit event and writes a pending count.</summary>
    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= _onExit;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            string pending = TakeRepeats();
            if (pending.Length > 0) Append(pending, _lastRepeat);
        }
    }

    /// <summary>The count that began at <see cref="_firstRepeat"/> is written once it is RepeatFlush old; a newer count re-arms.</summary>
    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed || _repeats == 0) return;
            var due = _firstRepeat + RepeatFlush - _time.GetLocalNow();
            if (due > TimeSpan.Zero) _timer.Change(due, Timeout.InfiniteTimeSpan);
            else Append(TakeRepeats(), _lastRepeat);
        }
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Line(DateTimeOffset at, string text) => Stamp(at) + "  " + text + "\n";

    /// <summary>The pending repeats as one line ("" when none); the count starts over.</summary>
    private string TakeRepeats()
    {
        int n = _repeats;
        _repeats = 0;
        return n switch
        {
            0 => "",
            1 => Line(_lastRepeat, _lastText!),
            _ => Line(_lastRepeat, $"repeated {n.ToString(CultureInfo.InvariantCulture)} times since {_firstRepeat.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}: {_lastText}"),
        };
    }

    public void Write(string text)
    {
        var now = _time.GetLocalNow();
        lock (_gate)
        {
            if (text == _lastText)
            {
                if (_repeats == 0)
                {
                    _firstRepeat = now;
                    if (!_disposed) _timer.Change(RepeatFlush, Timeout.InfiniteTimeSpan);
                }
                _repeats++;
                _lastRepeat = now;
                if (now - _lastWritten < RepeatFlush) return;
                Append(TakeRepeats(), now);
                return;
            }
            string pending = TakeRepeats();
            _lastText = text;
            Append(pending + Line(now, text), now);
        }
    }

    /// <summary>Writes a pending repeat count now.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            string pending = TakeRepeats();
            if (pending.Length > 0) Append(pending, _lastRepeat);
        }
    }

    private void Append(string lines, DateTimeOffset at)
    {
        _lastWritten = at;
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > _rollBytes) File.Move(_path, _path + ".1", overwrite: true);
            File.AppendAllText(_path, lines, Utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is best effort: a locked or read-only log file never stops a poll, nor the flush timer's thread.
            return;
        }
    }
}
