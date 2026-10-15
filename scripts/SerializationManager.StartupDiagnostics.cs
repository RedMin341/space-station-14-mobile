using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Robust.Shared.Serialization.Manager;

public sealed partial class SerializationManager
{
    // Keep the original parallel execution; observe it without changing game data.
    private static void TraceTypeInitialization(string phase, IEnumerable<Type> types, Action<Type> action)
    {
        if (Environment.GetEnvironmentVariable("SS14_OFFLINE_SANDBOX") != "1")
        {
            Parallel.ForEach(types, action);
            return;
        }

        var active = new ConcurrentDictionary<int, string>();
        var completed = 0;
        var total = -1;
        var clock = Stopwatch.StartNew();
        using var stop = new ManualResetEventSlim(false);
        void Report()
        {
            var names = string.Join("; ", active.OrderBy(x => x.Key).Select(x => x.Value).Take(12));
            Console.WriteLine($"[SERIAL PROGRESS] {phase}: {Volatile.Read(ref completed)}/{Volatile.Read(ref total)}; " +
                $"elapsed={clock.Elapsed.TotalSeconds:F0}s; managedMB={GC.GetTotalMemory(false) / 1048576}; active=[{names}]");
        }
        // A dedicated thread still reports when thread-pool workers are blocked.
        var reporter = new Thread(() =>
        {
            while (!stop.Wait(TimeSpan.FromSeconds(10))) Report();
        }) { IsBackground = true, Name = "Serialization startup diagnostics" };
        Console.WriteLine($"[SERIAL START] {phase}");
        reporter.Start();
        try
        {
            var snapshot = types.ToArray();
            Volatile.Write(ref total, snapshot.Length);
            Parallel.ForEach(snapshot, type =>
            {
                var id = Environment.CurrentManagedThreadId;
                active[id] = type.FullName ?? type.Name;
                try { action(type); Interlocked.Increment(ref completed); }
                catch (Exception e)
                {
                    Console.WriteLine($"[SERIAL ERROR] {phase}: {type.FullName}: {e}");
                    throw;
                }
                finally { active.TryRemove(id, out _); }
            });
            Report();
            Console.WriteLine($"[SERIAL DONE] {phase}");
        }
        finally
        {
            stop.Set();
            reporter.Join();
        }
    }
}
