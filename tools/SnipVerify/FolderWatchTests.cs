using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Services;

internal static class FolderWatchTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "snip-watch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var calls = 0;
            using (var watcher = SnipFolderWatcher.Start(dir, () => Interlocked.Increment(ref calls),
                       debounce: TimeSpan.FromMilliseconds(120)))
            {
                check(watcher.IsWatching, "watch: a real folder is being watched");

                for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(dir, $"snip{i}.png"), new string('x', 200));
                await Task.Delay(600);
                check(calls is >= 1 and <= 4, $"watch: 25 rapid writes coalesced into {calls} notification(s)");

                var before = Volatile.Read(ref calls);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                File.WriteAllText(Path.Combine(dir, "later.png"), "y");
                while (Volatile.Read(ref calls) == before && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
                check(Volatile.Read(ref calls) > before, $"watch: a new file notified in {sw.ElapsedMilliseconds} ms");

                before = Volatile.Read(ref calls);
                File.Delete(Path.Combine(dir, "later.png"));
                sw.Restart();
                while (Volatile.Read(ref calls) == before && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
                check(Volatile.Read(ref calls) > before, $"watch: a delete notified in {sw.ElapsedMilliseconds} ms");
            }

            var afterDispose = Volatile.Read(ref calls);
            File.WriteAllText(Path.Combine(dir, "orphan.png"), "z");
            await Task.Delay(500);
            check(Volatile.Read(ref calls) == afterDispose, "watch: disposed watcher stays silent");

            var missing = Path.Combine(dir, "does-not-exist");
            var missingCalls = 0;
            using (var fallback = SnipFolderWatcher.Start(missing, () => Interlocked.Increment(ref missingCalls)))
            {
                check(!fallback.IsWatching, "watch: an unwatchable folder reports polling-only");
            }
            check(missingCalls == 0, "watch: the fallback never fires");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
