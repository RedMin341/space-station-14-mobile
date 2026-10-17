using Content.Android;
static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
var a = StartupProgress.FromLogLine("[SERIAL PROGRESS] Types: 68/6756; elapsed=25s; managedMB=128; active=[A]");
var b = StartupProgress.FromLogLine("[SERIAL PROGRESS] Types: 68/6756; elapsed=250s; managedMB=140; active=[B]");
Check(a == b, "Heartbeat must not count as forward progress");
var stalled = new StartupWaitPolicy();
stalled.Observe(a, TimeSpan.FromSeconds(25));
stalled.Observe(b, TimeSpan.FromSeconds(250));
Check(stalled.TimeoutReason(TimeSpan.FromSeconds(324)) == null, "Keep waiting before inactivity deadline");
Check(stalled.TimeoutReason(TimeSpan.FromSeconds(325)) != null, "Unchanged counter expires after five minutes");
var moving = new StartupWaitPolicy();
for (int minutes = 1; minutes <= 29; minutes++)
{
    moving.Observe($"Types: {minutes}/6756", TimeSpan.FromMinutes(minutes));
    Check(moving.TimeoutReason(TimeSpan.FromMinutes(minutes)) == null, "Moving startup must survive five-minute total limit");
}
Check(moving.TimeoutReason(TimeSpan.FromMinutes(30)) != null, "Absolute limit remains finite");
Check(StartupProgress.FromLogLine("[START] Карта") != null, "Stage transitions count");
Check(StartupProgress.FromLogLine("[INFO] unrelated message") == null, "Unrelated logs do not count");
var prototypes = new StartupWaitPolicy();
var protoFirst = StartupProgress.FromLogLine("[PROTO PROGRESS] YAML: 100 операций; elapsed=10s; lastCompleted=A");
var protoHeartbeat = StartupProgress.FromLogLine("[PROTO PROGRESS] YAML: 100 операций; elapsed=299s; lastCompleted=B");
Check(protoFirst != null && protoFirst == protoHeartbeat, "Prototype metadata must not reset timeout");
prototypes.Observe(protoFirst, TimeSpan.FromSeconds(10));
prototypes.Observe(protoHeartbeat, TimeSpan.FromSeconds(299));
Check(prototypes.TimeoutReason(TimeSpan.FromSeconds(310)) != null, "Stalled prototype counter expires");
var protoNext = StartupProgress.FromLogLine("[PROTO PROGRESS] YAML: 101 операций; elapsed=300s; lastCompleted=C");
prototypes.Observe(protoNext, TimeSpan.FromSeconds(300));
Check(prototypes.TimeoutReason(TimeSpan.FromSeconds(310)) == null, "Completed prototype work extends wait");
Check(StartupProgress.FromLogLine("[PROTO START] Наследование") != null, "Prototype phase transitions count");
// Check that simultaneous workers neither lose counts nor leak the reporter thread.
var savedConsole = Console.Out;
var savedEnv = Environment.GetEnvironmentVariable("SS14_OFFLINE_SANDBOX");
using var captured = new StringWriter();
try
{
    Environment.SetEnvironmentVariable("SS14_OFFLINE_SANDBOX", "1");
    Console.SetOut(TextWriter.Synchronized(captured));
    using (var tracker = new Robust.Shared.Prototypes.PrototypeStartupProgress("Test"))
        Parallel.For(0, 5000, i => tracker.Advance(i.ToString()));
    Check(captured.ToString().Contains("[PROTO PROGRESS] Test: 5000 операций;"), "Concurrent progress counts must be exact");
}
finally
{
    Console.SetOut(savedConsole);
    Environment.SetEnvironmentVariable("SS14_OFFLINE_SANDBOX", savedEnv);
}
Console.WriteLine("Startup progress tests passed");
