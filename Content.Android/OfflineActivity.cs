using System.Reflection;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace Content.Android;

[Activity(Label = "SS14 Песочница", MainLauncher = true, Exported = true,
    Icon = "@mipmap/ic_launcher", Theme = "@android:style/Theme.Material.NoActionBar",
    ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
public sealed class OfflineActivity : Activity
{
    private CancellationTokenSource? _launch;
    private TextView _status = null!;
    private Button _start = null!;
    private string Root => FilesDir!.AbsolutePath;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var scroll = new ScrollView(this);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(32, 16, 32, 16);
        scroll.AddView(layout);
        void Text(string text, float size = 18)
        {
            var view = new TextView(this) { Text = text, TextSize = size };
            layout.AddView(view);
        }
        Text("КОМНАТА ЧЁРНОГО КОТИКА", 26);
        Text("Экспериментальная автономная песочница на публичных исходниках Фобоса и Android-порте SolomonNumb1/space-station-14-mobile.\nРедактировал Чёрный Котик. Интернет и аккаунт не нужны. Каждый запуск создаёт новую комнату.");
        _start = new Button(this) { Text = "Начать тренировку" };
        _start.Click += async (_, _) => await LaunchRoom();
        layout.AddView(_start);
        var stop = new Button(this) { Text = "Остановить комнату" };
        stop.Click += (_, _) => { _launch?.Cancel(); StopRoom(); _status.Text = "Комната остановлена."; };
        layout.AddView(stop);
        var help = new Button(this) { Text = "Управление и админские возможности" };
        help.Click += (_, _) => new AlertDialog.Builder(this)
            .SetTitle("Как играть")
            .SetMessage("ПЕСОЧНИЦА — B: инструменты песочницы, призрак, полный доступ и возрождение.\nПРЕДМЕТЫ — F5: выбрать вещь и коснуться пола для создания.\nАДМИН — F7: обычная админ-панель игры.\n\nКасание — левая кнопка мыши. ПКМ (SEC) — включить правую кнопку и коснуться цели: меню объектов и действий. ОСМОТР — Shift + касание. ТЯНУТЬ — Ctrl + касание. ALT — Alt + касание. РУКА — X, ВЫЛОЖ. — Q, ПРИМ. — Z, ДЕЙСТ. — E, РЮКЗАК — V.\n\nПРАВКА позволяет двигать кнопки и добавлять свои клавиши. На столе лежат несколько инструментов вместе — можно потренировать выбор. Есть шкафы, ящик, рюкзак, аптечка, уточка, банановая кожура и кот.\n\nАдминские права действуют только в этой комнате. Закрытые механики Фобоса не включены. Комната пока не сохраняется; раскладка управления сохраняется.")
            .SetPositiveButton("Понятно", (_, _) => { }).Show();
        layout.AddView(help);
        var logs = new Button(this) { Text = "Журнал запуска комнаты" };
        logs.Click += (_, _) =>
        {
            var path = Path.Combine(Root, "offline-server.log");
            var text = File.Exists(path) ? File.ReadAllText(path) : "Сервер ещё не запускался.";
            if (text.Length > 16000) text = text[^16000..];
            new AlertDialog.Builder(this).SetTitle("Журнал сервера").SetMessage(text)
                .SetPositiveButton("Закрыть", (_, _) => { }).Show();
        };
        layout.AddView(logs);
        _status = new TextView(this) { Text = "Готово к запуску. Первый запуск распакует встроенные ресурсы.", TextSize = 16 };
        layout.AddView(_status);
        SetContentView(scroll);
    }

    private async Task LaunchRoom()
    {
        if (_launch != null) return;
        _launch = new CancellationTokenSource();
        var cancel = _launch.Token;
        _start.Enabled = false;
        try
        {
            // Each run is a fresh local world. Stop both old isolated processes first.
            StopRoom();
            await Task.Delay(500, cancel);
            var run = Guid.NewGuid().ToString("N");
            _status.Text = "Распаковка встроенных ресурсов…";
            var root = Root;
            await Task.Run(() => PrepareResources(root, cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            var intent = new Intent(this, typeof(OfflineServerService));
            intent.PutExtra("run", run);
            StartForegroundService(intent);
            _status.Text = "Запускается локальный сервер. Это может занять несколько минут…";
            var ready = Path.Combine(root, "offline-" + run + ".ready");
            var error = Path.Combine(root, "offline-" + run + ".error");
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(ready))
            {
                cancel.ThrowIfCancellationRequested();
                if (File.Exists(error)) throw new InvalidOperationException(File.ReadAllText(error));
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Сервер не запустился за 5 минут. Открой журнал запуска комнаты.");
                await Task.Delay(500, cancel);
            }
            cancel.ThrowIfCancellationRequested();
            var game = new Intent(this, typeof(MainActivity));
            game.PutExtra("mount_zip", Path.Combine(root, "offline.zip"));
            game.PutExtra("connect_addr", "udp://127.0.0.1:12121");
            game.PutExtra("ss14_addr", "ss14://127.0.0.1:12121");
            game.PutExtra("username", "BlackKitty");
            StartActivity(game);
            _status.Text = "Комната работает на телефоне. После игры нажми «Остановить комнату».";
        }
        catch (OperationCanceledException) { StopRoom(); }
        catch (Exception e) { StopRoom(); _status.Text = "Ошибка запуска: " + e.Message; }
        finally { _launch?.Dispose(); _launch = null; _start.Enabled = true; }
    }

    private void PrepareResources(string root, CancellationToken cancel)
    {
        var assembly = typeof(OfflineActivity).Assembly;
        var marker = Path.Combine(root, "offline-resources.version");
        var version = assembly.ManifestModule.ModuleVersionId + ":" +
            PackageManager!.GetPackageInfo(PackageName!, global::Android.Content.PM.PackageInfoFlags.MetaData)!.LastUpdateTime;
        if (File.Exists(marker) && File.ReadAllText(marker) == version &&
            File.Exists(Path.Combine(root, "offline.zip")) && File.Exists(Path.Combine(root, "resources.zip"))) return;
        foreach (var (name, target) in new[] { ("Content.Android.resources.zip", "resources.zip"),
                     ("Content.Android.offline.zip", "offline.zip"), ("Offline.server.toml", "offline-server.toml") })
        {
            cancel.ThrowIfCancellationRequested();
            using var input = name == "Content.Android.offline.zip"
                ? Assets!.Open("offline.zip")
                : assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
            var path = Path.Combine(root, target);
            using (var output = File.Create(path + ".tmp")) input.CopyTo(output);
            File.Move(path + ".tmp", path, true);
        }
        File.WriteAllText(marker, version);
    }

    private void StopRoom()
    {
        StopService(new Intent(this, typeof(OfflineServerService)));
        // Robust and SDL cannot be initialized twice in a process. Both children
        // belong to this APK; never terminate the launcher or another application.
        var manager = (ActivityManager?)GetSystemService(ActivityService);
        foreach (var process in manager?.RunningAppProcesses ?? Array.Empty<ActivityManager.RunningAppProcessInfo>())
        {
            if (process.Uid == global::Android.OS.Process.MyUid() &&
                (process.ProcessName == PackageName + ":game" || process.ProcessName == PackageName + ":sandbox"))
                global::Android.OS.Process.KillProcess(process.Pid);
        }
    }
    protected override void OnDestroy()
    {
        _launch?.Cancel();
        base.OnDestroy();
    }
}
