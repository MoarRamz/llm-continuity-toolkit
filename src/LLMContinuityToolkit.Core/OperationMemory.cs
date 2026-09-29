using System.Diagnostics;
using System.Runtime;

namespace LLMContinuityToolkit.Core;

public static class OperationMemory
{
    public static string Snapshot(string label)
    {
        var managed = GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d;
        using var process = Process.GetCurrentProcess();
        var privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
        return $"{label}: managed {managed:F1} MB • process private {privateMb:F1} MB";
    }

    public static TimeSpan CompactTransientAllocations()
    {
        var watch = Stopwatch.StartNew();
        var previousMode = GCSettings.LargeObjectHeapCompactionMode;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false);
        }
        finally
        {
            if (previousMode != GCLargeObjectHeapCompactionMode.CompactOnce)
                GCSettings.LargeObjectHeapCompactionMode = previousMode;
            watch.Stop();
        }
        return watch.Elapsed;
    }
}
