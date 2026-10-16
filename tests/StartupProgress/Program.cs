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
Console.WriteLine("Startup progress tests passed");
