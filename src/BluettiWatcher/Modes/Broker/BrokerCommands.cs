using System.Security.Principal;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker's command-line verbs: <c>--broker</c> (the SYSTEM task's run) and <c>--broker-allow &lt;kind&gt; &lt;name&gt;</c>
/// (an elevated edit of the policy: <c>service</c> a service key name, <c>profile</c> a .nip already in the profiles
/// folder, <c>inspector</c> NVIDIA Profile Inspector's exe under Program Files or the broker folder). Both refuse to run
/// without elevation (exit 2).
/// </summary>
internal static class BrokerCommands
{
    private static bool Elevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static BrokerCore NewCore(BrokerPaths paths, IProcessRunner runner) =>
        new(paths, new ScmServiceControl(TimeProvider.System), new SchTasksTaskControl(runner), new NvidiaSmi(runner), new ProfileImporter(runner, TimeProvider.System), TimeProvider.System);

    public static async Task<int> RunBrokerAsync(TextWriter error)
    {
        if (!Elevated()) { await error.WriteLineAsync("Bluetti Watcher: --broker runs only as the SYSTEM broker task"); return 2; }
        var paths = BrokerPaths.Default;
        using var log = new FileLog(paths.Log);
        var runner = new ProcessRunner();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        return await new BrokerRunner(new WindowsBrokerInbox(paths, TimeProvider.System), NewCore(paths, runner), log.Write).RunAsync(stop.Token);
    }

    public static async Task<int> AllowAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 3) { await error.WriteLineAsync("Bluetti Watcher: usage: --broker-allow service|task|profile|inspector <name>"); return 2; }
        if (!Elevated()) { await error.WriteLineAsync("Bluetti Watcher: --broker-allow edits the admin-only policy: run it elevated"); return 2; }
        var paths = BrokerPaths.Default;
        string? problem = Allow(paths, args[1], args[2], out string done);
        await (problem is null ? output.WriteLineAsync(done) : error.WriteLineAsync($"Bluetti Watcher: {problem}"));
        return problem is null ? 0 : 1;
    }

    /// <summary>Adds <paramref name="name"/> to the policy file (written whole, atomically) · the problem, or null when done.</summary>
    internal static string? Allow(BrokerPaths paths, string kind, string name, out string done)
    {
        done = "";
        if (!File.Exists(paths.Policy)) return $"no policy at {paths.Policy}: install.ps1 -Broker -Apply (elevated) writes it";
        var policy = BrokerPolicy.Parse(File.ReadAllText(paths.Policy), out string error);
        if (policy is null) return $"the policy {paths.Policy} is not valid ({error}); it was not changed";
        BrokerPolicy next;
        switch (kind)
        {
            case "service":
                if (!BrokerPolicy.Matches(BrokerPolicy.ServiceNamePattern(), name)) return $"'{name}' is not a service key name";
                next = policy.AllowsService(name) ? policy : policy with { Services = [.. policy.Services, name] };
                break;
            case "task":
                if (!BrokerPolicy.Matches(BrokerPolicy.TaskNamePattern(), name)) return $"'{name}' is not a scheduled task's full name";
                next = policy.ListedTask(name) is not null ? policy : policy with { Tasks = [.. policy.Tasks, name] };
                break;
            case "profile":
                if (!BrokerPolicy.Matches(BrokerPolicy.ProfileNamePattern(), name)) return $"'{name}' is not a .nip file name";
                if (!File.Exists(Path.Combine(paths.Profiles, name))) return $"put {name} into {paths.Profiles} first";
                next = policy.AllowsProfile(name) ? policy : policy with { Profiles = [.. policy.Profiles, name] };
                break;
            case "inspector":
                if (!BrokerCore.AdminOnlyLocation(name, paths) || !File.Exists(name)) return $"'{name}' is not an existing exe under Program Files or {paths.Root}";
                next = policy with { ProfileInspector = Path.GetFullPath(name) };
                break;
            default:
                return $"unknown kind '{kind}': service, task, profile or inspector";
        }
        string tmp = paths.Policy + ".tmp";
        File.WriteAllText(tmp, next.ToJson());
        // A file an elevated admin writes may be owned by that user, whose implicit rights the broker refuses: Administrators own it.
        var info = new FileInfo(tmp);
        var security = info.GetAccessControl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(security);
        File.Move(tmp, paths.Policy, overwrite: true);
        done = $"allowed {kind} {name} in {paths.Policy}";
        return null;
    }
}
