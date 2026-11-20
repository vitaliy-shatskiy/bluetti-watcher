using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BluettiWatcher.Modes;

/// <summary>
/// The append-only JSON-lines journal of every write the modes make. Each record is one write of one line, flushed to the
/// disk before the call returns, so a record that was acted on is durable. A last line without its newline is the write a
/// crash cut short (never acted on) and is ignored; any other unreadable line makes the whole journal untrusted.
/// Not thread-safe: the engine is its single writer.
/// </summary>
internal sealed class ModeJournal(string path)
{
    public const string FileName = "modes-journal.jsonl";
    /// <summary>The most records <see cref="Since"/> returns at once.</summary>
    public const int PageSize = 500;

    private readonly List<JournalRecord> _records = [];
    private long _seq;

    public string Path => path;

    /// <summary>Reads the file (absent = empty). Untrusted: the problem is returned and nothing is kept.</summary>
    public JournalLoad Load()
    {
        _records.Clear();
        _seq = 0;
        if (!File.Exists(path)) return new JournalLoad([], null);
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new JournalLoad([], $"journal unreadable: {e.Message}"); }
        int end = text.LastIndexOf('\n') + 1;
        // A tail without its newline is a write a crash cut short: Append returns only after the whole line is on the disk,
        // so nothing acted on it. It is cut off, or the next record would be glued to it.
        if (end < text.Length)
        {
            text = text[..end];
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                fs.SetLength(Encoding.UTF8.GetByteCount(text));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new JournalLoad([], $"journal torn tail cannot be cut: {e.Message}"); }
        }
        var lines = text.Split('\n');
        var records = new List<JournalRecord>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            JournalRecord? r;
            try { r = JsonSerializer.Deserialize(line, ModesJson.Default.JournalRecord); }
            catch (JsonException e) { return Corrupt(i, e.Message); }
            if (r is null || r.Seq <= _seq) return Corrupt(i, "record out of order");
            _seq = r.Seq;
            records.Add(r);
        }
        _records.AddRange(records);
        return new JournalLoad(records, null);
    }

    private JournalLoad Corrupt(int line, string why)
    {
        _records.Clear();
        _seq = 0;
        return new JournalLoad([], $"journal line {(line + 1).ToString(CultureInfo.InvariantCulture)} unreadable: {why}");
    }

    /// <summary>Moves an untrusted journal aside (kept for a person to read) so new records start a clean file.</summary>
    public string Quarantine(DateTimeOffset now)
    {
        string aside = $"{path}.corrupt-{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        if (File.Exists(path)) File.Move(path, aside, overwrite: true);
        _records.Clear();
        _seq = 0;
        return aside;
    }

    /// <summary>Appends <paramref name="record"/> with the next sequence number and returns it as written.</summary>
    public JournalRecord Append(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var r = record with { Seq = _seq + 1 };
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, ModesJson.Default.JournalRecord) + "\n");
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(line);
            fs.Flush(flushToDisk: true);
        }
        _seq = r.Seq;
        _records.Add(r);
        return r;
    }

    /// <summary>The records after <paramref name="since"/>, oldest first, at most <see cref="PageSize"/>.</summary>
    public IReadOnlyList<JournalRecord> Since(long since) => [.. _records.Where(r => r.Seq > since).Take(PageSize)];
}
