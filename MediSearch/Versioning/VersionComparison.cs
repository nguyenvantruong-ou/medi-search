namespace MediSearch.Versioning;

public enum UpdateRequirement
{
    Current,
    Optional,
    Required
}

public static class VersionComparison
{
    public static UpdateRequirement Compare(Version localVersion, Version latestVersion, Version minimumVersion)
    {
        localVersion = AppVersion.Normalize(localVersion);
        latestVersion = AppVersion.Normalize(latestVersion);
        minimumVersion = AppVersion.Normalize(minimumVersion);

        if (localVersion < minimumVersion)
        {
            return UpdateRequirement.Required;
        }

        return localVersion < latestVersion
            ? UpdateRequirement.Optional
            : UpdateRequirement.Current;
    }
}
