using System.IO;
using System.Text;

namespace MediSearch.Versioning;

public static class UpdateLogger
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "updates.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception exception, string message) => Write("ERROR", $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {level} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }
}
