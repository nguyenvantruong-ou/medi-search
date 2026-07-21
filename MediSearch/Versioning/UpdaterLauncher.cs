using System.Diagnostics;
using System.IO;

namespace MediSearch.Versioning;

public static class UpdaterLauncher
{
    public static void Launch(string zipPath)
    {
        var updaterPath = Path.Combine(AppContext.BaseDirectory, "MediSearch.Updater.exe");
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("Updater executable was not found in the application directory.", updaterPath);
        }

        var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Unable to determine the current application executable path.");
        }

        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = $"--pid {Environment.ProcessId} --zip \"{zipPath}\" --target \"{targetDirectory}\" --exe \"{executablePath}\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }
}
