namespace MediSearch.Versioning;

public sealed record UpdateCheckResult(
    UpdateRequirement Requirement,
    Version LocalVersion,
    Version LatestVersion,
    Version MinimumVersion,
    VersionInfo VersionInfo);
