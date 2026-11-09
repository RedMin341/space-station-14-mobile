using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Widget;
using Content.Android.Launcher.Models;

namespace Content.Android;

/// <summary>
/// Resource archives are downloaded by the normal launcher. For any server C#
/// content or engine update, find the newest *successfully built* matching APK,
/// not a stale, hardcoded GitHub Actions URL.
/// </summary>
internal sealed class FobosApkUpdater
{
    private const string Repository = "RedMin341/space-station-14-mobile";
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
    private int _busy;

    internal string StatusText { get; private set; } = "Проверка обновлений ещё не завершена.";
    internal string ReleaseNotes { get; private set; } =
        "Версия 0.1.14: добавлена вкладка «Изменения», проверка сборки Фобоса и ссылка на новый APK.";
    internal string? DownloadUrl { get; private set; }
    internal string InstalledVersion => GetInstalledVersion().ToString();
    internal string TargetEngine { get; private set; } = FobosBuild.Engine;
    internal string LatestVersion { get; private set; } = "Неизвестна";

    private sealed record PublishedApk(Version Version, string Engine, string Content, long RunId, long ArtifactId, string Notes)
    {
        public string Url => $"https://github.com/{Repository}/actions/runs/{RunId}/artifacts/{ArtifactId}";
    }

    internal FobosApkUpdater(LauncherActivity activity)
    {
        _activity = activity;
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SS14-Fobos-Mobile/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private void SetState(string status, string? notes = null, PublishedApk? apk = null)
    {
        StatusText = status;
        if (notes != null)
            ReleaseNotes = notes;
        DownloadUrl = apk?.Url;
        if (apk != null)
            LatestVersion = apk.Version.ToString();
        _activity.RunOnUiThread(() => _activity.RefreshUpdatesTab());
    }

    internal void OpenLatest() => Open(DownloadUrl ?? ActionsPage);

    /// <summary>
    /// GitHub APK checks must work even when the game server is offline or blocked.
    /// Server engine compatibility is separately enforced when connecting.
    /// </summary>
    internal async Task CheckAsync(bool manual = false, bool offerPopup = true)
    {
        SetState("Проверяем готовые обновления APK на GitHub…");
        await OfferAsync(null, null, manual, offerPopup);
    }

    /// <summary>Invoked by the connection screen on an incompatible C# build.</summary>
    internal Task OfferForServerAsync(ServerBuildInfo serverBuild) =>
        OfferAsync(serverBuild.EngineVersion, serverBuild.Version, manual: true);

    private async Task OfferAsync(string? targetEngine, string? targetContent, bool manual, bool offerPopup = true)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return;
        try
        {
            // Prefer the live engine when it is available from a connection,
            // but do not require the game server for checking Android APK releases.
            var apk = await FindLatestCompatibleAsync(targetEngine, targetContent);
            var local = GetInstalledVersion();
            TargetEngine = apk?.Engine ?? targetEngine ?? FobosBuild.Engine;
            var installedMatchesServer =
                (targetEngine == null || targetEngine == FobosBuild.Engine) &&
                (targetContent == null || string.Equals(targetContent, FobosBuild.ContentRevision,
                    StringComparison.OrdinalIgnoreCase));

            if (apk != null && apk.Version > local)
            {
                SetState($"Доступна новая версия Фобоса: {apk.Version} (установлена {local}).",
                    apk.Notes, apk);
                if (offerPopup)
                    ShowDownload(apk, apk.Engine);
            }
            else if (!installedMatchesServer)
            {
                SetState("Игровой код или движок Фобоса обновился. Совместимого APK пока нет.",
                    $"Движок: {targetEngine ?? "неизвестен"}. " +
                    $"Ревизия контента: {targetContent ?? "неизвестна"}. " +
                    "Старую APK запускать нельзя — дождись новой подписанной сборки.");
                if (offerPopup)
                    ShowMissingBuild(targetEngine, targetContent);
            }
            else if (apk == null)
            {
                SetState("GitHub доступен, но готовых APK Фобоса пока не найдено.",
                    "Проверь историю сборок или повтори проверку позже.");
            }
            else
            {
                SetState($"У вас последняя версия: {local}.",
                    "Обновлять APK не требуется.\n\n" +
                    "Изменения:\n" +
                    "• Вкладка «Изменения» с проверкой версий и ссылками на APK.\n" +
                    "• Изменения движка и игрового C#-кода собираются на GitHub.\n" +
                    "• Только ресурсы той же ревизии загружаются без переустановки.",
                    null);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
            SetState("Не удалось получить сведения об APK с GitHub. Проверь интернет или повтори проверку.");
            if (manual && offerPopup)
                _activity.RunOnUiThread(() =>
                    new AlertDialog.Builder(_activity)
                        .SetTitle("Не удалось проверить обновления")
                        .SetMessage("GitHub недоступен или ограничил запросы. Сборки можно открыть вручную.")
                        .SetPositiveButton("Сборки GitHub", (_, _) => Open(ActionsPage))
                        .SetNegativeButton("Закрыть", (_, _) => { })
                        .Show());
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<PublishedApk?> FindLatestCompatibleAsync(string? engineVersion, string? contentRevision)
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
            var contentMatch = Regex.Match(buildSource,
                "public const string ContentRevision\\s*=\\s*\"([0-9a-fA-F]{40})\"");
            if (!engineMatch.Success || !contentMatch.Success ||
                (engineVersion != null && engineMatch.Groups[1].Value != engineVersion) ||
                (contentRevision != null && !string.Equals(contentMatch.Groups[1].Value,
                    contentRevision, StringComparison.OrdinalIgnoreCase)))
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
                var commitMessage = "";
                if (run.TryGetProperty("head_commit", out var headCommit) &&
                    headCommit.ValueKind == JsonValueKind.Object &&
                    headCommit.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    commitMessage = message.GetString()?.Split('\n')[0]?.Trim() ?? "";
                var notes = version == new Version(0, 1, 14)
                    ? "• Новая вкладка «Изменения».\n" +
                      "• Уведомление «мяу! Пора ставить новый хром чума!» при выходе APK.\n" +
                      "• Кнопка ведёт на актуальный артефакт сборки GitHub.\n" +
                      "• Ресурсы сервера обновляются внутри приложения, движок через GitHub."
                    : version == new Version(0, 1, 15)
                    ? "• Тест обновления: версия приложения изменена с 0.1.14 на 0.1.15.\n" +
                      "• Движок и игровое управление не изменены."
                    : version == new Version(0, 1, 18)
                    ? "• Обновление контента частями: старые файлы повторно используются.\n" +
                      "• Скачиваются только недостающие ресурсы сервера.\n" +
                      "• Проверка целостности каждого файла BLAKE2b-256.\n" +
                      "• Исправлена полная повторная загрузка контента."
                    : version == new Version(0, 1, 16)
                    ? "• Исправлена проверка обновлений: GitHub проверяется без соединения с Фобосом.\n" +
                      "• Кнопка на вкладке «Изменения» открывает последнюю готовую APK-сборку."
                    : "• Обновлены C#-модули Фобоса (" + contentMatch.Groups[1].Value[..12] + ") и движок " + engineMatch.Groups[1].Value + ".\n" +
                      "• Сохранены управление Фобоса и подпись приложения.";
                if (!string.IsNullOrWhiteSpace(commitMessage))
                    notes += "\n• Из GitHub: " + commitMessage;
                var candidate = new PublishedApk(version, engineMatch.Groups[1].Value, contentMatch.Groups[1].Value,
                    runId, artifactIdElement.GetInt64(), notes);
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
            global::Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
        }
        return new Version(0, 0, 0);
    }

    private void ShowDownload(PublishedApk apk, string targetEngine)
    {
        _activity.RunOnUiThread(() =>
            new AlertDialog.Builder(_activity)
                .SetTitle("мяу! Пора ставить новый хром чума!")
                .SetMessage(
                    $"Новая версия: {apk.Version}\n" +
                    $"Движок: {targetEngine}\n\n" +
                    apk.Notes + "\n\n" +
                    "Нажми «Скачать обнову», чтобы открыть именно новую сборку в GitHub Actions. " +
                    "После загрузки Android попросит подтвердить установку. " +
                    "Для скачивания артефактов GitHub может понадобиться вход в аккаунт.")
                .SetPositiveButton("Скачать обнову", (_, _) => Open(apk.Url))
                .SetNeutralButton("Скопировать ссылку", (_, _) => CopyLink(apk.Url))
                .SetNegativeButton("Позже", (_, _) => { })
                .Show());
    }

    private void ShowMissingBuild(string? serverEngine, string? serverContent)
    {
        _activity.RunOnUiThread(() =>
            new AlertDialog.Builder(_activity)
                .SetTitle("Фобос обновился — требуется новая APK")
                .SetMessage(
                    "Код на сервере не совпадает с кодом приложения. " +
                    "Запуск остановлен, чтобы игра не вылетела при загрузке прототипов.\n\n" +
                    $"Движок сервера: {serverEngine ?? "неизвестен"}\n" +
                    $"Контент сервера: {serverContent ?? "неизвестен"}\n\n" +
                    "Автосборка должна выпустить новую подписанную APK. " +
                    "Проверь готовые сборки на GitHub через некоторое время.")
                .SetPositiveButton("Сборки GitHub", (_, _) => Open(ActionsPage))
                .SetNegativeButton("Закрыть", (_, _) => { })
                .Show());
    }

    private void Open(string url)
    {
        try
        {
            _activity.StartActivity(
                new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FobosApkUpdater", ex.ToString());
            CopyLink(url);
        }
    }

    private void CopyLink(string url)
    {
        var clipboard = (global::Android.Content.ClipboardManager?)
            _activity.GetSystemService(global::Android.Content.Context.ClipboardService);
        if (clipboard != null)
            clipboard.PrimaryClip = ClipData.NewPlainText("SS14 Фобос APK", url);
        ShowToast("Ссылка на APK скопирована");
    }

    private void ShowToast(string message) =>
        _activity.RunOnUiThread(() =>
            global::Android.Widget.Toast.MakeText(_activity, message, global::Android.Widget.ToastLength.Long)?.Show());
}
