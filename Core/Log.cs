using System.IO;
using System.Text;

namespace MagentaBridge.Core;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

/// <summary>
/// Rotating file log. The original plug-in had none — every failure was silent
/// (teardown defect 2). Thread-safe; raises <see cref="Line"/> so the UI can tail it.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _directory = "";
    private static string _file = "";
    private static long _maxBytes = 5L * 1024 * 1024;
    private static int _maxFiles = 5;
    private static LogLevel _minimum = LogLevel.Info;

    /// <summary>Raised for every line written, already formatted.</summary>
    public static event Action<string>? Line;

    public static string Directory => _directory;

    public static void Configure(string directory, LogLevel minimum, long maxBytes, int maxFiles)
    {
        lock (Gate)
        {
            _directory = directory;
            _minimum = minimum;
            _maxBytes = Math.Clamp(maxBytes, 64 * 1024, 256L * 1024 * 1024);
            _maxFiles = Math.Clamp(maxFiles, 1, 50);
            System.IO.Directory.CreateDirectory(_directory);
            _file = Path.Combine(_directory, "magenta-bridge.log");
            RotateIfNeeded();
        }
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        if (level < _minimum) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";

        lock (Gate)
        {
            try
            {
                if (_file.Length == 0) return;
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                RotateIfNeeded();
            }
            catch
            {
                // Never let logging take the app down.
            }
        }

        try { Line?.Invoke(line); } catch { /* UI listener failure is not fatal */ }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (_file.Length == 0) return;
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length < _maxBytes) return;

            for (var i = _maxFiles - 1; i >= 1; i--)
            {
                var older = $"{_file}.{i}";
                var newer = i == 1 ? _file : $"{_file}.{i - 1}";
                if (!File.Exists(newer)) continue;
                if (File.Exists(older)) File.Delete(older);
                File.Move(newer, older);
            }
        }
        catch
        {
            // Rotation is best-effort.
        }
    }
}
