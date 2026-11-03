using System.IO;

namespace Switch2ProManager.Services;

/// <summary>
/// Appends the activity log to %LOCALAPPDATA%\Switch2ProManager\logs\app.log, line by line,
/// so it survives tray sessions and crashes. Rotated to app.old.log above 1 MB.
/// </summary>
public static class FileLog
{
    private const long MaxBytes = 1_000_000;

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switch2ProManager", "logs");
    public static readonly string FilePath = Path.Combine(Dir, "app.log");
    private static readonly object Gate = new();

    static FileLog()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
            Write($"--- session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        }
        catch { /* logging must never break the app */ }
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch { /* logging must never break the app */ }
    }
}
