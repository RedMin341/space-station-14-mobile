using System;
using Android.App;
using Android.Content;

namespace Content.Android;

/// <summary>
/// Optional mobile graphics / memory optimizations. Never imposes a fixed
/// 1 GiB heap cap or pre-allocates a gigabyte of RAM.
/// </summary>
internal static class AndroidPerformance
{
    internal const string LowMemoryKey = "low_memory_mode";
    // Reuse the old preference key so existing user selections are preserved.
    // It now controls shadows only: dynamic lights are always rendered.
    internal const string DisableLightingKey = "disable_dynamic_lighting";
    internal const string ShadowQualityKey = "shadow_quality";
    internal const string LightingQualityKey = "lighting_quality";
    internal const string FpsLimitKey = "fps_limit";
    private const string PreferencesName = "ss14_mobile_config";
    private const long MiB = 1024L * 1024L;
    private const long AutoLowMemoryLimit = 6L * 1024L * MiB;

    internal static bool LowMemoryMode(Context context)
    {
        try
        {
            var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            return prefs?.GetBoolean(LowMemoryKey, IsLowRamDevice(context)) ?? IsLowRamDevice(context);
        }
        catch { return IsLowRamDevice(context); }
    }

    // Four independent shadow presets. "off" skips shadow rendering
    // but does NOT remove the lights themselves or the visibility/FOV system.
    internal static string ShadowQuality(Context context)
    {
        try
        {
            var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            var value = prefs?.GetString(ShadowQualityKey, null);
            if (value is "off" or "low" or "medium" or "high")
                return value;

            // Migration from the old "disable dynamic shadows" checkbox.
            if (prefs?.Contains(DisableLightingKey) == true)
                return prefs.GetBoolean(DisableLightingKey, false) ? "off" : "medium";
        }
        catch { }
        return IsLowRamDevice(context) ? "low" : "medium";
    }

    internal static string LightingQuality(Context context)
    {
        try
        {
            var value = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?
                .GetString(LightingQualityKey, null);
            if (value is "low" or "medium" or "high")
                return value;
        }
        catch { }
        return IsLowRamDevice(context) ? "low" : "medium";
    }

    internal static int FpsLimit(Context context)
    {
        try
        {
            var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            var fps = prefs?.GetInt(FpsLimitKey, IsLowRamDevice(context) ? 30 : 60) ?? 60;
            if (fps is 30 or 45 or 60 or 90)
                return fps;
        }
        catch { }
        return IsLowRamDevice(context) ? 30 : 60;
    }

    private static bool IsLowRamDevice(Context context)
    {
        var total = TotalPhysicalMemory(context);
        return total > 0 && total <= AutoLowMemoryLimit;
    }

    private static long TotalPhysicalMemory(Context context)
    {
        try
        {
            var am = context.GetSystemService(Context.ActivityService) as ActivityManager;
            var info = new ActivityManager.MemoryInfo();
            am?.GetMemoryInfo(info);
            return info.TotalMem;
        }
        catch { return 0; }
    }

    internal static string DescribeMemory()
    {
        try
        {
            var context = Application.Context;
            var am = context.GetSystemService(Context.ActivityService) as ActivityManager;
            var info = new ActivityManager.MemoryInfo();
            am?.GetMemoryInfo(info);
            var gc = GC.GetGCMemoryInfo();
            var gcBudget = gc.TotalAvailableMemoryBytes;
            return $"physical_total={info.TotalMem / MiB}MiB; " +
                   $"system_available={info.AvailMem / MiB}MiB; " +
                   $"system_low_memory={info.LowMemory}; " +
                   $"system_low_threshold={info.Threshold / MiB}MiB; " +
                   $"gc_total_available_budget={(gcBudget > 0 ? (gcBudget / MiB).ToString() : "unknown")}MiB; " +
                   $"java_memory_class={am?.MemoryClass ?? 0}MiB; " +
                   $"java_large_memory_class={am?.LargeMemoryClass ?? 0}MiB";
        }
        catch (Exception e)
        {
            return "Android system memory information unavailable: " + e.GetType().Name;
        }
    }
}
