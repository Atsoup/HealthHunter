using System.Collections.Concurrent;
using System.Text;

namespace HealthHunter;

/// <summary>
/// Diagnostics log. Scanner, auto-find and the bar reader write plain-language lines here so a failed
/// run can be explained afterwards: what was scanned, how much memory couldn't be read, how many
/// candidates each value type kept, what the bar read at each step. Shown on the Diagnostics page and
/// also written to %AppData%\HealthHunter\logs so it can be copied out after the app is closed.
/// </summary>
public static class Diag
{
    private static readonly ConcurrentQueue<string> _lines = new();
    private static readonly object _fileLock = new();
    private const int MaxLines = 4000;
    private static string _file;

    /// <summary>Raised on whatever thread logged. UI subscribers must marshal to the UI thread.</summary>
    public static event Action<string> Logged;

    public static string Folder => Path.Combine(Path.GetDirectoryName(ProfileStore.Folder)!, "logs");
    public static string FilePath => _file ??= Path.Combine(Folder, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines && _lines.TryDequeue(out _)) { }
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { }
        try { Logged?.Invoke(line); } catch { }
    }

    public static string All()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.AppendLine(l);
        return sb.ToString();
    }

    public static void Clear()
    {
        while (_lines.TryDequeue(out _)) { }
    }

    public static string Mb(long bytes) => bytes > 0 && bytes < 1048576 ? $"{Math.Max(1, bytes / 1024.0):N0} KB" : $"{bytes / 1048576.0:N0} MB";
}
