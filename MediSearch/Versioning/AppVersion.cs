using System.Reflection;

namespace MediSearch.Versioning;

public static class AppVersion
{
    public static Version Current { get; } = ReadCurrentVersion();

    public static Version Normalize(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    private static Version ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];

        if (Version.TryParse(informational, out var version))
        {
            return Normalize(version);
        }

        return assembly.GetName().Version is { } assemblyVersion
            ? Normalize(assemblyVersion)
            : new Version(0, 0, 0);
    }
}
