using System;
using Content.Android.Launcher.Models;

namespace Content.Android;

internal static class FobosBuild
{
    public const string Engine = "276.0.6";
    public const string ContentRevision = "28e2afab9ed20a3af35b177eaef318dae1e81fff";
    public const string Fork = "dsfobos";

    // Content.Client and Content.Shared are compiled into this APK. A server
    // revision can add C# enums and components without updating RobustToolbox.
    // Only resources may be downloaded independently of this exact revision.
    public static bool Matches(ServerBuildInfo? build) => build != null
        && string.Equals(build.EngineVersion, Engine, StringComparison.Ordinal)
        && string.Equals(build.ForkId, Fork, StringComparison.Ordinal)
        && string.Equals(build.Version, ContentRevision, StringComparison.OrdinalIgnoreCase);

    public static string DescribeMismatch(ServerBuildInfo? build)
    {
        if (build == null)
            return "Сервер не сообщил версию сборки. Запуск небезопасен.";
        if (!string.Equals(build.ForkId, Fork, StringComparison.Ordinal))
            return "Это не сервер SS14 Фобос.";
        if (!string.Equals(build.EngineVersion, Engine, StringComparison.Ordinal))
            return $"Движок сервера: {build.EngineVersion ?? "неизвестен"}; в APK: {Engine}. Требуется новая версия APK.";
        if (!string.Equals(build.Version, ContentRevision, StringComparison.OrdinalIgnoreCase))
            return $"Игровой код сервера обновился ({build.Version ?? "неизвестен"}). " +
                   $"В APK встроен более старый код ({ContentRevision}). Требуется новая версия APK.";
        return "Сборка совместима.";
    }
}
