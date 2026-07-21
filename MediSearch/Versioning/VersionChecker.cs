namespace MediSearch.Versioning;

public sealed class VersionChecker(VersionMetadataClient metadataClient)
{
    public string? LastError => metadataClient.LastError;

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken)
    {
        var versionInfo = await metadataClient.GetLatestAsync(cancellationToken);
        if (versionInfo is null)
        {
            return null;
        }

        if (!Version.TryParse(versionInfo.Version, out var latestVersion) ||
            !Version.TryParse(versionInfo.MinimumVersion, out var minimumVersion))
        {
            UpdateLogger.Info($"Invalid version metadata received: latest={versionInfo.Version}, minimum={versionInfo.MinimumVersion}");
            return null;
        }

        var localVersion = AppVersion.Current;
        var requirement = VersionComparison.Compare(localVersion, latestVersion, minimumVersion);
        return new UpdateCheckResult(requirement, localVersion, AppVersion.Normalize(latestVersion), AppVersion.Normalize(minimumVersion), versionInfo);
    }
}
