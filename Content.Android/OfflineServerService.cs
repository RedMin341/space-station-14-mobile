using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Android.App;
using Android.Content;
using Android.OS;
using Content.Android.Launcher.Api;

namespace Content.Android;

[Service(Exported = false, Process = ":sandbox",
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class OfflineServerService : Service
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SqliteVersion();

    private bool _started;
    private const string Channel = "ss14_offline";
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "stop") { StopSelf(); return StartCommandResult.NotSticky; }
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Локальная комната", NotificationImportance.Low));
        var stop = PendingIntent.GetService(this, 0, new Intent(this, typeof(OfflineServerService)).SetAction("stop"),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(OfflineActivity)),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("SS14: автономная комната")
            .SetContentText("Сервер работает только на этом телефоне")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetOngoing(true).SetContentIntent(open)
            .AddAction(new Notification.Action.Builder(null, "Остановить", stop).Build()).Build();
        StartForeground(276, notification);
        var run = intent?.GetStringExtra("run");
        if (_started)
        {
            if (run != null) File.WriteAllText(Path.Combine(FilesDir!.AbsolutePath, "offline-" + run + ".error"),
                "Комната уже работает. Останови её и запусти заново.");
            return StartCommandResult.NotSticky;
        }
        if (run == null) { StopSelf(); return StartCommandResult.NotSticky; }
        _started = true;
        var root = FilesDir!.AbsolutePath;
        new Thread(() => Run(root, run)) { IsBackground = true }.Start();
        return StartCommandResult.NotSticky;
    }

    private void Run(string root, string run)
    {
        var ready = Path.Combine(root, "offline-" + run + ".ready");
        var error = Path.Combine(root, "offline-" + run + ".error");
        try
        {
            Directory.SetCurrentDirectory(root);
            AczClient.InitNative();
            // Java System.loadLibrary called through JNI can use the bootstrap loader.
            // Resolve the app-owned library explicitly for the managed SQLite provider.
            var sqlitePath = Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libsqlite3.so");
            if (!File.Exists(sqlitePath))
                throw new FileNotFoundException("В установленном APK отсутствует SQLite: " + sqlitePath);
            var sqlite = NativeLibrary.Load(sqlitePath);
            var provider = Assembly.Load("SQLitePCLRaw.provider.sqlite3");
            NativeLibrary.SetDllImportResolver(provider, (name, assembly, searchPath) =>
                name == "sqlite3" || name == "libsqlite3.so" ? sqlite : IntPtr.Zero);
            var version = Marshal.GetDelegateForFunctionPointer<SqliteVersion>(
                NativeLibrary.GetExport(sqlite, "sqlite3_libversion"));
            File.AppendAllText(Path.Combine(root, "offline-server.log"),
                "\nSQLite loaded: " + Marshal.PtrToStringUTF8(version()) + "\n");
            System.Environment.SetEnvironmentVariable("SS14_OFFLINE_SANDBOX", "1");
            var writer = new ServerLog(Path.Combine(root, "offline-server.log"), ready);
            Console.SetOut(TextWriter.Synchronized(writer));
            Console.SetError(Console.Out);
            var shared = Assembly.Load("Robust.Shared");
            shared.GetType("Robust.Shared.ProgramShared", true)!.GetField("PathOffset")!.SetValue(null, root + "/");
            var server = Assembly.Load("Robust.Server");
            var options = Activator.CreateInstance(server.GetType("Robust.Server.ServerOptions", true)!);
            var data = Path.Combine(root, "offline-data");
            Directory.CreateDirectory(data);
            var args = new[] { "--config-file", Path.Combine(root, "offline-server.toml"),
                "--data-dir", data, "--mount-zip", Path.Combine(root, "offline.zip") };
            var start = server.GetType("Robust.Server.Program", true)!.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic)!;
            start.Invoke(null, new object?[] { args, options, true });
            File.WriteAllText(error, "Сервер остановился. Подробности в журнале запуска комнаты.");
        }
        catch (Exception e)
        {
            var details = e.ToString();
            File.AppendAllText(Path.Combine(root, "offline-server.log"), "\n" + details);
            File.WriteAllText(error, details);
        }
        finally { if (File.Exists(ready)) File.Delete(ready); StopSelf(); }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
        // The server owns global IoC state. Only terminate its isolated process.
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private sealed class ServerLog : TextWriter
    {
        private readonly string _path;
        private readonly string _ready;
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;
        public ServerLog(string path, string ready) { _path = path; _ready = ready; File.WriteAllText(path, ""); }
        public override void Write(char value)
        {
            if (value == '\n')
            {
                var text = _line.ToString(); _line.Clear();
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
                File.AppendAllText(_path, "[" + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + text + "\n");
                if (text.Contains("Server Version ") && text.Contains("-> Ready")) File.WriteAllText(_ready, "ready");
            }
            else _line.Append(value);
        }
    }
}
