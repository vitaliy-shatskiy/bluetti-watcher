using System.Globalization;

namespace BluettiWatcher.Modes;

/// <summary>
/// The modes feature in the service: the settings, the selector, the reconciler and the flag word, behind one lock. Each
/// poll (<see cref="StepAsync"/>) decides the mode and reconciles towards it; an API change reconciles at once. A station
/// emergency (decided in StationDecision, outside the modes) overrides both: the mode's actions stay, the flag keeps saying
/// emergency (the engine does not write over it).
/// </summary>
internal sealed class ModesEngine(StationConfig config, ModuleRegistry registry, ModesStore store, ModeJournal journal, IFlagWordWriter flag,
    TimeProvider time, Action<string> log, Action<string, string>? onEvent = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ModeReconciler _reconciler = new(registry, journal, time, onEvent);
    private ModesSettings _settings = new();
    private SelectorInput? _input;
    private string _reason = "";
    private bool _emergency;
    private FlagWord? _flagWritten;
    private string? _flagFailure;
    private readonly List<string> _problems = [];

    public ModuleRegistry Registry => registry;
    public ModesSettings Settings => _settings;
    public ReconcileSnapshot Snapshot => _reconciler.Snapshot;
    public string Reason => _reason;
    public bool Emergency => _emergency;
    /// <summary>Start problems (settings or journal), shown by the API until the next start.</summary>
    public IReadOnlyList<string> Problems => [.. _problems];
    public bool AutoHibernate => _settings.AutoHibernate;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Loads the settings and replays the journal before any write. An untrusted journal is moved aside and the start is
    /// made safe: the fallback mode is held (manual) and nothing is restored, since nothing of before is known.
    /// </summary>
    public void Start(DateTimeOffset bootAt)
    {
        var now = time.GetUtcNow();
        var (settings, problem) = store.Load(() => BuiltInModes.HasLegacySettings(config)
            ? BuiltInModes.Migrate(config)
            : BuiltInModes.Defaults(config.MainsLostSec, config.MainsBackSec), now);
        _settings = settings;
        if (problem is not null) Problem(problem);
        CarryNoHibernate();
        var load = journal.Load();
        if (load.Problem is null)
        {
            _reconciler.Replay(load.Records, bootAt);
            CompactJournal();
            return;
        }
        string aside = journal.Quarantine(now);
        _settings = _settings with { Selection = new Selection { Kind = SelectionKind.Manual, ManualModeId = _settings.FallbackModeId } };
        SaveQuietly();
        Problem($"{load.Problem}: moved to {Path.GetFileName(aside)}; nothing restored, {_settings.FallbackModeId} held until Auto");
    }

    /// <summary>
    /// The legacy <c>no-hibernate</c> file becomes autoHibernate false and is renamed once that is saved: the setting is
    /// then the only switch, so turning it on in the page hibernates again.
    /// </summary>
    private void CarryNoHibernate()
    {
        string file = BuiltInModes.NoHibernatePath(config);
        if (!File.Exists(file)) return;
        _settings = _settings with { AutoHibernate = false };
        if (!store.Writable) return;
        try
        {
            store.Save(_settings);
            File.Move(file, file + ".migrated", overwrite: true);
            log("modes: no-hibernate carried into autoHibernate false and renamed no-hibernate.migrated");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Problem($"no-hibernate not carried: {e.Message}"); }
    }

    private void CompactJournal()
    {
        try
        {
            int dropped = journal.Compact();
            if (dropped > 0) log($"modes: journal compacted, {dropped.ToString(CultureInfo.InvariantCulture)} old records dropped");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"modes: journal not compacted: {e.Message}"); }
    }

    private void Problem(string text)
    {
        _problems.Add(text);
        log($"modes: {text}");
        onEvent?.Invoke("modes-problem", text);
    }

    /// <summary>One poll: decide, save a changed selection, reconcile, write the flag word.</summary>
    public async Task StepAsync(SelectorInput input, bool emergency, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _input = input;
            // The emergency writes the flag outside the engine: once it is over the mode's word is written again.
            if (emergency) _flagWritten = null;
            _emergency = emergency;
            await ReconcileAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var input = _input ?? new SelectorInput(null, null, null, null, time.GetLocalNow());
        var d = ModeSelector.Decide(_settings, input);
        if (d.Selection != _settings.Selection)
        {
            _settings = _settings with { Selection = d.Selection };
            SaveQuietly();
        }
        var mode = _settings.Modes.First(m => m.Id == d.ModeId);
        if (!string.Equals(_reconciler.ActiveModeId, mode.Id, StringComparison.Ordinal))
        {
            log($"modes: {mode.Id} ({d.Reason})");
            onEvent?.Invoke("mode-changed", $"{mode.Id} ({d.Reason})");
        }
        _reason = d.Reason;
        await _reconciler.ReconcileAsync(mode, ct);
        if (journal.Count > ModeJournal.CompactAbove) CompactJournal();
        if (_emergency || _flagWritten == mode.FlagWord) return;
        string why = await flag.WriteAsync(mode.FlagWord, ct);
        if (why.Length == 0)
        {
            _flagWritten = mode.FlagWord;
            _flagFailure = null;
        }
        else if (why != _flagFailure)
        {
            // Tried every poll, alerted once per reason: a refused flag folder is not a notification a minute.
            _flagFailure = why;
            onEvent?.Invoke("action-failed", $"power-mode flag: {why}");
        }
    }

    private void SaveQuietly()
    {
        if (!store.Writable) return;
        try { store.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Problem($"{ModesStore.FileName} not saved: {e.Message}"); }
    }

    /// <summary>"auto" or a mode id; (0, "") = done · else (HTTP status, why).</summary>
    public Task<(int Status, string Error)> SelectAsync(string choice, CancellationToken ct) => ChangeAsync(s =>
    {
        if (choice == "auto") return (s with { Selection = s.Selection with { Kind = SelectionKind.Auto, ManualModeId = null } }, "");
        return s.Modes.Any(m => m.Id == choice)
            ? (s with { Selection = s.Selection with { Kind = SelectionKind.Manual, ManualModeId = choice } }, "")
            : (s, $"404:no mode {choice}");
    }, ct);

    /// <summary>Adds (<paramref name="create"/>) or replaces a mode. A built-in keeps its id and stays built in.</summary>
    public Task<(int Status, string Error)> PutModeAsync(Mode mode, bool create, CancellationToken ct) => ChangeAsync(s =>
    {
        bool exists = s.Modes.Any(m => m.Id == mode.Id);
        if (create && exists) return (s, $"409:mode {mode.Id} exists");
        if (create && s.Modes.Count >= ModesValidation.MaxModes) return (s, $"409:at most {ModesValidation.MaxModes.ToString(CultureInfo.InvariantCulture)} modes");
        if (!create && !exists) return (s, $"404:no mode {mode.Id}");
        if (ModesValidation.Mode(mode, registry) is { Count: > 0 } bad) return (s, "400:" + string.Join("; ", bad));
        var put = mode with { BuiltIn = BuiltInModes.IsBuiltIn(mode.Id) };
        return (s with { Modes = create ? [.. s.Modes, put] : [.. s.Modes.Select(m => m.Id == mode.Id ? put : m)] }, "");
    }, ct);

    public Task<(int Status, string Error)> DeleteModeAsync(string id, CancellationToken ct) => ChangeAsync(s =>
    {
        if (BuiltInModes.IsBuiltIn(id)) return (s, $"409:{id} is built in: editable, not deletable");
        if (s.Modes.All(m => m.Id != id)) return (s, $"404:no mode {id}");
        if (s.Rules.Any(r => r.ModeId == id)) return (s, $"409:a rule chooses {id}: change the rules first");
        if (s.FallbackModeId == id) return (s, $"409:{id} is the fallback mode");
        return (s with { Modes = [.. s.Modes.Where(m => m.Id != id)] }, "");
    }, ct);

    public Task<(int Status, string Error)> PutRulesAsync(IReadOnlyList<Rule> rules, CancellationToken ct) => ChangeAsync(s =>
    {
        if (ModesValidation.Rules(rules, s.Modes) is { Count: > 0 } bad) return (s, "400:" + string.Join("; ", bad));
        // The rule in force is an index into the old list: it no longer means anything.
        return (s with { Rules = rules, Selection = s.Selection with { AutoRule = null } }, "");
    }, ct);

    public Task<(int Status, string Error)> SetAutoHibernateAsync(bool on, CancellationToken ct) => ChangeAsync(s => (s with { AutoHibernate = on }, ""), ct);

    /// <summary>
    /// Applies <paramref name="change"/> (its error "status:text" refuses it), saves, and reconciles at once so the API
    /// answers with the new statuses.
    /// </summary>
    private async Task<(int Status, string Error)> ChangeAsync(Func<ModesSettings, (ModesSettings Next, string Error)> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (next, error) = change(_settings);
            if (error.Length > 0)
            {
                int colon = error.IndexOf(':', StringComparison.Ordinal);
                return (int.Parse(error[..colon], CultureInfo.InvariantCulture), error[(colon + 1)..]);
            }
            if (!store.Writable) return (409, $"{ModesStore.FileName} is not writable by this build: {string.Join("; ", _problems)}");
            try { store.Save(next); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (500, $"{ModesStore.FileName} not saved: {e.Message}"); }
            _settings = next;
            await ReconcileAsync(ct);
            return (0, "");
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<JournalRecord> Journal(long since) => journal.Since(since);
}
