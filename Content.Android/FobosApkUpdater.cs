using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Widget;
using Uri = Android.Net.Uri;
using Path = System.IO.Path;

namespace Content.Android;

/// <summary>
/// Checks ONLY published SS14 Fobos releases. The content cache still downloads
/// normally from the server, so an APK is required only for engine/client updates.
/// </summary>
internal sealed class FobosApkUpdater
{
    private const string ReleasesApi =
        "https://api.github.com/repos/RedMin341/space-station-14-mobile/releases?per_page=30";
    // SHA256 of the existing *Fobos* signing certificate, not the upstream SS14 APK.
    private const string CertificateSha256 =
        "E12FB4788853DBD2E99EB37A219C903E3984AAD952B3FD022EF9D2C8FB0BC78A";
    private const string Mime = "application/vnd.android.package-archive";
    private const long MaxSize = 500L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(12)
    };

    private readonly LauncherActivity _activity;
    private int _working;
    private string? _readyApk;

    private sealed record Release(Version Version, string Url, long Size, string? Sha256);

    internal FobosApkUpdater(LauncherActivity activity) => _activity = activity;

    internal async Task CheckAsync(bool manual = false)
    {
        if (Interlocked.Exchange(ref _working, 1) != 0)
            return;

        try
        {
            _activity.SetUpdateButtonStatus("Проверка…");
            var latest = await FindLatestReleaseAsync();
            if (latest == null)
            {
                if (manual)
                    ToastOnUi("Обновлений Фобоса пока нет");
                return;
            }

            var installed = InstalledVersion();
            if (latest.Version.CompareTo(installed) <= 0)
            {
                if (manual)
                    ToastOnUi("Установлена последняя версия Фобоса: " + installed);
                return;
            }

            var downloads = Path.Combine(_activity.FilesDir?.AbsolutePath ??
                throw new IOException("Не найден каталог приложения"), "updates");
            Directory.CreateDirectory(downloads);
            var candidate = Path.Combine(downloads, "FobosUpdate.apk");

            if (File.Exists(candidate))
            {
                try
                {
                    ValidatePackage(candidate, latest);
                    _readyApk = candidate;
                    ShowInstallDialog(latest.Version);
                    return;
                }
                catch
                {
                    File.Delete(candidate);
                }
            }

            // An updated APK downloads inside this application. No GitHub browser or
            // manually looking up artifacts; Android still asks before installing.
            _activity.SetUpdateButtonStatus("Загрузка 0%");
            await DownloadAsync(latest, candidate);
            ValidatePackage(candidate, latest);
            _readyApk = candidate;
            ShowInstallDialog(latest.Version);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FobosUpdater", ex.ToString());
            if (manual)
                ShowError(ex.Message);
            else
                ToastOnUi("Проверка обновлений Фобоса не удалась");
        }
        finally
        {
            _activity.SetUpdateButtonStatus(_readyApk != null ? "Установить" : "Обновить");
            Interlocked.Exchange(ref _working, 0);
        }
    }

    internal void InstallReadyOrCheck()
    {
        if (_readyApk != null && File.Exists(_readyApk))
        {
            try
            {
                // Validate again before invoking Android's installer.
                ValidatePackage(_readyApk, null);
                Install(_readyApk);
                return;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return;
            }
        }

        _ = CheckAsync(manual: true);
    }

    private Version InstalledVersion()
    {
        var version = _activity.PackageManager?.GetPackageInfo(_activity.PackageName!, 0)?.VersionName;
        return Version.TryParse(version?.TrimStart('v', 'V'), out var parsed)
            ? parsed
            : new Version(0, 0, 0);
    }

    private async Task<Release?> FindLatestReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("SS14-Fobos-Android-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Release? result = null;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;
            if (item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                continue;
            var tag = item.GetProperty("tag_name").GetString();
            if (tag == null || !tag.StartsWith("fobos-v", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Version.TryParse(tag.Substring("fobos-v".Length), out var version))
                continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl) ||
                    !nameEl.GetString()!.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
                    parsedUrl.Scheme != "https" ||
                    parsedUrl.Host != "github.com")
                    continue;
                var size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > MaxSize)
                    continue;
                string? checksum = null;
                if (asset.TryGetProperty("digest", out var digestEl) &&
                    digestEl.ValueKind == JsonValueKind.String)
                {
                    var digest = digestEl.GetString();
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        checksum = digest.Substring("sha256:".Length);
                }

                var candidate = new Release(version, url!, size, checksum);
                if (result == null || candidate.Version.CompareTo(result.Version) > 0)
                    result = candidate;
            }
        }

        return result;
    }

    private async Task DownloadAsync(Release release, string destination)
    {
        var temporary = destination + ".part";
        if (File.Exists(temporary))
            File.Delete(temporary);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, release.Url);
            req.Headers.UserAgent.ParseAdd("SS14-Fobos-Android-Updater/1.0");
            using var response = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long len &&
                (len > MaxSize || len != release.Size))
                throw new IOException("Размер APK отличается от опубликованного");

            using var source = await response.Content.ReadAsStreamAsync();
            await using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                int lastPercent = -1;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxSize)
                        throw new IOException("APK превышает допустимый размер");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    var percent = (int)Math.Min(100, total * 100 / release.Size);
                    if (percent != lastPercent && percent % 5 == 0)
                    {
                        _activity.SetUpdateButtonStatus("Загрузка " + percent + "%");
                        lastPercent = percent;
                    }
                }
                if (total != release.Size)
                    throw new IOException("Загрузка APK завершилась раньше времени");
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                await using var file = File.OpenRead(temporary);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма APK не совпадает");
            }
            // Validate before publishing the download into the visible update slot.
            ValidatePackage(temporary, release);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void ValidatePackage(string filename, Release? release)
    {
        var pm = _activity.PackageManager ?? throw new IOException("Android PackageManager недоступен");
        var flags = Build.VERSION.SdkInt >= BuildVersionCodes.P
            ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        var archive = pm.GetPackageArchiveInfo(filename, flags)
            ?? throw new IOException("Загруженный APK не распознан Android");

        if (!string.Equals(archive.PackageName, _activity.PackageName, StringComparison.Ordinal))
            throw new IOException("Это APK другого приложения, установка заблокирована");
        var installedCode = pm.GetPackageInfo(_activity.PackageName!, 0)?.VersionCode ?? 0;
        if (archive.VersionCode <= installedCode)
            throw new IOException("APK старее или совпадает с установленным");

        if (release != null && !Version.TryParse(archive.VersionName, out var archiveVersion))
            throw new IOException("В APK отсутствует версия приложения");
        if (release != null && archiveVersion!.CompareTo(release.Version) != 0)
            throw new IOException("Версия APK отличается от опубликованной");

        Android.Content.PM.Signature[]? signatures = Build.VERSION.SdkInt >= BuildVersionCodes.P
            ? archive.SigningInfo?.GetApkContentsSigners()
            : archive.Signatures;
        if (signatures is not { Length: 1 })
            throw new IOException("У APK нет ожидаемой цифровой подписи");

        var digest = Convert.ToHexString(SHA256.HashData(signatures[0].ToByteArray()));
        if (!digest.Equals(CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Цифровая подпись не совпадает с исходной подписью Фобоса");
    }

    private void ShowInstallDialog(Version version)
    {
        _activity.RunOnUiThread(() =>
        {
            new AlertDialog.Builder(_activity)
                .SetTitle("SS14 Фобос: обновление готово")
                .SetMessage($"Версия {version} скачана и проверена. Установить поверх текущего Фобоса? Настройки останутся на месте.")
                .SetPositiveButton("Установить", (_, _) => InstallReadyOrCheck())
                .SetNegativeButton("Позже", (_, _) => { })
                .Show();
        });
    }

    private void Install(string filename)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O &&
            _activity.PackageManager?.CanRequestPackageInstalls() == false)
        {
            _activity.RunOnUiThread(() =>
                new AlertDialog.Builder(_activity)
                    .SetTitle("Разрешить обновления Фобоса")
                    .SetMessage("Разреши установку APK из этого приложения, затем вернись и нажми «Установить». Android всё равно попросит подтверждение установки.")
                    .SetPositiveButton("Настройки", (_, _) =>
                    {
                        var permission = new Intent(Settings.ActionManageUnknownAppSources,
                            Uri.Parse("package:" + _activity.PackageName));
                        _activity.StartActivity(permission);
                    })
                    .SetNegativeButton("Отмена", (_, _) => { })
                    .Show());
            return;
        }

        var content = Uri.Parse("content://" + _activity.PackageName + ".updates/latest.apk");
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(content, Mime);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        _activity.StartActivity(intent);
    }

    private void ToastOnUi(string message)
        => _activity.RunOnUiThread(() =>
            Toast.MakeText(_activity, message, ToastLength.Long)?.Show());

    private void ShowError(string message)
        => _activity.RunOnUiThread(() =>
            new AlertDialog.Builder(_activity)
                .SetTitle("Обновление Фобоса")
                .SetMessage(message)
                .SetPositiveButton("Закрыть", (_, _) => { })
                .Show());
}
