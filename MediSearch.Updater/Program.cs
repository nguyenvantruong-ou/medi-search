using System.Diagnostics;
using System.IO.Compression;

var arguments = ParsedArguments.Parse(args);
if (!arguments.TryGetInt("pid", out var processId) ||
    !arguments.TryGet("zip", out var zipPath) ||
    !arguments.TryGet("target", out var targetDirectory) ||
    !arguments.TryGet("exe", out var executablePath))
{
    Console.Error.WriteLine("Usage: MediSearch.Updater --pid <pid> --zip <path> --target <directory> --exe <path>");
    return 2;
}

try
{
    await WaitForApplicationExitAsync(processId);

    var stagingDirectory = Path.Combine(Path.GetTempPath(), "MediSearch", "staged", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stagingDirectory);
    ZipFile.ExtractToDirectory(zipPath, stagingDirectory, overwriteFiles: true);

    var sourceDirectory = FindPackageRoot(stagingDirectory);
    CopyDirectory(sourceDirectory, targetDirectory);

    Process.Start(new ProcessStartInfo
    {
        FileName = executablePath,
        WorkingDirectory = targetDirectory,
        UseShellExecute = true
    });

    TryDelete(Path.GetDirectoryName(zipPath));
    TryDelete(stagingDirectory);
    return 0;
}
catch (Exception ex)
{
    var logDirectory = Path.Combine(targetDirectory, "logs");
    Directory.CreateDirectory(logDirectory);
    await File.AppendAllTextAsync(
        Path.Combine(logDirectory, "updater.log"),
        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ex}{Environment.NewLine}");
    return 1;
}

static async Task WaitForApplicationExitAsync(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        if (!process.HasExited)
        {
            process.CloseMainWindow();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    catch (ArgumentException)
    {
        // The application has already exited.
    }
    catch (TimeoutException)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}

static string FindPackageRoot(string stagingDirectory)
{
    var directExe = Path.Combine(stagingDirectory, "MediSearch.exe");
    if (File.Exists(directExe))
    {
        return stagingDirectory;
    }

    var nested = Directory
        .EnumerateFiles(stagingDirectory, "MediSearch.exe", SearchOption.AllDirectories)
        .Select(Path.GetDirectoryName)
        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    return nested ?? stagingDirectory;
}

static void CopyDirectory(string sourceDirectory, string targetDirectory)
{
    Directory.CreateDirectory(targetDirectory);

    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDirectory, directory);
        Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDirectory, file);
        var destination = Path.Combine(targetDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}

static void TryDelete(string? path)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
        // Temporary files can be cleaned later if another process still has a handle.
    }
}

internal sealed class ParsedArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ParsedArguments Parse(string[] args)
    {
        var parsed = new ParsedArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                continue;
            }

            parsed._values[key[2..]] = args[++index];
        }

        return parsed;
    }

    public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value!);

    public bool TryGetInt(string key, out int value)
    {
        value = 0;
        return TryGet(key, out var rawValue) && int.TryParse(rawValue, out value);
    }
}
