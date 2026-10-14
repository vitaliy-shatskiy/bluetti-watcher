<#
bench.ps1 - footprint of BluettiWatcher.exe against its footprint budgets: startup to the first HTTP 200, memory, threads,
handles, idle CPU and wakeups, API latency on a 30-day database, response sizes (plain and compressed, cache
headers), exe and zip size. It starts the exe in --dry-run on a test port with scratch data directories (never the live
service's port, data, flag file or mutex), measures, kills it and deletes the scratch directory.

  pwsh -NoProfile -File tools/bench.ps1 [-Exe <BluettiWatcher.exe>] [-Port 18965] [-IdleSec 60] [-NoNetwork] [-JsonOut <file>]

No -Exe: tools/publish.ps1 publishes into the scratch directory first.
Runs:
  startup  -StartRuns cold starts on empty data directories; the median of start -> first 200 on /api/health.
  idle     no station configured: the loop only idles; memory, threads, handles, CPU, context switches over -IdleSec.
  page     a database seeded with 30 days of minutes and a year of 5-minute aggregates (no product code, no Python: the SQLite
           library the release ships, driven through inline C#): the page's load (/api/now and the 26-hour /api/history
           every 2 s), the HTTP sizes and latencies, and the memory after it. Unless -NoNetwork the DTEK module is on (the
           public dataset over HTTPS), so the TLS stack is loaded as on a live install.
NOT MEASURED: CPU per poll and disk bytes per day (the MaxPollCpuMs and MaxDiskMBPerDay budgets). Since 1.1.0 the service
reads over Bluetooth or the BLUETTI cloud, and the cloud address is fixed in the exe, so there is no station to stand in
for it without a product change, and polling a made-up MAC would drive this machine's radio. The report names both as
"not measured", and the closing line says so: those budgets are neither met nor missed here (see the poll test suite and
a live service's own counters).Timing numbers (startup, CPU, latency) are marked provisional while a dotnet test run is busy on this machine; memory and
sizes are not affected by it.

Exit: 0 every MEASURED budget met (the unmeasured ones are named; not a pass for them) · 1 a measured budget missed (each named) ·
2 could not run.
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [int] $Port = 18965,
    [int] $IdleSec = 60,
    [int] $PageWindowSec = 30,
    [int] $StartRuns = 5,
    [int] $LatencyRuns = 60,
    [switch] $NoNetwork,
    # NAME=value pairs put into the exe's environment (GC settings such as DOTNET_GCgen0size, for experiments).
    [string[]] $EnvVars = @(),
    [string] $JsonOut,
    # A script run with the live-like instance's process id before it is stopped (a memory map, a dump).
    [string] $SnapshotScript,
    # Budgets.
    [double] $MaxPrivateMB = 15,
    # NativeAOT keeps hydrated data (IlcDehydrate=false): a bigger file for less private memory. Since 1.2.0 the exe is
    # the whole release (SQLite is Windows' winsqlite3.dll): 10.7 MB measured.
    [double] $MaxExeMB = 12,
    [double] $MaxStartupMs = 100,
    [double] $MaxIdleWakeupsPerMin = 30
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Fail2([string] $why) { Write-Output "bench: COULD NOT RUN: $why"; exit 2 }

if ($Port -eq 18940) { Fail2 'port 18940 is the live service' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('bw-bench-' + [guid]::NewGuid().ToString('n').Substring(0, 8))
$null = New-Item -ItemType Directory -Force -Path $work
$procs = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

try {
    # --- the exe -------------------------------------------------------------------------------------------------------
    if (-not $Exe) {
        $pubOut = Join-Path $work 'publish'
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'publish.ps1') -Out $pubOut | Write-Output
        if ($LASTEXITCODE -ne 0) { Fail2 "publish exit $LASTEXITCODE" }
        $Exe = Join-Path $pubOut 'BluettiWatcher.exe'
    }
    $Exe = [IO.Path]::GetFullPath($Exe)
    if (-not (Test-Path -LiteralPath $Exe)) { Fail2 "no exe at $Exe" }
    $exeBytes = (Get-Item -LiteralPath $Exe).Length
    # The binaries the release ships: the exe and every native library next to it.
    $bins = @(Get-ChildItem -LiteralPath (Split-Path $Exe) -File | Where-Object { $_.Extension -in '.exe', '.dll' })
    $binBytes = ($bins | Measure-Object Length -Sum).Sum
    $zip = Join-Path $work 'bin.zip'
    Compress-Archive -LiteralPath $bins.FullName -DestinationPath $zip -CompressionLevel Optimal
    $zipBytes = (Get-Item -LiteralPath $zip).Length

    # --- quiet machine? -------------------------------------------------------------------------------------------------
    $busy = @(Get-CimInstance Win32_Process -Filter "Name='testhost.exe' OR Name='dotnet.exe' OR Name='vstest.console.exe'" |
            Where-Object { $_.Name -ne 'dotnet.exe' -or $_.CommandLine -match '\btest\b|vstest|testhost' })
    $provisional = $busy.Count -gt 0

    # --- the seed database: no product code, no Python -----------------------------------------------------------------------
    # The SQLite library the exe uses: Windows' own winsqlite3.dll since 1.2.0 (the release ships no SQLite library);
    # a build before 1.2.0 has e_sqlite3.dll beside the exe, and that one is used. Driven through a few inline C# P/Invokes. Same layout as StationStore makes for a new file (1 KB pages, WAL), so the
    # exe's CREATE IF NOT EXISTS keeps it.
    $sqliteDll = Join-Path (Split-Path $Exe) 'e_sqlite3.dll'
    if (-not (Test-Path -LiteralPath $sqliteDll)) { $sqliteDll = Join-Path ([Environment]::SystemDirectory) 'winsqlite3.dll' }
    if (-not (Test-Path -LiteralPath $sqliteDll)) { Fail2 'no SQLite library to seed the database with (e_sqlite3.dll beside the exe, winsqlite3.dll in System32)' }
    if (-not ('BenchSqlite' -as [type])) {
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BenchSqlite {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr cb, IntPtr arg, out IntPtr err);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Close(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrMsg(IntPtr db);
    public static void Run(string library, string file, string sql) {
        IntPtr lib = NativeLibrary.Load(library);
        var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(lib, "sqlite3_open"));
        var exec = Marshal.GetDelegateForFunctionPointer<Exec>(NativeLibrary.GetExport(lib, "sqlite3_exec"));
        var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(lib, "sqlite3_close"));
        var errmsg = Marshal.GetDelegateForFunctionPointer<ErrMsg>(NativeLibrary.GetExport(lib, "sqlite3_errmsg"));
        IntPtr db;
        int rc = open(file, out db);
        try {
            if (rc != 0) throw new InvalidOperationException("sqlite3_open " + rc);
            IntPtr err;
            rc = exec(db, sql, IntPtr.Zero, IntPtr.Zero, out err);
            if (rc != 0) throw new InvalidOperationException("sqlite3_exec " + rc + ": " + Marshal.PtrToStringUTF8(errmsg(db)));
        } finally { close(db); }
    }
}
'@
    }
    function New-SeedSql {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 60) - 600
        $aggStart = [long][math]::Floor(($now - 30 * 86400) / 300) * 300
        @"
PRAGMA page_size=1024;
PRAGMA journal_mode=WAL;
BEGIN;
CREATE TABLE IF NOT EXISTS reading(at INTEGER NOT NULL, status INTEGER NOT NULL, soc REAL, in_ac REAL, in_dc REAL,
  out_ac REAL, out_dc REAL, volt REAL, hz REAL, mode TEXT, reason TEXT);
CREATE INDEX IF NOT EXISTS reading_at ON reading(at);
CREATE TABLE IF NOT EXISTS agg5(at INTEGER PRIMARY KEY, n INTEGER NOT NULL, soc REAL, soc_min REAL, soc_max REAL,
  in_ac REAL, in_dc REAL, out_ac REAL, out_dc REAL, volt REAL, hz REAL, mains REAL, missed INTEGER NOT NULL, rejected INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS event(at INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS event_at ON event(at);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < $(30 * 1440 - 1))
  INSERT INTO reading SELECT $now - i * 60, 0, 60 + (i % 600) / 20.0, 300.0, 0.0, 120.0, 5.0, 230.1, 50.0, 'INVERTER_OUTPUT', NULL FROM n;
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < $(365 * 288 - 1))
  INSERT INTO agg5 SELECT $aggStart - i * 300, 5, 70.0, 69.0, 71.0, 300.0, 0.0, 120.0, 5.0, 230.1, 50.0, 1.0, 0, 0 FROM n;
WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 499)
  INSERT INTO event SELECT $now - i * 3600, 'mode', 'auto' FROM n;
COMMIT;
"@
    }

    # --- helpers --------------------------------------------------------------------------------------------------------
    $handler = [System.Net.Http.SocketsHttpHandler]::new()
    $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
    $handler.PooledConnectionLifetime = [TimeSpan]::Zero
    $http = [System.Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(10)
    $base = "http://127.0.0.1:$Port"

    function Get-Raw([string] $path, [string] $acceptEncoding, [string] $ifNoneMatch) {
        $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$base$path")
        if ($acceptEncoding) { $null = $req.Headers.TryAddWithoutValidation('Accept-Encoding', $acceptEncoding) }
        if ($ifNoneMatch) { $null = $req.Headers.TryAddWithoutValidation('If-None-Match', $ifNoneMatch) }
        $resp = $http.SendAsync($req).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $enc = ($resp.Content.Headers.ContentEncoding | Select-Object -First 1)
        $etag = if ($resp.Headers.ETag) { $resp.Headers.ETag.ToString() } else { $null }
        $cc = if ($resp.Headers.CacheControl) { $resp.Headers.CacheControl.ToString() } else { '' }
        $resp.Dispose()
        [pscustomobject]@{ Status = [int]$resp.StatusCode; Bytes = $body.Length; Encoding = $enc; ETag = $etag; CacheControl = $cc }
    }

    function New-DataDir([string] $name, [hashtable] $extra) {
        $dir = Join-Path $work $name
        $null = New-Item -ItemType Directory -Force -Path $dir
        $cfg = [ordered]@{
            port         = $Port
            cloudEnabled = $false
            lockWaitSec  = 0
            # Never the live names: a scratch flag file and its own mutex.
            powerMode    = [ordered]@{ flagPath = (Join-Path $dir 'flag.json'); mutex = "Local\BluettiBench-$([guid]::NewGuid().ToString('n'))" }
        }
        foreach ($k in $extra.Keys) { $cfg[$k] = $extra[$k] }
        [IO.File]::WriteAllText((Join-Path $dir 'config.json'), ($cfg | ConvertTo-Json -Depth 5))
        $dir
    }

    function Test-Listening {
        foreach ($l in [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()) {
            if ($l.Port -eq $Port) { return $true }
        }
        $false
    }

    function Start-Bw([string] $dir) {
        if (Test-Listening) { throw "port $Port is taken before the start" }
        $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
        foreach ($a in @('--data-dir', $dir, '--port', "$Port", '--dry-run')) { $psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        foreach ($e in $EnvVars) { $k, $v = $e -split '=', 2; $psi.Environment[$k] = $v }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $p = [System.Diagnostics.Process]::Start($psi)
        $procs.Add($p)
        $ms = $null
        # A connect to a port nobody listens on takes ~0.5 s on Windows (SYN retries): wait for the listener first.
        while ($sw.ElapsedMilliseconds -lt 15000) {
            if ($p.HasExited) { throw "the exe exited with $($p.ExitCode) before answering; log: $(Get-Content -LiteralPath (Join-Path $dir 'service.log') -Tail 3 -ErrorAction SilentlyContinue)" }
            if (Test-Listening) {
                $r = $http.GetAsync("$base/api/health").GetAwaiter().GetResult()
                $code = [int]$r.StatusCode; $r.Dispose()
                if ($code -eq 200) { $ms = $sw.Elapsed.TotalMilliseconds; break }
            }
        }
        if ($null -eq $ms) { throw 'no HTTP 200 within 15 s' }
        [pscustomobject]@{ Process = $p; StartupMs = $ms }
    }

    function Stop-Bw($p) {
        if (-not $p.HasExited) { $p.Kill(); $null = $p.WaitForExit(5000) }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ((Test-Listening) -and $sw.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 20 }
    }

    function Get-ThreadSwitches([int] $id) {
        # Raw cumulative context switches per thread id of the process.
        $h = @{}
        foreach ($t in Get-CimInstance Win32_PerfRawData_PerfProc_Thread -Filter "IDProcess=$id") { $h[[int]$t.IDThread] = [long]$t.ContextSwitchesPersec }
        $h
    }

    function Get-SwitchDelta([hashtable] $a, [hashtable] $b) {
        # Threads alive at the end: their switches since the start (all of them for a thread born in the window).
        $sum = 0L
        foreach ($k in $b.Keys) { $sum += $b[$k] - ($a.ContainsKey($k) ? $a[$k] : 0) }
        $sum
    }

    function Get-Snapshot($p) {
        $p.Refresh()
        $pws = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)").WorkingSetPrivate
        $io = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)"
        [pscustomobject]@{
            PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
            PrivateWsMB  = [math]::Round($pws / 1MB, 2)
            WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 2)
            Threads      = $p.Threads.Count
            Handles      = $p.HandleCount
            CpuMs        = $p.TotalProcessorTime.TotalMilliseconds
            WriteBytes   = [long]$io.WriteTransferCount
        }
    }

    function Get-DirBytes([string] $dir) { [long](Get-ChildItem -LiteralPath $dir -File -Force | Measure-Object Length -Sum).Sum }

    function Get-Percentile([double[]] $v, [double] $q) {
        $s = @($v | Sort-Object)
        $s[[math]::Min($s.Count - 1, [math]::Floor($q * ($s.Count - 1) + 0.5))]
    }

    # --- 1. startup ------------------------------------------------------------------------------------------------------
    $startups = @()
    for ($i = 0; $i -lt $StartRuns; $i++) {
        $s = Start-Bw (New-DataDir "start$i" @{})
        $startups += $s.StartupMs
        Stop-Bw $s.Process
    }
    $startupMs = Get-Percentile $startups 0.5

    # --- 2. idle: no station configured ----------------------------------------------------------------------------------
    $idleDir = New-DataDir 'idle' @{}
    $p = (Start-Bw $idleDir).Process
    Start-Sleep -Seconds 5
    $a = Get-Snapshot $p; $csA = Get-ThreadSwitches $p.Id
    Start-Sleep -Seconds $IdleSec
    $b = Get-Snapshot $p; $csB = Get-ThreadSwitches $p.Id
    $idle = [pscustomobject]@{
        PrivateMB = $b.PrivateMB; PrivateWsMB = $b.PrivateWsMB; WorkingSetMB = $b.WorkingSetMB; Threads = $b.Threads; Handles = $b.Handles
        CpuMs = [math]::Round($b.CpuMs - $a.CpuMs, 1)
        WakeupsPerMin = [math]::Round((Get-SwitchDelta $csA $csB) * 60.0 / $IdleSec, 1)
    }
    Stop-Bw $p

    # --- 3. the page's load on a seeded database (no polls: see the header) -------------------------------------------------
    # No `mac`: the loop only idles (the Bluetooth reader is never started, so no radio is touched on this machine), and
    # every pollSec it still runs the GC between polls, as a live service does.
    $extra = @{ pollSec = 2 }
    if (-not $NoNetwork) { $extra.dtekWatcher = [ordered]@{ enabled = $true; group = 'GPV1.1' } }
    $pageDir = New-DataDir 'page' $extra
    $dbFile = Join-Path $pageDir 'station.db'
    [BenchSqlite]::Run($sqliteDll, $dbFile, (New-SeedSql))
    $seedBytes = (Get-Item -LiteralPath $dbFile).Length
    $s = Start-Bw $pageDir
    $p = $s.Process
    Start-Sleep -Seconds 6   # past the module's first fetch

    # The page: /api/now and the 26-hour history every 2 s, as an open tab does every minute.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $histPath = { $to = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); "/api/history?from=$($to - 26 * 3600000)&to=$to&step=60" }
    while ($sw.Elapsed.TotalSeconds -lt $PageWindowSec) {
        $null = Get-Raw '/api/now' 'br, gzip' $null
        $null = Get-Raw (& $histPath) 'br, gzip' $null
        Start-Sleep -Seconds 2
    }
    $routes = '/', '/api/now', '/api/health', (& $histPath), '/api/events'
    $sizes = foreach ($r in $routes) {
        $plain = Get-Raw $r $null $null
        $comp = Get-Raw $r 'br, gzip' $null
        $revalidate = if ($comp.ETag) { (Get-Raw $r 'br, gzip' $comp.ETag).Status } else { $null }
        [pscustomobject]@{ Route = ($r -replace '\?.*', '?26h'); Status = $plain.Status; Plain = $plain.Bytes; Wire = $comp.Bytes; Encoding = $comp.Encoding
            CacheControl = $comp.CacheControl; ETag = [bool]$comp.ETag; Revalidate = $revalidate }
    }
    $lat = [ordered]@{}
    foreach ($r in '/api/now', '/', (& $histPath)) {
        $t = for ($i = 0; $i -lt $LatencyRuns; $i++) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew(); $null = Get-Raw $r 'br, gzip' $null; $sw.Elapsed.TotalMilliseconds
        }
        $lat[($r -replace '\?.*', '?26h')] = [pscustomobject]@{ P50 = [math]::Round((Get-Percentile $t 0.5), 2); P95 = [math]::Round((Get-Percentile $t 0.95), 2) }
    }
    Start-Sleep -Seconds 10
    $live = Get-Snapshot $p
    if ($SnapshotScript) { & pwsh -NoProfile -File $SnapshotScript -ProcessId $p.Id | Write-Output }
    Stop-Bw $p
}
catch {
    foreach ($q in $procs) { if (-not $q.HasExited) { $q.Kill() } }
    Fail2 "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}
finally {
    foreach ($q in $procs) { if (-not $q.HasExited) { $q.Kill(); $null = $q.WaitForExit(5000) } }
}

# --- report ------------------------------------------------------------------------------------------------------------
$prov = if ($provisional) { ' (timings provisional: a dotnet test run is busy)' } else { '' }
Write-Output "bench: $Exe$prov$(if ($EnvVars) { ' env: ' + ($EnvVars -join ' ') })"
Write-Output ('exe {0:N2} MB; binaries {1:N2} MB ({2}), zipped {3:N2} MB' -f ($exeBytes / 1MB), ($binBytes / 1MB), (($bins.Name) -join ', '), ($zipBytes / 1MB))
Write-Output ('startup to first 200: median {0:N1} ms (runs: {1})' -f $startupMs, (($startups | ForEach-Object { '{0:N0}' -f $_ }) -join ', '))
Write-Output ('idle, no station, {0} s: private {1} MB, private WS {2} MB, WS {3} MB, threads {4}, handles {5}, CPU {6} ms, context switches {7}/min' -f `
        $IdleSec, $idle.PrivateMB, $idle.PrivateWsMB, $idle.WorkingSetMB, $idle.Threads, $idle.Handles, $idle.CpuMs, $idle.WakeupsPerMin)
Write-Output ('page phase: database seeded with {0:N1} MB (30 days of minutes, a year of 5-minute aggregates), TLS module: {1}' -f ($seedBytes / 1MB), (-not $NoNetwork))
Write-Output 'poll CPU per poll: NOT MEASURED (no station to poll without a product change); disk MB/day: NOT MEASURED (the same)'
Write-Output ('live-like after page load: private {0} MB, private WS {1} MB, WS {2} MB, threads {3}, handles {4}' -f `
        $live.PrivateMB, $live.PrivateWsMB, $live.WorkingSetMB, $live.Threads, $live.Handles)
foreach ($k in $lat.Keys) { Write-Output ('latency {0}: p50 {1} ms, p95 {2} ms' -f $k, $lat[$k].P50, $lat[$k].P95) }
$sizes | Format-Table Route, Status, Plain, Wire, Encoding, CacheControl, ETag, Revalidate -AutoSize | Out-String -Width 200 | Write-Output

$over = [System.Collections.Generic.List[string]]::new()
if ($exeBytes / 1MB -gt $MaxExeMB) { $over.Add(('exe {0:N2} MB > {1}' -f ($exeBytes / 1MB), $MaxExeMB)) }
if ($live.PrivateMB -gt $MaxPrivateMB) { $over.Add("live-like private $($live.PrivateMB) MB > $MaxPrivateMB") }
if ($startupMs -gt $MaxStartupMs) { $over.Add(('startup {0:N1} ms > {1}' -f $startupMs, $MaxStartupMs)) }
if ($idle.WakeupsPerMin -gt $MaxIdleWakeupsPerMin) { $over.Add("idle context switches $($idle.WakeupsPerMin)/min > $MaxIdleWakeupsPerMin") }
foreach ($z in $sizes | Where-Object { $_.Route -in '/', '/api/history?26h' }) { if (-not $z.Encoding) { $over.Add("$($z.Route) not compressed") } }
$page = $sizes | Where-Object Route -EQ '/'
if ($page.Revalidate -ne 304 -and $page.CacheControl -notmatch 'max-age=[1-9]') { $over.Add('/ neither cached nor revalidated (304)') }

if ($JsonOut) {
    $json = [ordered]@{ exe = $Exe; provisional = $provisional; exeBytes = $exeBytes; binBytes = $binBytes; zipBytes = $zipBytes; startupMs = $startupMs
        startups = $startups; idle = $idle; live = $live; latency = $lat; sizes = $sizes; seedBytes = $seedBytes; notMeasured = @('pollCpuMsPerPoll', 'diskMBPerDay'); over = $over } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($JsonOut), $json)
}
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
if ($over.Count -gt 0) { Write-Output "bench: OVER BUDGET: $($over -join '; ')"; exit 1 }
Write-Output 'bench: every measured budget met; NOT MEASURED, neither met nor missed: poll CPU per poll, disk MB/day'
exit 0
