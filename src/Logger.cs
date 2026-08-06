// Coder CLI (.NET 11 port) — file + console logger. Ported from src/utils/logger.ts.

namespace Coder.Utils;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed class Logger
{
    private LogLevel _level = LogLevel.Info;
    private string? _filePath;

    public void SetLevel(LogLevel level) => _level = level;
    public LogLevel Level => _level;

    public string? FilePath => _filePath;

    public string InitFileLogger(string configDir)
    {
        try
        {
            Directory.CreateDirectory(configDir);
            _filePath = Path.Combine(configDir, "coder.log");
            File.WriteAllText(_filePath, $"[{DateTime.Now:O}] Coder CLI session started\n");
        }
        catch
        {
            _filePath = null;
        }
        return _filePath ?? "(no log file)";
    }

    private void Write(LogLevel lvl, string prefix, string message)
    {
        if (lvl < _level) return;
        if (_filePath != null)
        {
            try
            {
                File.AppendAllText(_filePath, $"[{DateTime.Now:O}] [{prefix}] {message}\n");
            }
            catch { /* ignore */ }
        }
        // Only warnings/errors go to stderr in the terminal to avoid polluting output.
        if (lvl >= LogLevel.Warn)
        {
            Console.Error.WriteLine($"[{prefix}] {message}");
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, "debug", message);
    public void Debug(string message, string detail) => Write(LogLevel.Debug, "debug", $"{message} {detail}");
    public void Info(string message) => Write(LogLevel.Info, "info", message);
    public void Warn(string message) => Write(LogLevel.Warn, "warn", message);
    public void Warn(string message, string detail) => Write(LogLevel.Warn, "warn", $"{message} {detail}");
    public void Error(string message) => Write(LogLevel.Error, "error", message);
    public void Error(string message, string detail) => Write(LogLevel.Error, "error", $"{message} {detail}");
}

public static class Log
{
    public static Logger Instance { get; } = new Logger();
}
