namespace BluettiWatcher.Io;

/// <summary>
/// The page's cloud controls (design.md §4 Reading source, Cloud sign-in): the reading source, the sign-in (the
/// <c>--cloud-login</c> flow run inside the service, its catcher on a port the OS assigns), the sign-out and the state.
/// One sign-in at a time: a second start while one waits gets the same address. Nothing here ever logs or answers a token.
/// </summary>
internal sealed class CloudAccount(StationConfig config, BluettiCloudReader reader, HttpMessageHandler handler, TimeProvider time, Action<string> log, CancellationToken stop) : IDisposable
{
    private readonly Lock _lock = new();
    private SignInStatus _signIn = new("idle", "", null);
    private CancellationTokenSource? _run;
    /// <summary>The running sign-in's authorize address (null when it ended before one); null when none runs.</summary>
    private Task<Uri?>? _address;

    /// <summary>How long a sign-in waits for the browser to come back.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The most a sign-out waits for a cloud read in flight to end.</summary>
    public TimeSpan GateWait { get; init; } = TimeSpan.FromSeconds(5);

    public SignInStatus SignIn { get { lock (_lock) return _signIn; } }

    public CloudStatus Status() => reader.Status();

    /// <summary>(address, "") = the browser goes there · (null, why) = the sign-in could not start or ended at once.</summary>
    public async Task<(Uri? Url, string Why)> StartSignInAsync(CancellationToken ct)
    {
        Task<Uri?> address;
        lock (_lock) address = _address ??= Begin();
        var url = await address.WaitAsync(ct);
        return url is null ? (null, SignIn.Message) : (url, "");
    }

    /// <summary>Under the lock.</summary>
    private Task<Uri?> Begin()
    {
        var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var address = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string last = "";
        var login = new CloudLogin(config, handler, time, line => { last = line; log($"cloud sign-in: {line}"); }, _ => { })
        {
            Wait = Wait,
            Store = reader.ReplaceTokenAsync,
        };
        _run = run;
        _signIn = new SignInStatus("waiting", "", time.GetUtcNow());
        _ = Task.Run(async () =>
        {
            int exit;
            try { exit = await login.RunAsync(url => address.TrySetResult(url), run.Token); }
            catch (OperationCanceledException) { exit = -1; }
            lock (_lock)
            {
                if (_run == run)
                {
                    _signIn = exit switch
                    {
                        0 => new SignInStatus("signed-in", last, time.GetUtcNow()),
                        < 0 => new SignInStatus("idle", "", time.GetUtcNow()),
                        _ => new SignInStatus("failed", last, time.GetUtcNow()),
                    };
                    _run = null;
                    _address = null;
                }
            }
            address.TrySetResult(null);
            run.Dispose();
        }, CancellationToken.None);   // the session ends by its own token; a disposed one never starts
        return address.Task;
    }

    /// <summary>Stops a waiting sign-in and deletes the token file · (200, "") done · (503, why) a read holds the reader · (500, why) not deleted.</summary>
    public async Task<(int Status, string Why)> SignOutAsync(CancellationToken ct)
    {
        StopRun();
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(GateWait);
        try { await reader.ReplaceTokenAsync(null, cap.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (503, "a cloud read is running: try again in a minute"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (500, $"the token file could not be deleted ({e.GetType().Name})"); }
        lock (_lock) _signIn = new SignInStatus("idle", "signed out", time.GetUtcNow());
        log("cloud: signed out on the page; the token file is deleted");
        return (200, "");
    }

    /// <summary>
    /// Saves the page's reading source and applies it from the next poll; null = "use config": the saved choice is deleted
    /// and the config key applies again · (200, "") · (409, why) with it nothing would be polled (no readings, no battery
    /// protection), nothing changed · (500, why) not saved or not deleted, nothing changed.
    /// </summary>
    public (int Status, string Why) SetSource(ReadingSource? source)
    {
        if (config.PollProblemWith(source) is { } nothingPolled)
            return (409, $"refused: with this reading source nothing would be polled ({nothingPolled}); the source stays {ReadingSourceFile.Name(config.Source)}");
        try
        {
            if (source is { } s) ReadingSourceFile.Write(config.DataDir, s);
            else File.Delete(ReadingSourceFile.PathOf(config.DataDir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (500, $"{ReadingSourceFile.FileName} could not be {(source is null ? "deleted" : "written")} ({e.GetType().Name})"); }
        config.PageSource = source;
        log(source is { } chosen ? $"reading source set on the page: {ReadingSourceFile.Name(chosen)}" : $"reading source: the page's choice is cleared, the config applies ({ReadingSourceFile.Name(config.Source)})");
        return (200, "");
    }

    /// <summary>Cancels a waiting sign-in (its session disposes its own source); the next start begins anew.</summary>
    private void StopRun()
    {
        lock (_lock)
        {
            _run?.Cancel();
            _run = null;
            _address = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
        }
    }
}
