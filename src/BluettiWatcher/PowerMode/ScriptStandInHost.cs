using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BluettiWatcher.PowerMode;

/// <summary>
/// The PowerShell that runs the *.ps1 stand-ins (POWER_MODE_POWERCFG / _SCHTASKS / _NVIDIASMI, tests only) the way
/// power-mode.ps1 ran them in its own process: one host per command, started before the flag's lock is taken, each call
/// splatted with errors merged and a throw = code -1. pwsh on PATH, else Windows PowerShell 5.1.
/// </summary>
internal sealed class ScriptStandInHost : IDisposable
{
    private const string EndMark = "@@power-mode-stand-in-end@@";
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = [];

    public ScriptStandInHost(IReadOnlyDictionary<string, string> env)
    {
        string host = FindOnPath("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-"]) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) =>
        {
            if (_lines.IsAddingCompleted) return;
            if (e.Data is null) _lines.CompleteAdding();
            else _lines.Add(e.Data);
        };
        _process.ErrorDataReceived += (_, e) => System.Diagnostics.Debug.WriteLine(e.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Send("[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false");
    }

    private void Send(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    private static string Literal(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>One stand-in call: its exit code and non-blank lines; -2 = no answer within <paramref name="timeoutMs"/>.</summary>
    public PmToolResult Run(string script, IReadOnlyList<string> list, int timeoutMs)
    {
        string body = "& { $ErrorActionPreference = 'Stop'; $f = " + Literal(script) + "; $a = @(" + string.Join(", ", list.Select(Literal)) + "); $c = 0\n"
            + "try { $global:LASTEXITCODE = 0; $o = & $f @a 2>&1; $c = $LASTEXITCODE; $o | ForEach-Object { \"$_\" } } catch { $_.Exception.Message; $c = -1 }\n"
            + "'" + EndMark + " ' + $c }";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        Send($"Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')))");
        var output = new List<string>();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
            if (!_lines.TryTake(out var line, left))
                return new(-2, [$"{Path.GetFileName(script)} timed out after {timeoutMs.ToString(CultureInfo.InvariantCulture)} ms (stopped)"]);
            if (line.StartsWith(EndMark, StringComparison.Ordinal))
            {
                int code = int.TryParse(line[EndMark.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : -1;
                return new(code, [.. output.Where(l => l.Trim().Length > 0)]);
            }
            output.Add(line);
        }
    }

    internal static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string d = dir.Trim();
            if (d.Length == 0 || d.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            string candidate = Path.Combine(d, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            _process.WaitForExit();   // the output events are over
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { System.Diagnostics.Debug.WriteLine(e.Message); }
        _process.Dispose();
        _lines.Dispose();
    }
}
