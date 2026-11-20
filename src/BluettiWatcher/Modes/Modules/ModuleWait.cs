namespace BluettiWatcher.Modes.Modules;

/// <summary>A bounded poll: a module that started a change (a service stop, an app close) reads until it settles or the budget ends.</summary>
internal static class ModuleWait
{
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    /// <summary>True as soon as <paramref name="reached"/> holds; false once <paramref name="budget"/> has passed without it.</summary>
    public static async Task<bool> UntilAsync(Func<CancellationToken, Task<bool>> reached, TimeSpan budget, TimeProvider time, CancellationToken ct)
    {
        long start = time.GetTimestamp();
        while (true)
        {
            if (await reached(ct)) return true;
            if (time.GetElapsedTime(start) >= budget) return false;
            await Task.Delay(Step, time, ct);
        }
    }
}
