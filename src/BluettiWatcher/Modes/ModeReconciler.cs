using System.Globalization;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// Brings the machine to the active mode and answers for every write it makes. Per resource: read; a failed read writes
/// nothing and claims nothing; already equivalent = satisfied (nothing to undo); else Prepared(before, target) is journaled,
/// the module writes and reads back, and Applied(written) is journaled. A resource is ours only while it still reads what
/// we wrote: leaving a mode restores the baseline only then, and a resource the person changed is let go (relinquished),
/// or re-applied when its action has <see cref="ModeAction.Enforce"/>. A failed restore is retried on later passes, at most
/// <see cref="MaxRestoreTries"/> times. One action failing never stops the others. Single writer: the engine calls it under
/// its own lock.
/// </summary>
internal sealed class ModeReconciler(ModuleRegistry registry, ModeJournal journal, TimeProvider time, Action<string, string>? onEvent = null)
{
    public const int MaxRestoreTries = 3;
    /// <summary>The longest pause between two tries of a write that keeps failing (1, 2, 4 ... minutes up to this).</summary>
    public static readonly TimeSpan MaxRetryPause = TimeSpan.FromMinutes(30);

    /// <summary>Writes that failed in this activation: not tried again before <c>RetryAt</c>.</summary>
    private readonly Dictionary<string, (int Fails, DateTimeOffset RetryAt, string Error)> _failing = new(StringComparer.Ordinal);
    /// <summary>Keys whose failure was alerted in this activation: one action-failed per mode entry and resource.</summary>
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    private readonly Dictionary<string, OwnedResource> _owned = new(StringComparer.Ordinal);
    /// <summary>Keys the person took over during this activation: not written again until the mode is entered anew.</summary>
    private readonly HashSet<string> _relinquished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceStatus> _restores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleAvailability> _available = new(StringComparer.Ordinal);
    /// <summary>Statuses of <see cref="IChecksOnTransition"/> resources settled in this activation: not read again until the mode changes.</summary>
    private readonly Dictionary<string, (ActionStatus, string)> _checked = new(StringComparer.Ordinal);
    private string? _activeModeId;
    private volatile ReconcileSnapshot _snapshot = ReconcileSnapshot.Empty;

    public ReconcileSnapshot Snapshot => _snapshot;
    public IReadOnlyDictionary<string, OwnedResource> Owned => _owned;
    public string? ActiveModeId => _activeModeId;

    /// <summary>
    /// Rebuilds ownership from the journal, before any new write. A token resource (a pause, a hold) written before
    /// <paramref name="bootAt"/> did not survive the reboot and is let go; persistent resources stay owned and are checked
    /// against what they read on the next pass.
    /// </summary>
    public void Replay(IReadOnlyList<JournalRecord> records, DateTimeOffset bootAt)
    {
        ArgumentNullException.ThrowIfNull(records);
        _owned.Clear();
        foreach (var r in records)
        {
            switch (r.Kind)
            {
                case JournalKind.ModeEntered: _activeModeId = r.ModeId; break;
                case JournalKind.Prepared:
                    bool prior = _owned.TryGetValue(r.Key, out var was) && was.Applied;
                    _owned[r.Key] = new OwnedResource
                    {
                        Key = r.Key, TypeId = r.TypeId, ModeId = r.ModeId, Before = Clone(r.Before), Target = Clone(r.Value),
                        PriorWritten = prior ? Clone(was!.Written) : null, HadPrior = prior, At = r.At,
                    };
                    break;
                case JournalKind.Applied:
                    if (_owned.TryGetValue(r.Key, out var own))
                        _owned[r.Key] = own with { ModeId = r.ModeId, Written = Clone(r.Value), Applied = true, HadPrior = false, PriorWritten = null, RestoreTries = 0, At = r.At };
                    break;
                case JournalKind.RestoreFailed:
                    if (r.Final) _owned.Remove(r.Key);
                    else if (_owned.TryGetValue(r.Key, out var failing)) _owned[r.Key] = failing with { RestoreTries = failing.RestoreTries + 1 };
                    break;
                default: _owned.Remove(r.Key); break;   // relinquished, restored, dropped
            }
        }
        // a resource of a type this build removed has no module left to restore it with: let go, never a restore failure
        foreach (var own in _owned.Values.Where(o => RetiredActions.TypeIds.Contains(o.TypeId)).ToList())
            Close(own, JournalKind.Dropped, "the action type was removed");
        foreach (var own in _owned.Values.Where(o => o.At < bootAt).ToList())
        {
            if (registry.TryGet(own.TypeId, out var m) && m.Descriptor.Restoration == RestorationClass.Token)
                Close(own, JournalKind.Relinquished, "runtime state did not survive the reboot");
        }
        foreach (var own in _owned.Values.Where(o => o.Applied))
        {
            if (registry.TryGet(own.TypeId, out var m) && m is IRecallsWrites recalls) recalls.Recall(own.Key, Clone(own.Target));
        }
    }

    /// <summary>One pass towards <paramref name="target"/>: settle open writes, restore what the last mode left, apply this one.</summary>
    public async Task ReconcileAsync(Mode target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(_activeModeId, target.Id, StringComparison.Ordinal))
        {
            journal.Append(new JournalRecord { At = time.GetUtcNow(), Kind = JournalKind.ModeEntered, ModeId = target.Id });
            _activeModeId = target.Id;
            _relinquished.Clear();
            _failing.Clear();
            _alerted.Clear();
            _available.Clear();
            _checked.Clear();
            foreach (var key in _restores.Where(kv => kv.Value.Status != ActionStatus.RestoreFailed || !_owned.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
                _restores.Remove(key);
        }
        foreach (var own in _owned.Values.Where(o => !o.Applied).ToList()) await SettleAsync(own, ct);

        var wanted = new Dictionary<string, (int Action, IActionModule Module, ResourceIntent Intent, bool Enforce)>(StringComparer.Ordinal);
        var perAction = new List<(int Index, string TypeId, List<(ActionStatus, string)> Keys)>();
        for (int i = 0; i < target.Actions.Count; i++)
        {
            var a = target.Actions[i];
            if (!a.Enabled) continue;
            var keys = new List<(ActionStatus, string)>();
            perAction.Add((i, a.TypeId, keys));
            if (await AvailabilityAsync(a.TypeId, ct) is { Available: false } no) { keys.Add((ActionStatus.Unsupported, no.Reason)); continue; }
            registry.TryGet(a.TypeId, out var module);
            IReadOnlyList<ResourceIntent> intents;
            try { intents = module.Expand(a.Settings); }
            catch (Exception e) when (e is not OperationCanceledException) { keys.Add((ActionStatus.Failed, $"settings: {e.Message}")); continue; }
            foreach (var intent in intents)
            {
                if (wanted.TryGetValue(intent.Key, out var first))
                    keys.Add((ActionStatus.Failed, $"{intent.Key} is already set by action {(first.Action + 1).ToString(CultureInfo.InvariantCulture)}"));
                else wanted[intent.Key] = (i, module, intent, a.Enforce);
            }
        }

        foreach (var own in _owned.Values.Where(o => !wanted.ContainsKey(o.Key)).ToList()) await RestoreAsync(own, ct);

        foreach (var (key, w) in wanted)
        {
            if (w.Module is IChecksOnTransition && _checked.TryGetValue(key, out var kept))
            {
                perAction.First(p => p.Index == w.Action).Keys.Add(kept);
                continue;
            }
            var status = await ApplyAsync(target.Id, w.Module, w.Intent, w.Enforce, ct);
            if (w.Module is IChecksOnTransition && status.Item1 is not (ActionStatus.Failed or ActionStatus.Unknown)) _checked[key] = status;
            perAction.First(p => p.Index == w.Action).Keys.Add(status);
        }
        _snapshot = new ReconcileSnapshot(target.Id,
            [.. perAction.Select(p => Aggregate(p.Index, p.TypeId, p.Keys))],
            [.. _restores.Values.OrderBy(r => r.Key, StringComparer.Ordinal)]);
    }

    private async Task<ModuleAvailability> AvailabilityAsync(string typeId, CancellationToken ct)
    {
        if (_available.TryGetValue(typeId, out var known)) return known;
        ModuleAvailability a;
        if (!registry.TryGet(typeId, out var m)) a = ModuleAvailability.No($"unknown action type {typeId}");
        else
        {
            try { a = await m.AvailableAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { a = ModuleAvailability.No(e.Message); }
        }
        _available[typeId] = a;
        return a;
    }

    /// <summary>The most telling status of an action's resources wins: a failure over a hand change over an unknown.</summary>
    private static ActionReport Aggregate(int index, string typeId, List<(ActionStatus Status, string Detail)> keys)
    {
        ActionStatus[] order = [ActionStatus.Unsupported, ActionStatus.Failed, ActionStatus.Relinquished, ActionStatus.Unknown, ActionStatus.Applied, ActionStatus.Satisfied];
        foreach (var s in order)
            if (keys.FirstOrDefault(k => k.Status == s) is { Detail: not null } hit) return new ActionReport(index, typeId, s, hit.Detail);
        return new ActionReport(index, typeId, ActionStatus.Satisfied, "");
    }

    /// <summary>A write prepared with no outcome: what the resource reads now says whether it took.</summary>
    private async Task SettleAsync(OwnedResource own, CancellationToken ct)
    {
        if (!registry.TryGet(own.TypeId, out var m)) return;
        var read = await ReadAsync(m, own.Key, ct);
        if (!read.Ok) return;   // still unknown: kept open, nothing written over it
        if (Same(m, own.Key, read.Value, own.Target)) Record(own, read.Value, own.ModeId);
        else if (own.HadPrior && Same(m, own.Key, read.Value, own.PriorWritten)) Record(own, read.Value, own.ModeId);
        else if (Same(m, own.Key, read.Value, own.Before)) Close(own, JournalKind.Dropped, "the write did not take");
        else Close(own, JournalKind.Relinquished, "changed while the write was open");
    }

    private async Task<(ActionStatus, string)> ApplyAsync(string modeId, IActionModule m, ResourceIntent intent, bool enforce, CancellationToken ct)
    {
        string key = intent.Key;
        if (_relinquished.Contains(key)) return (ActionStatus.Relinquished, $"{key} changed by hand");
        if (_failing.TryGetValue(key, out var failing) && time.GetUtcNow() < failing.RetryAt)
            return (ActionStatus.Failed, $"{key}: {failing.Error} - next try at {failing.RetryAt.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC");
        var read = await ReadAsync(m, key, ct);
        if (!read.Ok) return (ActionStatus.Failed, $"{key}: {read.Error}");
        if (_owned.TryGetValue(key, out var own))
        {
            if (!own.Applied) return (ActionStatus.Unknown, $"{key}: an earlier write is not settled");
            bool sameMode = string.Equals(own.ModeId, modeId, StringComparison.Ordinal);
            if (Same(m, key, read.Value, own.Written))
            {
                if (Same(m, key, own.Written, intent.Target))
                {
                    if (!sameMode) Record(own, own.Written, modeId);   // the new mode takes over the write and its baseline
                    return (ActionStatus.Applied, "");
                }
                return await WriteAsync(modeId, m, intent, own.Before, own, ct);
            }
            if (sameMode && enforce) return await WriteAsync(modeId, m, intent, own.Before, own, ct);
            Close(own, JournalKind.Relinquished, "changed by hand");
            if (sameMode)
            {
                _relinquished.Add(key);
                onEvent?.Invoke("mode-drift", $"{key} changed by hand: left as it is");
                return (ActionStatus.Relinquished, $"{key} changed by hand");
            }
        }
        if (Same(m, key, read.Value, intent.Target)) return (ActionStatus.Satisfied, "");
        return await WriteAsync(modeId, m, intent, read.Value, null, ct);
    }

    private async Task<(ActionStatus, string)> WriteAsync(string modeId, IActionModule m, ResourceIntent intent, JsonNode? before, OwnedResource? prior, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        journal.Append(new JournalRecord { At = now, Kind = JournalKind.Prepared, ModeId = modeId, TypeId = m.Descriptor.TypeId, Key = intent.Key, Before = Clone(before), Value = Clone(intent.Target) });
        var open = new OwnedResource
        {
            Key = intent.Key, TypeId = m.Descriptor.TypeId, ModeId = modeId, Before = Clone(before), Target = Clone(intent.Target),
            PriorWritten = Clone(prior?.Written), HadPrior = prior is not null, At = now,
        };
        _owned[intent.Key] = open;
        ApplyResult res;
        try { res = await m.ApplyAsync(intent, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { res = ApplyResult.Failed(e.Message); }
        if (!res.Ok)
        {
            // Each failure doubles the pause before the next try: a write that cannot succeed costs a few journal lines
            // an hour, not two a poll.
            int fails = _failing.TryGetValue(intent.Key, out var was) ? was.Fails + 1 : 1;
            var pause = TimeSpan.FromMinutes(Math.Min(MaxRetryPause.TotalMinutes, Math.Pow(2, Math.Min(fails - 1, 16))));
            _failing[intent.Key] = (fails, now + pause, res.Error);
            if (_alerted.Add(intent.Key)) onEvent?.Invoke("action-failed", $"{intent.Key}: {res.Error}");
            return (ActionStatus.Failed, $"{intent.Key}: {res.Error}");   // stays open: the next pass reads what it did
        }
        _failing.Remove(intent.Key);
        _alerted.Remove(intent.Key);
        Record(open, res.ReadBack, modeId);
        return Same(m, intent.Key, res.ReadBack, intent.Target)
            ? (ActionStatus.Applied, "")
            : (ActionStatus.Failed, $"{intent.Key}: reads {Text(res.ReadBack)} after writing {Text(intent.Target)}");
    }

    private async Task RestoreAsync(OwnedResource own, CancellationToken ct)
    {
        if (!own.Applied)
        {
            _restores[own.Key] = new ResourceStatus(own.Key, own.TypeId, ActionStatus.Unknown, "an earlier write is not settled");
            return;
        }
        if (!registry.TryGet(own.TypeId, out var m)) { RestoreFailed(own, $"unknown action type {own.TypeId}"); return; }
        if (m.Descriptor.Restoration == RestorationClass.Irreversible) { Close(own, JournalKind.Dropped, "irreversible"); return; }
        var read = await ReadAsync(m, own.Key, ct);
        if (!read.Ok) { RestoreFailed(own, read.Error); return; }
        if (!Same(m, own.Key, read.Value, own.Written))
        {
            Close(own, JournalKind.Relinquished, "changed by hand");
            _restores[own.Key] = new ResourceStatus(own.Key, own.TypeId, ActionStatus.Relinquished, "changed by hand: not restored");
            onEvent?.Invoke("mode-drift", $"{own.Key} changed by hand: not restored");
            return;
        }
        ApplyResult res;
        try { res = await m.ApplyAsync(new ResourceIntent(own.Key, Clone(own.Before)), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { res = ApplyResult.Failed(e.Message); }
        if (!res.Ok) { RestoreFailed(own, res.Error); return; }
        Close(own, JournalKind.Restored, "");
        _restores.Remove(own.Key);
    }

    private void RestoreFailed(OwnedResource own, string error)
    {
        int tries = own.RestoreTries + 1;
        bool final = tries >= MaxRestoreTries;
        journal.Append(new JournalRecord { At = time.GetUtcNow(), Kind = JournalKind.RestoreFailed, ModeId = own.ModeId, TypeId = own.TypeId, Key = own.Key, Final = final, Detail = error });
        string detail = final ? $"{error} - given up after {tries.ToString(CultureInfo.InvariantCulture)} tries" : $"{error} - try {tries.ToString(CultureInfo.InvariantCulture)} of {MaxRestoreTries.ToString(CultureInfo.InvariantCulture)}";
        _restores[own.Key] = new ResourceStatus(own.Key, own.TypeId, ActionStatus.RestoreFailed, detail);
        if (final)
        {
            _owned.Remove(own.Key);
            onEvent?.Invoke("action-failed", $"{own.Key}: restore failed: {detail}");
        }
        else _owned[own.Key] = own with { RestoreTries = tries };
    }

    private void Record(OwnedResource own, JsonNode? written, string modeId)
    {
        var now = time.GetUtcNow();
        journal.Append(new JournalRecord { At = now, Kind = JournalKind.Applied, ModeId = modeId, TypeId = own.TypeId, Key = own.Key, Value = Clone(written) });
        _owned[own.Key] = own with { ModeId = modeId, Written = Clone(written), Applied = true, HadPrior = false, PriorWritten = null, RestoreTries = 0, At = now };
    }

    private void Close(OwnedResource own, JournalKind kind, string detail)
    {
        journal.Append(new JournalRecord { At = time.GetUtcNow(), Kind = kind, ModeId = own.ModeId, TypeId = own.TypeId, Key = own.Key, Detail = detail });
        _owned.Remove(own.Key);
    }

    private static async Task<ResourceRead> ReadAsync(IActionModule m, string key, CancellationToken ct)
    {
        try { return await m.ReadAsync(key, ct) ?? ResourceRead.Failed("no answer"); }
        catch (Exception e) when (e is not OperationCanceledException) { return ResourceRead.Failed(e.Message); }
    }

    /// <summary>A module that cannot compare says "different": that only ever costs a write or a let-go, never a false claim.</summary>
    private bool Same(IActionModule m, string key, JsonNode? a, JsonNode? b)
    {
        try { return m.Equivalent(key, a, b); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            onEvent?.Invoke("action-failed", $"{key}: compare failed: {e.Message}");
            return false;
        }
    }

    private static JsonNode? Clone(JsonNode? n) => n?.DeepClone();

    private static string Text(JsonNode? n) => n?.ToJsonString() ?? "null";
}
