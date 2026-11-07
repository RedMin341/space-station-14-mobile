using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Widget;

namespace Content.Android;

/// <summary>
/// Fobos server content is downloaded by the normal launcher. For an engine
/// update, find the newest *successfully built* matching Fobos APK artifact,
/// not a stale, hardcoded GitHub Actions URL.
/// </summary>
internal sealed class FobosApkUpdater
{
    private const string Repository = "RedMin341/space-station-14-mobile";
    private const string FobosAddress = "ss14://f1.deadspace14.net:1212";
    private const string RunsApi =
        "https://api.github.com/repos/" + Repository +
        "/actions/runs?branch=ss14-fobos-276&per_page=30";
    private const string ActionsPage =
        "https://github.com/" + Repository + "/actions/workflows/android-release.yml";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(18),
    };

    private readonly LauncherActivity _activity;
    private readonly Launcher.Api.HubClient _hub = new();
    private int _busy;

    private sealed record PublishedApk(Version Version, string Engine, long RunId, long ArtifactId)
    {
        public string Url => $"https://github.com/{Repository}/actions/runs/{RunId}/artifacts/{ArtifactId}";
    }

    internal FobosApkUpdater(LauncherActivity activity)
    {
        _activity = activity;
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SS14-Fobos-Mobile/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Check the live Fobos engine and offer its current APK on launch.</summary>
    internal async Task CheckAsync(bool manual = false)
    {
        try
        {
            var info = await _hub.FetchServerInfoAsync(FobosAddress);
            if (info?.Build == null || !string.Equals(info.Build.ForkId, FobosBuild.Fork, StringComparison.Ordinal))
            {
                if (manual)
                    Toast("Не удалось получить версию сервера Фобос");
                return;
            }
            await OfferAsync(info.Build.EngineVersion, manual);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
            if (manual)
                Toast("Не удалось проверить обновления. Проверь интернет.");
        }
    }

    /// <summary>Invoked by the connection screen on an incompatible engine.</summary>
    internal Task OfferForServerAsync(string? serverEngine) =>
        OfferAsync(serverEngine, manual: true);

    private async Task OfferAsync(string? targetEngine, bool manual)
    {
        if (string.IsNullOrWhiteSpace(targetEngine))
        {
            if (manual)
                Toast("Фобос не сообщил версию движка");
            return;
        }

        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return;
        try
        {
            var apk = await FindLatestCompatibleAsync(targetEngine);
            var local = GetInstalledVersion();
            if (apk != null && apk.Version > local)
            {
                ShowDownload(apk, targetEngine);
            }
            else if (manual || targetEngine != FobosBuild.Engine)
            {
                var text = targetEngine != FobosBuild.Engine
                    ? $"Сервер обновился до RobustToolbox {targetEngine}. Подходящего готового APK пока нет. Не скачивай старые сборки: GitHub собирает новую автоматически."
                    : $"Готового обновления новее {local} пока нет.";
                _activity.RunOnUiThread(() =>
                    new AlertDialog.Builder(_activity)
                        .SetTitle("SS14 Фобос 276")
                        .SetMessage(text)
                        .SetPositiveButton("Сборки GitHub", (_, _) => Open(ActionsPage))
                        .SetNegativeButton("Закрыть", (_, _) => { })
                        .Show());
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
            if (manual)
                _activity.RunOnUiThread(() =>
                    new AlertDialog.Builder(_activity)
                        .SetTitle("Не удалось проверить APK")
                        .SetMessage("GitHub недоступен или ограничил запросы. " +
                                    "Открой сборки вручную, если обновление сервера уже вышло.")
                        .SetPositiveButton("GitHub", (_, _) => Open(ActionsPage))
                        .SetNegativeButton("Закрыть", (_, _) => { })
                        .Show());
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<PublishedApk?> FindLatestCompatibleAsync(string engineVersion)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync(RunsApi));
        if (!json.RootElement.TryGetProperty("workflow_runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
            return null;

        PublishedApk? best = null;
        foreach (var run in runs.EnumerateArray())
        {
            if (!IsText(run, "name", "Build SS14 Fobos 276") ||
                !IsText(run, "status", "completed") ||
                !IsText(run, "conclusion", "success") ||
                !IsText(run, "head_branch", "ss14-fobos-276"))
                continue;
            if (!run.TryGetProperty("id", out var runIdElement) ||
                !run.TryGetProperty("head_sha", out var shaElement))
                continue;

            var sha = shaElement.GetString();
            if (sha == null || !Regex.IsMatch(sha, @"\A[0-9a-f]{40}\z"))
                continue;

            // Never offer a successful APK compiled for a *different* engine.
            // Read the exact source revision used by this GitHub Actions run.
            var sourceUrl = $"https://raw.githubusercontent.com/{Repository}/{sha}/Content.Android/FobosBuild.cs";
            var buildSource = await Http.GetStringAsync(sourceUrl);
            var engineMatch = Regex.Match(buildSource,
                "public const string Engine\\s*=\\s*\"([^\"]+)\"");
            if (!engineMatch.Success || engineMatch.Groups[1].Value != engineVersion)
                continue;

            var runId = runIdElement.GetInt64();
            var artifactApi = $"https://api.github.com/repos/{Repository}/actions/runs/{runId}/artifacts?per_page=100";
            using var artifactsJson = JsonDocument.Parse(await Http.GetStringAsync(artifactApi));
            if (!artifactsJson.RootElement.TryGetProperty("artifacts", out var artifacts) ||
                artifacts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (artifact.TryGetProperty("expired", out var expired) && expired.GetBoolean())
                    continue;
                if (!artifact.TryGetProperty("name", out var artifactName))
                    continue;
                var name = artifactName.GetString();
                if (string.IsNullOrEmpty(name) ||
                    !name.StartsWith("SS14-Fobos-v", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith("-APK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var versionText = name.Substring(
                    "SS14-Fobos-v".Length,
                    name.Length - "SS14-Fobos-v".Length - "-APK".Length);
                if (!Version.TryParse(versionText, out var version) ||
                    !artifact.TryGetProperty("id", out var artifactIdElement))
                    continue;
                var candidate = new PublishedApk(version, engineVersion,
                    runId, artifactIdElement.GetInt64());
                if (best == null || candidate.Version > best.Version)
                    best = candidate;
            }

            // GitHub lists runs in reverse chronological order. The first
            // compatible successful run is sufficient unless a better artifact
            // was already found among its outputs.
            if (best != null)
                return best;
        }
        return best;
    }

    private static bool IsText(JsonElement element, string name, string value) =>
        element.TryGetProperty(name, out var field) &&
        string.Equals(field.GetString(), value, StringComparison.Ordinal);

    private Version GetInstalledVersion()
    {
        try
        {
            var installed = _activity.PackageManager?.GetPackageInfo(_activity.PackageName!, 0);
            if (Version.TryParse(installed?.VersionName?.TrimStart('v', 'V'), out var version))
                return version;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
        }
        return new Version(0, 0, 0);
    }

    private void ShowDownload(PublishedApk apk, string targetEngine)
    {
        _activity.RunOnUiThread(() =>
            new AlertDialog.Builder(_activity)
                .SetTitle("Обновление SS14 Фобос готово!")
                .SetMessage(
                    $"Новая версия: {apk.Version}\n" +
                    $"Движок: {targetEngine}\n\n" +
                    "Нажми «Скачать APK», чтобы открыть именно новую сборку в GitHub Actions. " +
                    "После загрузки Android попросит подтвердить установку. " +
                    "Для скачивания артефактов GitHub может понадобиться вход в аккаунт.")
                .SetPositiveButton("Скачать APK", (_, _) => Open(apk.Url))
                .SetNeutralButton("Скопировать ссылку", (_, _) => CopyLink(apk.Url))
                .SetNegativeButton("Позже", (_, _) => { })
                .Show());
    }

    private void Open(string url)
    {
        try
        {
            _activity.StartActivity(
                new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
            CopyLink(url);
        }
    }

    private void CopyLink(string url)
    {
        var clipboard = (Android.Content.ClipboardManager?)
            _activity.GetSystemService(Context.ClipboardService);
        if (clipboard != null)
            clipboard.PrimaryClip = ClipData.NewPlainText("SS14 Фобос APK", url);
        Toast("Ссылка на APK скопирована");
    }

    private void Toast(string message) =>
        _activity.RunOnUiThread(() =>
            Toast.MakeText(_activity, message, ToastLength.Long)?.Show());
}
