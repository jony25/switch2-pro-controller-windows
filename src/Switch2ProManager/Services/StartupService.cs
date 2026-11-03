using Microsoft.Win32;

namespace Switch2ProManager.Services;

/// <summary>
/// Starts the app with Windows through HKCU\...\Run (per user, no administrator rights needed).
/// The entry starts the app with <see cref="MinimizedArg"/> so it begins in the notification area.
/// </summary>
public static class StartupService
{
    public const string MinimizedArg = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Switch2ProManager";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    private static void Enable()
    {
        // Under "dotnet run" the process is dotnet.exe, which must not be registered.
        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith("Switch2ProManager.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start with Windows needs the built exe (scripts\\publish.bat), not dotnet run.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}", RegistryValueKind.String);
    }

    private static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
