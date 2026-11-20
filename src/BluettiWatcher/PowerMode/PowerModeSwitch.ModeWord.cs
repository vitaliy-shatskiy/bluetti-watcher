namespace BluettiWatcher.PowerMode;

internal sealed partial class PowerModeSwitch
{
    /// <summary>
    /// The modes engine's flag write: the flag says <paramref name="word"/> (normal, eco or emergency) and nothing on the
    /// machine is touched (the modes own the plan and the GPU). Under the flag lock; the battery, and any plan or GPU limit
    /// an older eco still owes, are carried over unchanged so the switch's <c>off</c> can still pay them. A flag already
    /// saying the word keeps its <c>since</c>. Exit 0 = written or already so · 2 = could not (the line says why).
    /// Runs on the thread that calls it (the lock is a mutex owned by the thread).
    /// </summary>
    public int WriteModeWord(string flagPath, string word, string source)
    {
        if (word is not ("normal" or "eco" or "emergency")) throw new ArgumentOutOfRangeException(nameof(word), word, "not normal, eco or emergency");
        if (source is not ("manual" or "station")) throw new ArgumentOutOfRangeException(nameof(source), source, "not manual or station");
        try
        {
            LoadNames();
            string path = FlagPathOf(flagPath);
            using var flagLock = TryEnterLock(out string lockError);
            if (flagLock is null) { Say($"power mode: COULD NOT RUN: {lockError}"); return 2; }
            var flag = ReadFlag(path);
            if (flag is not null && flag.Raw == word && !flag.Expired) return 0;
            bool lift = word == "normal";
            WriteFlag(path, new PmWrite
            {
                Mode = word, Source = lift ? "" : source, By = lift ? source : "", Battery = flag?.Battery,
                PrevPlan = flag?.PrevPlan ?? "", PrevGpuLimitW = flag?.PrevGpuLimitW, GpuCapW = flag?.GpuCapW,
            });
            return 0;
        }
        catch (Exception e) when (Fails(e))
        {
            Say($"power mode: COULD NOT RUN: {e.Message}");
            return 2;
        }
    }
}
