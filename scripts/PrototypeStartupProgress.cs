using System;
using System.Diagnostics;
using System.Threading;

namespace Robust.Shared.Prototypes;

// A single counter covers concurrent prototype work, so alternating worker names
// cannot make a stalled phase look alive. Only completed operations advance it.
internal sealed class PrototypeStartupProgress : IDisposable
{
    private readonly bool _enabled;
    private readonly string _phase;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim? _stop;
    private readonly Thread? _reporter;
    private long _completed;
    private string _last = "—";

    public PrototypeStartupProgress(string phase)
    {
        _phase = phase;
        _enabled = Environment.GetEnvironmentVariable("SS14_OFFLINE_SANDBOX") == "1";
        if (!_enabled) return;
        System.Console.WriteLine($"[PROTO START] {_phase}");
        _stop = new ManualResetEventSlim();
        _reporter = new Thread(() =>
        {
            while (!_stop.Wait(TimeSpan.FromSeconds(10))) Report();
        }) { IsBackground = true, Name = "Prototype startup progress" };
        _reporter.Start();
    }

    public void Advance(string completedItem)
    {
        if (!_enabled) return;
        Volatile.Write(ref _last, completedItem);
        Interlocked.Increment(ref _completed);
    }

    private void Report()
    {
        System.Console.WriteLine($"[PROTO PROGRESS] {_phase}: {Interlocked.Read(ref _completed)} операций; " +
            $"elapsed={(int)_clock.Elapsed.TotalSeconds}s; managedMB={GC.GetTotalMemory(false) / 1048576}; " +
            $"lastCompleted={Volatile.Read(ref _last)}");
    }

    public void Dispose()
    {
        if (!_enabled) return;
        _stop!.Set();
        _reporter!.Join();
        Report();
        _stop.Dispose();
    }
}
