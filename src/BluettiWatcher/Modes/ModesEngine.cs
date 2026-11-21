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
    /// <summary>The event of a switch the user made himself: kept in the history, no notification.</summary>
    public const string ModeChosenEvent = "mode-chosen";

    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>The engine's own lifetime: a change runs on it, so only the service's shutdown stops one half-way.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ModeReconciler _reconciler = new(registry, journal, time, onEvent);
    private Task _pending = Task.CompletedTask;
    private volatile string? _switching;
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
    /// <summary>Start problems (settings or journal), shown by the API until the next start; then the active mode's entries the broker policy does not allow.</summary>
    public IReadOnlyList<string> Problems => [.. _problems, .. _policyProblems];

    /// <summary>
    /// The admin broker's policy (the user can read it, only an elevated step writes it); null = none or unreadable, and
    /// then no entry is warned about (the broker's health says that).
    /// </summary>
    public Func<Broker.BrokerPolicy?> ReadPolicy { get; init; } = () => null;

    /// <summary>The exe a warning's <c>--broker-allow</c> command runs.</summary>
    public string ExePath { get; init; } = Environment.ProcessPath ?? "BluettiWatcher.exe";

    private IReadOnlyList<string> _policyProblems = [];

    /// <summary>The entries of mode <paramref name="modeId"/> the broker policy does not allow; none when there is no policy or no such mode.</summary>
    public IReadOnlyList<PolicyWarning> PolicyWarnings(string modeId) =>
        _settings.Modes.FirstOrDefault(m => m.Id == modeId) is { } mode && ReadPolicy() is { } policy
            ? PolicyWarning.For(mode, registry, policy, ExePath)
            : [];

    /// <summary>The active mode's policy gaps, re-read each pass; a new set is logged and raised once.</summary>
    private void CheckPolicy(Mode mode)
    {
        IReadOnlyList<string> now = [.. PolicyWarnings(mode.Id).Select(w => w.Text(mode.Id))];
        if (now.SequenceEqual(_policyProblems, StringComparer.Ordinal)) return;
        _policyProblems = now;
        foreach (var text in now)
        {
            log($"modes: {text}");
            onEvent?.Invoke("action-failed", text);
        }
    }
    public bool AutoHibernate => _settings.AutoHibernate;
    /// <summary>The mode being entered while its actions run; null when no transition is under way.</summary>
    public string? Switching => _switching;
    /// <summary>The last change asked for (API or tray): done or not, it is not cut short by the caller that asked.</summary>
    public Task Pending => Volatile.Read(ref _pending);
    /// <summary>A change is queued or running.</summary>
    public bool Busy => !Pending.IsCompleted;

    /// <summary>The service stops: a transition under way stops at its next action.</summary>
    public void Shutdown()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }

    public void Dispose()
    {
        Shutdown();
        _lifetime.Dispose();
        _gate.Dispose();
    }

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
        DropRetiredActions();
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

    /// <summary>A saved action of a type this build removed is taken out and the file saved without it, said in one log line.</summary>
    private void DropRetiredActions()
    {
        var (kept, dropped) = RetiredActions.Drop(_settings);
        if (dropped == 0) return;
        _settings = kept;
        string types = string.Join(", ", RetiredActions.TypeIds.Order(StringComparer.Ordinal));
        SaveQuietly();
        log($"modes: {dropped.ToString(CultureInfo.InvariantCulture)} action(s) of a removed type dropped ({types})");
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

    // byUser: a change the user made (a pick on the page, the tray menu, the CLI, an edit): its switch is stored as
    // mode-chosen, which shows no notification; the switches the program makes on its own stay mode-changed.
    private async Task ReconcileAsync(CancellationToken ct, bool byUser = false)
    {
        var input = _input ?? new SelectorInput(null, null, null, null, time.GetLocalNow());
        var d = ModeSelector.Decide(_settings, input);
        if (d.Selection != _settings.Selection)
        {
            _settings = _settings with { Selection = d.Selection };
            SaveQuietly();
        }
        var mode = _settings.Modes.First(m => m.Id == d.ModeId);
        bool changed = !string.Equals(_reconciler.ActiveModeId, mode.Id, StringComparison.Ordinal);
        if (changed) log($"modes: {mode.Id} ({d.Reason})");
        _reason = d.Reason;
        CheckPolicy(mode);
        bool entering = !string.Equals(_reconciler.Snapshot.ModeId, mode.Id, StringComparison.Ordinal);
        if (entering) _switching = mode.Id;
        try { await _reconciler.ReconcileAsync(mode, ct); }
        finally { if (entering) _switching = null; }
        // Raised once the actions ran: its tray notice is a banner of a few seconds, which a refresh-rate change (screens
        // dark while they resync) or a plan's display-off timeout would otherwise swallow.
        if (changed) onEvent?.Invoke(byUser ? ModeChosenEvent : "mode-changed", $"{mode.Id} ({d.Reason})");
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
    /// <remarks>
    /// The change runs on the engine's lifetime, never on <paramref name="ct"/>: a transition stopped half-way leaves the
    /// machine in the new mode's state while the view still names the old one. <paramref name="ct"/> only ends the
    /// caller's wait (an HTTP request's limit, a client gone). It starts on the thread pool, never on the caller's thread:
    /// a module's validation and actions may block their thread (ChangeDisplaySettingsEx while the screens resync, a
    /// service query), and run inline they would hold the caller past its bounded wait (a dropped request).
    /// </remarks>
    private Task<(int Status, string Error)> ChangeAsync(Func<ModesSettings, (ModesSettings Next, string Error)> change, CancellationToken ct)
    {
        var run = Task.Run(() => RunChangeAsync(change, _lifetime.Token), CancellationToken.None);
        Volatile.Write(ref _pending, run);
        return run.WaitAsync(ct);
    }

    private async Task<(int Status, string Error)> RunChangeAsync(Func<ModesSettings, (ModesSettings Next, string Error)> change, CancellationToken ct)
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
            await ReconcileAsync(ct, byUser: true);
            return (0, "");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The caller may have stopped waiting (202): the failure is said here, not lost with the task.
            log($"modes: change failed: {e.GetType().Name}: {e.Message}");
            throw;
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<JournalRecord> Journal(long since) => journal.Since(since);
}
