using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Services;

// Refresh-cadence verification: the folder watcher that re-arms the poll's interval is the one
// piece of the refresh path that cannot be unit tested (it is all real I/O and real threads), so
// it is driven here against a throwaway folder. The invariants: a burst of events collapses into
// a single notification, a notification arrives quickly, and after Dispose nothing fires -- the
// page relies on the last one to be able to stop watching without a stray callback landing on a
// torn-down page.
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

                // A save is several events in a row (create, write, size, last-write). The page only
                // needs to know "something moved", so that burst must not become a burst of calls.
                for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(dir, $"snip{i}.png"), new string('x', 200));
                await Task.Delay(600);
                check(calls is >= 1 and <= 4, $"watch: 25 rapid writes coalesced into {calls} notification(s)");

                // A quiet pick-up: a file landing with nothing else happening.
                var before = Volatile.Read(ref calls);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                File.WriteAllText(Path.Combine(dir, "later.png"), "y");
                while (Volatile.Read(ref calls) == before && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
                check(Volatile.Read(ref calls) > before, $"watch: a new file notified in {sw.ElapsedMilliseconds} ms");

                // Deletes matter too: the poll has to drop a card that went away.
                before = Volatile.Read(ref calls);
                File.Delete(Path.Combine(dir, "later.png"));
                sw.Restart();
                while (Volatile.Read(ref calls) == before && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
                check(Volatile.Read(ref calls) > before, $"watch: a delete notified in {sw.ElapsedMilliseconds} ms");
            }

            // After the page unloads, the watcher must be silent.
            var afterDispose = Volatile.Read(ref calls);
            File.WriteAllText(Path.Combine(dir, "orphan.png"), "z");
            await Task.Delay(500);
            check(Volatile.Read(ref calls) == afterDispose, "watch: disposed watcher stays silent");

            // A folder that cannot be watched degrades to polling instead of throwing.
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
