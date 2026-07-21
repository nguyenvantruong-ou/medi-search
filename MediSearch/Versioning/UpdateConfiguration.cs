using System.IO;
using System.Text.Json;

namespace MediSearch.Versioning;

public static class UpdateConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static UpdateSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "update-settings.json");
        if (!File.Exists(path))
        {
            return new UpdateSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), JsonOptions) ?? new UpdateSettings();
            return Normalize(settings);
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unable to read update settings");
            return new UpdateSettings();
        }
    }

    private static UpdateSettings Normalize(UpdateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Owner) ||
            settings.Owner.Contains("YOUR_GITHUB_OWNER", StringComparison.OrdinalIgnoreCase))
        {
            settings.Owner = UpdateSettings.DefaultOwner;
        }

        if (string.IsNullOrWhiteSpace(settings.Repo) ||
            settings.Repo.Contains("YOUR_GITHUB_REPO", StringComparison.OrdinalIgnoreCase))
        {
            settings.Repo = UpdateSettings.DefaultRepo;
        }

        if (string.IsNullOrWhiteSpace(settings.VersionMetadataUrl) ||
            settings.VersionMetadataUrl.Contains("YOUR_GITHUB_OWNER", StringComparison.OrdinalIgnoreCase) ||
            settings.VersionMetadataUrl.Contains("YOUR_GITHUB_REPO", StringComparison.OrdinalIgnoreCase))
        {
            settings.VersionMetadataUrl = $"https://github.com/{settings.Owner}/{settings.Repo}/releases/latest/download/version.json";
        }

        return settings;
    }
}
