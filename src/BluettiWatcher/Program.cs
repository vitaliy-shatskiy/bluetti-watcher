using System.Net;
using System.Net.Sockets;
using BluettiWatcher;
using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Io.Ble;
using BluettiWatcher.Storage;
using BluettiWatcher.Tray;

// Command line: see Cli.Usage (--help). The service prints nothing (it logs to service.log); every other command prints
// into the terminal it was started from.
// Exit: 0 stopped (or token saved, help, version) · 1 the login failed · 2 could not run (bad arguments, another instance past lockWaitSec, the port taken) · 3 Windows' SQLite missing or older than 3.24.0.
using var console = ConsoleOut.Attach();
var outcome = await Cli.ExecuteAsync(args, Console.Out, Console.Error, async cfg =>
{
    using var loginHttp = new SocketsHttpHandler();
    var login = new CloudLogin(cfg, loginHttp, TimeProvider.System, Console.WriteLine, ConsoleOut.OpenBrowser);
    return await login.RunAsync(CancellationToken.None);
});
if (outcome.Exit is { } exitCode) return exitCode;
var config = outcome.Config!;
Directory.CreateDirectory(config.DataDir);
using var fileLog = new FileLog(Path.Combine(config.DataDir, "service.log"));
void Log(string text) => fileLog.Write(text);

// A task restart can start this instance while the old one still exits: wait for its lock, bounded (0..600 s).
int lockWait = Math.Clamp(config.LockWaitSec, 0, 600);
using var single = InstanceLock.Take(config.Port, TimeSpan.FromSeconds(lockWait), Log);
if (single is null) { Log($"DID NOT RUN - another instance still holds this port's lock after {lockWait} s"); return 2; }

// The flag's mutex is Local\: the service must share the user's interactive session, never session 0.
if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0) { Log("DID NOT RUN - session 0: the service runs in the user's interactive session (install.ps1)"); return 2; }

// Without a station address (or a station neither Bluetooth nor the cloud reads) nothing is polled; the page and
// /api/health still answer and name the problem.
if (config.ExtraReadersProblem is { } badReaders) Log($"CONFIG - {badReaders}");
if (config.IgnoredKeysNote is { } ignored) Log($"CONFIG - {ignored}");
if (config.PollProblem is { } noPoll) Log($"NOT POLLING - {noPoll}");
else if (config.BluetoothProblem is { } noBluetooth) Log($"CLOUD ONLY - {noBluetooth}");

// SQLite is Windows' own winsqlite3.dll: one too old for the store's upserts is refused here, named in service.log.
if (SqliteSetup.StartGate(Log) is { } sqliteExit) return sqliteExit;

using var store = new StationStore(Path.Combine(config.DataDir, "station.db"));
var runner = new ProcessRunner();
var time = TimeProvider.System;
if (config.MutexWarning is { } mutexWarning)
{
    Log($"WARNING - {mutexWarning}");
    store.AddEvent(time.GetLocalNow(), "config-warning", mutexWarning);
}
// An eco end task registered for the console exe would open a console window when it fires: it starts the windowless twin.
using (var endTaskSwitch = new BluettiWatcher.PowerMode.PowerModeSwitch([.. BluettiWatcher.PowerMode.PowerModeSwitch.ProcessEnvironment(), .. config.PowerModeEnvironment], TextWriter.Null))
{
    if (!config.DryRun && endTaskSwitch.RepointEndTask() is { } repointed) Log(repointed);
}
// The wake of a PC hibernated in a cut: armed only with wakeEveryMinutes > 0; turned off at the first reading on mains whatever it says.
var wake = new WakeKeeper(config, new ScheduledWakeTask(config, runner, Log), store, Log);
// The page's reading-source choice, saved in the data directory, wins over the config key (design.md §4 Reading source).
config.PageSource = ReadingSourceFile.Read(config.DataDir, Log);
// Under the auto source: Bluetooth first; when it misses, the BLUETTI cloud (design.md §4 Cloud fallback), on whenever its token file exists (read at each poll, so a token made while running counts) unless cloudEnabled is false.
// The source is read at each poll: bluetooth never calls the cloud, cloud never opens Bluetooth.
using var cloudHttp = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
// The cloud's own 5-minute history (two read-only statistics calls) fills the database's gaps in its own task (design.md §4a),
// and its newest point stands in for a device list with no live state (§4 AC2b).
// Its own pool: it never follows a redirect; the live reader's handler keeps its behaviour.
using var historyHttp = CloudHistoryReader.NewHandler();
using var historyReader = new CloudHistoryReader(historyHttp);
// The serial an earlier history run saved for this station type: the history session then skips the device list, which the cloud has left unanswered.
using var cloud = BluettiCloudReader.ForService(config, cloudHttp, time, Log, store, historyReader);
using var backfill = new CloudBackfill(config, historyReader, cloud.HistorySessionAsync, store, time, Log, cloud.ForgetSerial);
// Bluetooth: the built-in WinRT reader, IoT V1 and V2 models, read only (Io/Ble).
var bluetooth = new NativeBleReader(config, new WinRtBleConnector(), Log);
// Power modes v2 (modes.engine = v2): settings and journal in the data directory; the journal is replayed before any write.
if (!config.Modes.V2On && !string.Equals(config.Modes.Engine, BluettiWatcher.Modes.ModesConfig.Legacy, StringComparison.OrdinalIgnoreCase))
    Log($"CONFIG - modes.engine \"{config.Modes.Engine}\" is not legacy or v2: legacy is used");
using var modes = config.Modes.V2On ? new BluettiWatcher.Modes.ModesEngine(config, new BluettiWatcher.Modes.ModuleRegistry(BluettiWatcher.Modes.ModuleCatalog.Production(config, runner, time, Log)),
    new BluettiWatcher.Modes.ModesStore(config.DataDir), new BluettiWatcher.Modes.ModeJournal(Path.Combine(config.DataDir, BluettiWatcher.Modes.ModeJournal.FileName)),
    new BluettiWatcher.Modes.SwitchFlagWordWriter(config, Log), time, Log, (kind, detail) => store.AddEvent(time.GetLocalNow(), kind, detail))
    {
        // Read, never written: an entry it lacks is said on the page with the elevated --broker-allow command.
        ReadPolicy = () => BluettiWatcher.Modes.Broker.BrokerPolicy.Read(BluettiWatcher.Modes.Broker.BrokerPaths.Default.Policy),
    } : null;
modes?.Start(BluettiWatcher.Io.BootClock.BootAt(time, BluettiWatcher.Io.BootClock.SinceBoot));
using var service = new StationService(config, new FallbackReader(bluetooth, cloud, () => config.CloudOn, r => StationDecision.Check(r, new StationState(), time.GetUtcNow(), config), Log, () => config.CloudOnly), new PowerModeAdapter(config, runner, Log, new BluettiWatcher.PowerMode.InProcessPowerMode()),
    config.DryRun ? new DryRunHibernator(Log) : new ShutdownHibernator(runner, Log),
    new FlagFileReader(config.FlagFile, Log, config.PowerModeMutex), store, time, Log, wake, new StateActions(config, runner, store, time, Log)) { Modes = modes };
service.Compact();

var started = time.GetLocalNow();
// The outage schedule: its own task and cadence, never on the poll path; off (no source, no request) unless dtekWatcher.enabled.
// The guard is the module's whole boundary: no call into it ever throws into this loop or into /api/now.
using var dtek = DtekWatcherGuard.Create(config, store, time, Log);
// The Auto rules' cutWithin reads the schedule's next planned outage (the guard never throws; off = no outage known).
service.NextCut = () => dtek.View(service.State.Cut, null).NextStart;
using var stop = new CancellationTokenSource();
// A mode change runs on the engine's lifetime, never a request's: the service's stop is what ends one half-way.
using var stopModes = stop.Token.Register(() => modes?.Shutdown());
// The page's sign-in: the --cloud-login flow inside the service, its catcher on a port the OS assigns; the token is written through the reader's lock.
using var signInHttp = new SocketsHttpHandler();
using var account = new CloudAccount(config, cloud, signInHttp, time, Log, stop.Token);
// Which notifications show: notifications.json in the data directory, edited through /api/settings/notifications.
var notificationSettings = new NotificationSettingsStore(Path.Combine(config.DataDir, "notifications.json"), Log);
BluettiWatcher.Modes.Broker.BrokerStatus BrokerState() => BluettiWatcher.Modes.Broker.BrokerVersion.Check(typeof(StationService).Assembly.GetName().Version ?? new Version(0, 0),
    BluettiWatcher.Modes.Broker.BrokerVersion.Read(BluettiWatcher.Modes.Broker.BrokerPaths.Default));
// The catalogue's discovery takes seconds: built at start, so the page's first ask is answered at once.
var modesApi = modes is null ? null : new ModesApi(config, modes);
modesApi?.WarmCatalogue();
var api = new StationApi(config, service, store, time, started, dtek, Log, () => bluetooth.StationEncrypted, () => bluetooth.BluetoothStalled,
        () =>
        {
            using var planSwitch = new BluettiWatcher.PowerMode.PowerModeSwitch([.. BluettiWatcher.PowerMode.PowerModeSwitch.ProcessEnvironment(), .. config.PowerModeEnvironment], TextWriter.Null);
            return planSwitch.ActivePlan();
        }, account, bluetooth.StationBlocks, modesApi, notificationSettings, BrokerState);
// The notification-area icon and the notifications: a thread of this process, only in an interactive session (README, Tray).
using var trayHost = config.Tray && Environment.UserInteractive
    ? TrayHost.Start(modes is null ? new ManualTrayModes(service) : new ModesTrayModes(modes), new Uri($"http://127.0.0.1:{config.Port}/"), time.LocalTimeZone, Log)
    : null;
var tray = trayHost is null ? null : new TrayController(trayHost, notificationSettings);
if (tray is not null) store.EventAdded = tray.OnEvent;
api.SendTestNotice = tray is null ? null : tray.SendTest;
void ShowTray()
{
    if (tray is null) return;
    var at = time.GetLocalNow();
    var last = service.LastAccepted;
    tray.Update(TraySnapshot.From(last, service.LastAcceptedAt, service.State, service.LastFlag, api.Runtime(last, at).HoursLeft, at, config.MaxGapSec));
    tray.ReportAlarms(StationAlarm.ListOf(service.StationAlarmsNow));
}
ShowTray();
// A broker older than this service (its copy is refreshed only by the elevated install step) is said when it still is
// after a grace (an upgrade refreshes it seconds after this start), once per version: event, log, tray notice.
var brokerNotice = modes is null ? Task.CompletedTask
    : new BluettiWatcher.Modes.Broker.BrokerStaleNotice(BrokerState, (typeof(StationService).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3),
        Path.Combine(config.DataDir, "broker-stale-noticed"), (d, c) => Task.Delay(d, time, c),
        (kind, detail) => store.AddEvent(time.GetLocalNow(), kind, detail), Log).RunAsync(stop.Token);
using var http = new MiniHttpServer(IPAddress.Loopback, config.Port, api.HandleAsync, Log);
try { http.Start(); }
catch (SocketException e) { Log($"DID NOT RUN - 127.0.0.1:{config.Port} is taken ({e.SocketErrorCode})"); return 2; }

Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
Log($"Bluetti Watcher started: http://127.0.0.1:{config.Port}/ · source {ReadingSourceFile.Name(config.Source)}{(config.PageSource is null ? "" : " (set on the page)")} · reader {(config.CloudOnly ? "cloud only" : "Bluetooth")} · data {config.DataDir}{(config.CloudOn ? " · cloud fallback" : "")}{(config.DryRun ? " · DRY RUN" : "")}");

var serving = http.RunAsync(stop.Token);
var scheduleReads = dtek.RunAsync(stop.Token);
var historyRuns = backfill.RunAsync(stop.Token);
// The token is refreshed once a day while the source allows the cloud, even when Bluetooth answers (design.md §4 AC11).
var keepAlive = cloud.RunKeepAliveAsync(stop.Token);
// A page choice applies when it is saved, never after a station read that is still running.
var presses = service.RunPressesAsync(stop.Token);
long lastCompact = time.GetTimestamp();
var memory = new ProcessMemory(config, new Win32MemoryCalls(), Log);
try
{
    // The loop's own timing is monotonic: a wall-clock step never stalls polling. Readings keep their wall time.
    var schedule = new PollSchedule(time);
    while (!stop.IsCancellationRequested)
    {
        // An Auto pressed after a pinned Max or Eco asks for its reading now: the battery rules decide without the minute's wait.
        if (schedule.Due(service.TakePollRequest()))
        {
            int delay;
            if (config.PollProblem is not null) delay = config.PollSec;   // no station to poll: the loop only idles
            else try { delay = await service.PollAsync(stop.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One failed poll (a locked database, a process that would not start) never stops the service.
                Log($"poll failed: {e.GetType().Name}: {e.Message}");
                delay = config.PollSec;
            }
            schedule.Next(delay);
            dtek.AfterPoll(service.State);   // the finished cut goes to the queue search; nothing comes back
            backfill.AfterPoll(service.LastAcceptedAt);   // a gap that just ended wakes the back-fill
            // Between polls the process keeps only what it holds: the garbage of the poll and of the page's requests goes back
            // to Windows (a workstation gen0 budget is sized from the CPU cache, tens of MB, and the exe takes no DOTNET_GC* variable).
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            // Then the native heaps' free memory, and the working set unless trimWorkingSet is false (Io/ProcessMemory.cs).
            memory.AfterPoll();
        }
        // A page choice saved by POST /api/mode runs here, with the power-mode timeout, never the request's.
        try { await service.ApplyPendingAsync(stop.Token); }
        catch (Exception e) when (e is not OperationCanceledException) { Log($"mode change failed: {e.GetType().Name}: {e.Message}"); }
        // After each poll and each page or tray choice: the icon is redrawn only when what it shows changed.
        try { ShowTray(); }
        catch (Exception e) when (e is not OperationCanceledException) { Log($"tray update failed: {e.GetType().Name}: {e.Message}"); }
        if (time.GetElapsedTime(lastCompact).TotalHours >= 1) { service.Compact(); lastCompact = time.GetTimestamp(); }
        await service.WaitForWorkAsync(schedule.Wait, stop.Token);
    }
}
catch (OperationCanceledException)
{
    Log("stopping");
}
await serving;
await scheduleReads;
await historyRuns;
await keepAlive;
try { await presses; }
catch (OperationCanceledException) { /* the press loop ends with the stop */ }
try { await brokerNotice; }
catch (OperationCanceledException) { /* a stop within the grace ends the wait */ }
Log("stopped");
return 0;
