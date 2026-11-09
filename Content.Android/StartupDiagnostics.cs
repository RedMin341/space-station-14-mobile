using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Content.Android;

/// <summary>
/// Persistent breadcrumbs for an Android startup that freezes or is killed
/// without throwing a managed exception. The launcher can read them even
/// after the previous SDL game activity/process disappears.
/// </summary>
internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static Timer? _timer;
    private static DateTime _startedUtc;
    private static DateTime _lastSampleUtc;
    private static TimeSpan _lastCpuTime;
    private static string _phase = "not started";
    private const long MaxFileBytes = 24 * 1024;

    internal static string Path => System.IO.Path.Combine(CrashReporter.FilesDir, "startup-diagnostics.txt");

    internal static void Start()
    {
        lock (Sync)
        {
            try { _timer?.Dispose(); } catch { }
            _startedUtc = DateTime.UtcNow;
            _lastSampleUtc = _startedUtc;
            _lastCpuTime = TimeSpan.Zero;
            _phase = "MainActivity.OnCreate";
            try
            {
                Directory.CreateDirectory(CrashReporter.FilesDir);
                File.WriteAllText(Path, "SS14 Android startup diagnostics, UTC\n" +
                    CrashReporter.GetDeviceSummary() +
                    "Fobos content revision: " + FobosBuild.ContentRevision + "\n");
            }
            catch { }
            WriteUnsafe("BEGIN: MainActivity.OnCreate");
            // Timer runs independently from SDL's native game thread.
            _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        }
    }

    internal static void Mark(string phase)
    {
        lock (Sync)
        {
            _phase = phase;
            WriteUnsafe("STAGE: " + phase);
        }
    }

    private static void Sample()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            string phase;
            DateTime started;
            DateTime previous;
            TimeSpan previousCpu;
            lock (Sync)
            {
                phase = _phase;
                started = _startedUtc;
                previous = _lastSampleUtc;
                previousCpu = _lastCpuTime;
                _lastSampleUtc = now;
            }

            var log = CrashReporter.GameLogPath;
            var logUtc = File.Exists(log) ? File.GetLastWriteTimeUtc(log) : DateTime.MinValue;
            var silentFor = logUtc == DateTime.MinValue
                ? "no game.log"
                : Math.Max(0, (int)(now - logUtc).TotalSeconds) + "s";
            var managedMiB = GC.GetTotalMemory(false) / (1024 * 1024);
            var procMem = ReadProcStatus();
            string cpuUsage = "unavailable";
            try
            {
                using var p = Process.GetCurrentProcess();
                var cpu = p.TotalProcessorTime;
                if (previousCpu != TimeSpan.Zero && (now - previous).TotalSeconds > 0)
                {
                    var cpuPct = 100 * (cpu - previousCpu).TotalSeconds / (now - previous).TotalSeconds;
                    cpuUsage = cpuPct.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "% of one core";
                }
                lock (Sync) _lastCpuTime = cpu;
            }
            catch { }

            lock (Sync)
            {
                WriteUnsafe($"HEARTBEAT: elapsed={(int)(now - started).TotalSeconds}s; phase={phase}; " +
                    $"last_game_log={silentFor}; managed_heap={managedMiB}MiB; cpu={cpuUsage}; {procMem}");
            }
        }
        catch (Exception ex)
        {
            lock (Sync) WriteUnsafe("HEARTBEAT FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string ReadProcStatus()
    {
        try
        {
            var fields = File.ReadLines("/proc/self/status")
                .Where(x => x.StartsWith("VmRSS:", StringComparison.Ordinal) ||
                            x.StartsWith("VmHWM:", StringComparison.Ordinal) ||
                            x.StartsWith("VmSize:", StringComparison.Ordinal) ||
                            x.StartsWith("Threads:", StringComparison.Ordinal))
                .Select(x => x.Trim())
                .ToArray();
            return string.Join("; ", fields);
        }
        catch { return "proc/status unavailable"; }
    }

    private static void WriteUnsafe(string value)
    {
        try
        {
            Directory.CreateDirectory(CrashReporter.FilesDir);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxFileBytes)
            {
                var lastLines = File.ReadAllLines(Path).TakeLast(100).ToArray();
                File.WriteAllLines(Path, lastLines);
            }

            File.AppendAllText(Path, $"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {value}\n");
        }
        catch { }
    }
}
