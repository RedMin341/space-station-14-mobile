using System;
using System.Text.RegularExpressions;

namespace Content.Android;

internal static class StartupProgress
{
    public static string? FromLogLine(string line)
    {
        if (line.StartsWith("[SERIAL PROGRESS] ", StringComparison.Ordinal))
        {
            // Only completed/total counts extend the deadline, not elapsed time or memory.
            var end = line.IndexOf(';');
            return end < 0 ? null : line[..end];
        }
        return Regex.IsMatch(line, @"^\[(?:START|DONE|SERIAL START|SERIAL DONE)\] ") ? line : null;
    }
}

internal sealed class StartupWaitPolicy
{
    private string? _lastProgress;
    private TimeSpan _lastChange;
    public void Observe(string? progress, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(progress) || progress == _lastProgress) return;
        _lastProgress = progress;
        _lastChange = elapsed;
    }
    public string? TimeoutReason(TimeSpan elapsed)
    {
        if (elapsed >= TimeSpan.FromMinutes(30))
            return "Запуск превысил 30 минут. Скопируй журнал запуска комнаты.";
        if (elapsed - _lastChange >= TimeSpan.FromMinutes(5))
            return "Нет прогресса запуска уже 5 минут. Скопируй журнал запуска комнаты.";
        return null;
    }
}
