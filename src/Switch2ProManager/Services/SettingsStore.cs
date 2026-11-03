using System.IO;
using System.Text.Json;
using Switch2ProManager.Models;

namespace Switch2ProManager.Services;

/// <summary>
/// The controller options the user saves under a name (docs/UI_SPEC.md, section 9).
/// Start with Windows, auto-reconnect, language, tray behaviour and DSU are global, not part of a profile.
/// </summary>
public sealed class Profile
{
    public const string DefaultName = "Default";
    public const string LegacyDefaultName = "Predeterminado";   // name used before the default was renamed to Default

    public string Name { get; set; } = DefaultName;

    public bool VirtualPadEnabled { get; set; } = true;
    public bool PositionalLayout { get; set; } = true;
    public bool GameRumbleEnabled { get; set; } = true;
    public double RumbleStrength { get; set; } = 100;   // percent
    public bool MotionEnabled { get; set; }

    // Mapping (docs/UI_SPEC.md, sections 6.1 and 6.2)
    public StickSettings LeftStick { get; set; } = new();
    public StickSettings RightStick { get; set; } = new();
    public List<ButtonBinding> Buttons { get; set; } = ButtonBinding.Defaults();
}

/// <summary>
/// Everything the user configures. Written when the app exits and on every profile change.
/// Test tools (rumble lab, audio probe) are never restored.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bump when a setting changes meaning, and convert old values when loading.
    /// Version 2 adds Language and profiles; a version 1 file is migrated (see SettingsStore).</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;

    public string Language { get; set; } = "en";

    // Global settings
    public bool AutoReconnect { get; set; }
    public bool DsuServerEnabled { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayNotifications { get; set; } = true;

    // Profiles
    public List<Profile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = Profile.DefaultName;
}

/// <summary>
/// Settings in %LOCALAPPDATA%\Switch2ProManager\settings.json. A missing or unreadable file falls back to the defaults.
/// </summary>
public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switch2ProManager");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly string V1BackupPath = Path.Combine(Folder, "settings.v1.json.bak");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(nameof(AppSettings.Profiles), out _))
                    return Normalize(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
                return MigrateFromV1(json);
            }
        }
        catch (Exception ex) { FileLog.Write($"Settings not loaded, using defaults: {ex.Message}"); }
        return Normalize(new AppSettings());
    }

    // Version 1 kept the profile options at the top level. They become the Predeterminado profile.
    private static AppSettings MigrateFromV1(string json)
    {
        if (!File.Exists(V1BackupPath)) File.Copy(FilePath, V1BackupPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        var profile = JsonSerializer.Deserialize<Profile>(json) ?? new Profile();
        profile.Name = Profile.DefaultName;
        settings.Profiles = new List<Profile> { profile };
        settings.ActiveProfile = Profile.DefaultName;
        FileLog.Write("Settings migrated from version 1 to version 2.");
        return Normalize(settings);
    }

    // The default profile always exists, names are unique, and the active profile must exist.
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Profiles = settings.Profiles
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name).Select(g => g.First()).ToList();
        if (!settings.Profiles.Any(p => p.Name == Profile.DefaultName))
            foreach (var p in settings.Profiles.Where(p => p.Name == Profile.LegacyDefaultName)) p.Name = Profile.DefaultName;
        if (settings.ActiveProfile == Profile.LegacyDefaultName) settings.ActiveProfile = Profile.DefaultName;
        foreach (var p in settings.Profiles)
        {
            p.Buttons ??= ButtonBinding.Defaults();
            foreach (var b in p.Buttons.Where(b => b.Key != 0 && b.Keys.Count == 0))
            {
                b.Keys = new List<int> { b.Key };   // one key of version 1 becomes a one-key combination
                b.Key = 0;
            }
        }
        if (!settings.Profiles.Any(p => p.Name == Profile.DefaultName)) settings.Profiles.Insert(0, new Profile());
        if (!settings.Profiles.Any(p => p.Name == settings.ActiveProfile)) settings.ActiveProfile = Profile.DefaultName;
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { FileLog.Write($"Settings not saved: {ex.Message}"); }
    }
}
