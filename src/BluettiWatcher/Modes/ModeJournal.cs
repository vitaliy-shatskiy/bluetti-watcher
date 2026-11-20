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

    /// <summary>The record count above which the engine compacts the journal while it runs (it also compacts at each start).</summary>
    public const int CompactAbove = 5000;
    /// <summary>The most recent records a compaction keeps whatever they are: the history the page shows.</summary>
    public const int KeepRecent = 200;

    public int Count => _records.Count;

    /// <summary>
    /// Rewrites the journal with what a replay needs and the recent history: for each resource still owned, the records
    /// its state is rebuilt from (<see cref="Needed"/>); the last mode entered; the last <see cref="KeepRecent"/> records.
    /// Sequence numbers are kept, so a replay of the result owns exactly what a replay of the whole file owns. Atomic: a
    /// temporary file is flushed and moved over the journal. Returns the number of records dropped.
    /// </summary>
    public int Compact()
    {
        var chains = new Dictionary<string, List<JournalRecord>>(StringComparer.Ordinal);
        long lastMode = 0;
        foreach (var r in _records)
        {
            switch (r.Kind)
            {
                case JournalKind.ModeEntered: lastMode = r.Seq; break;
                case JournalKind.Prepared:
                    if (!chains.TryGetValue(r.Key, out var open)) chains[r.Key] = open = [];
                    open.Add(r);
                    break;
                case JournalKind.Applied or JournalKind.RestoreFailed when !r.Final:
                    if (chains.TryGetValue(r.Key, out var owned)) owned.Add(r);
                    break;
                default: chains.Remove(r.Key); break;   // relinquished, restored, dropped, a final restore failure
            }
        }
        var keep = new HashSet<long>(chains.Values.SelectMany(Needed)) { lastMode };
        keep.UnionWith(_records.Skip(Math.Max(0, _records.Count - KeepRecent)).Select(r => r.Seq));
        var kept = _records.Where(r => keep.Contains(r.Seq)).ToList();
        int dropped = _records.Count - kept.Count;
        if (dropped == 0) return 0;
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var r in kept) fs.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, ModesJson.Default.JournalRecord) + "\n"));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
        _records.Clear();
        _records.AddRange(kept);
        return dropped;
    }

    /// <summary>
    /// The records of one owned resource's chain (opened by a Prepared, never closed) that its replayed state depends on.
    /// Each Applied resets what came before it except the last Prepared (key, type, baseline, target), so: an applied
    /// write = its last Prepared, its last Applied and the restore failures after it; an open write = its Prepared and,
    /// when an applied one preceded it (the value it may have left), that Applied with the Prepared it closed.
    /// </summary>
    private static IEnumerable<long> Needed(List<JournalRecord> chain)
    {
        int lastPrepared = chain.FindLastIndex(r => r.Kind == JournalKind.Prepared);
        int lastApplied = chain.FindLastIndex(r => r.Kind == JournalKind.Applied);
        yield return chain[lastPrepared].Seq;
        if (lastApplied > lastPrepared)
        {
            for (int i = lastApplied; i < chain.Count; i++) yield return chain[i].Seq;
            yield break;
        }
        if (lastApplied < 0) yield break;
        yield return chain[lastApplied].Seq;
        yield return chain[chain.FindLastIndex(lastApplied, r => r.Kind == JournalKind.Prepared)].Seq;
    }

    /// <summary>The records after <paramref name="since"/>, oldest first, at most <see cref="PageSize"/>.</summary>
    public IReadOnlyList<JournalRecord> Since(long since) => [.. _records.Where(r => r.Seq > since).Take(PageSize)];
}
