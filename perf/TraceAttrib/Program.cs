using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Symbols;

if (args.Length >= 1 && args[0] == "--reflect")
{
    var asm = typeof(StackSource).Assembly;
    foreach (var t in asm.GetTypes().Where(t => t.IsPublic &&
        (t.FullName!.Contains("EventPipeTraceEventDispatcher") || t.FullName == "Microsoft.Diagnostics.Tracing.Etlx.TraceLog")))
    {
        Console.WriteLine($"TYPE {t.FullName}  (base: {t.BaseType?.FullName})");
        foreach (var c in t.GetConstructors())
            Console.WriteLine($"   ctor: {c}");
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            Console.WriteLine($"   method: {m.Name}");
    }
    return 0;
}
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: TraceAttrib --reflect | <file.nettrace> [topN]");
    return 1;
}
int topN = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 30;
string path = args[0];

var selfSamples = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var inclusiveSamples = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
long total = 0;
long blocked = 0;

using (var source = new EventPipeTraceEventDispatcher(path))
{
    using var traceLog = new TraceLog(source);
    var computer = new SampleProfilerThreadTimeComputer(traceLog, new SymbolReader(TextWriter.Null));
    var stackSource = new MutableTraceEventStackSource(traceLog);
    computer.GenerateThreadTimeStacks(stackSource, traceLog.Events);
    stackSource.DoneAddingSamples();

    stackSource.ForEach(sample =>
    {
        var leafName = stackSource.GetFrameName(stackSource.GetFrameIndex(sample.StackIndex), false) ?? "<unknown>";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sawBlockedMarker = false;
        foreach (var cs in stackSource.Enumerate(sample.StackIndex))
        {
            var nm = stackSource.GetFrameName(stackSource.GetFrameIndex(cs), false) ?? "<unknown>";
            if (nm.StartsWith("[Blocked", StringComparison.OrdinalIgnoreCase) ||
                nm.StartsWith("[Ready", StringComparison.OrdinalIgnoreCase) ||
                nm.StartsWith("[Wait", StringComparison.OrdinalIgnoreCase) ||
                nm.StartsWith("[Sleep", StringComparison.OrdinalIgnoreCase))
            {
                sawBlockedMarker = true;
            }
            seen.Add(nm);
        }
        if (sawBlockedMarker) { blocked++; return; }

        total++;
        selfSamples.TryGetValue(leafName, out var s);
        selfSamples[leafName] = s + 1;
        foreach (var nm in seen)
        {
            inclusiveSamples.TryGetValue(nm, out var inc);
            inclusiveSamples[nm] = inc + 1;
        }
    });
}

double Pct(long v) => total == 0 ? 0 : v * 100.0 / total;
Console.WriteLine($"cpu-only samples: {total}   off-cpu(blocked-marker) samples skipped: {blocked}");
Console.WriteLine($"approx cpu seconds at ~100Hz sampling: {total / 100.0:F2}");
Console.WriteLine();
Console.WriteLine($"== top {topN} by SELF cpu time ==");
foreach (var kv in selfSamples.OrderByDescending(k => k.Value).Take(topN))
    Console.WriteLine($"{Pct(kv.Value),6:F2}%  {kv.Value,7}  {kv.Key}");
Console.WriteLine();
Console.WriteLine($"== top {topN} by INCLUSIVE cpu time ==");
foreach (var kv in inclusiveSamples.OrderByDescending(k => k.Value).Take(topN))
    Console.WriteLine($"{Pct(kv.Value),6:F2}%  {kv.Value,7}  {kv.Key}");
return 0;
