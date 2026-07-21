namespace MediSearch.Versioning;

public sealed class UpdateSettings
{
    public const string DefaultOwner = "nguyenvantruong-ou";
    public const string DefaultRepo = "medi-search";

    public string Owner { get; set; } = DefaultOwner;
    public string Repo { get; set; } = DefaultRepo;
    public string VersionMetadataUrl { get; set; } = $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases/latest/download/version.json";
    public int MetadataCacheMinutes { get; set; } = 60;
    public string ReleaseNotes { get; set; } = "Bug fixes and performance improvements.";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(VersionMetadataUrl) &&
        !VersionMetadataUrl.Contains("YOUR_GITHUB_OWNER", StringComparison.OrdinalIgnoreCase) &&
        !VersionMetadataUrl.Contains("YOUR_GITHUB_REPO", StringComparison.OrdinalIgnoreCase);
}
