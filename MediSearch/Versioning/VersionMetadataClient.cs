using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace MediSearch.Versioning;

public sealed class VersionMetadataClient(HttpClient httpClient, UpdateSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediSearch",
        "version-cache.json");

    public async Task<VersionInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        LastError = null;

        if (!settings.IsConfigured)
        {
            UpdateLogger.Info("Update check skipped because update-settings.json is not configured.");
            LastError = "Chưa cấu hình update-settings.json.";
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.VersionMetadataUrl);
            request.Headers.UserAgent.ParseAdd("MediSearch-Updater/1.0");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var versionInfo = await response.Content.ReadFromJsonAsync<VersionInfo>(JsonOptions, cancellationToken);
            if (versionInfo is not null)
            {
                WriteCache(versionInfo);
            }

            return versionInfo;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            LastError = ShortError(ex);
            UpdateLogger.Error(ex, "Unable to download latest version metadata");
            return TryReadFreshCache()
                ?? await TryReadLatestGitHubReleaseAsync(cancellationToken)
                ?? await TryReadLatestGitHubTagAsync(cancellationToken)
                ?? TryReadAnyCache();
        }
    }

    public string? LastError { get; private set; }

    private async Task<VersionInfo?> TryReadLatestGitHubReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.github.com/repos/{settings.Owner}/{settings.Repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("MediSearch-Updater/1.0");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(JsonOptions, cancellationToken);
            var version = release?.TagName?.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            version = version.TrimStart('v', 'V');
            var assetUrl = release?.Assets?
                .FirstOrDefault(asset => string.Equals(asset.Name, "MediSearch.zip", StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

            var versionInfo = new VersionInfo(
                version,
                AppVersion.Current.ToString(),
                string.IsNullOrWhiteSpace(assetUrl)
                    ? $"https://github.com/{settings.Owner}/{settings.Repo}/releases/latest/download/MediSearch.zip"
                    : assetUrl,
                string.IsNullOrWhiteSpace(release?.Body) ? settings.ReleaseNotes : release.Body);

            WriteCache(versionInfo);
            return versionInfo;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            LastError = ShortError(ex);
            UpdateLogger.Error(ex, "Unable to read latest GitHub release");
            return null;
        }
    }

    private async Task<VersionInfo?> TryReadLatestGitHubTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.github.com/repos/{settings.Owner}/{settings.Repo}/tags?per_page=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("MediSearch-Updater/1.0");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tags = await response.Content.ReadFromJsonAsync<List<GitHubTag>>(JsonOptions, cancellationToken);
            var version = tags?.FirstOrDefault()?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            version = version.TrimStart('v', 'V');
            var versionInfo = new VersionInfo(
                version,
                AppVersion.Current.ToString(),
                $"https://github.com/{settings.Owner}/{settings.Repo}/releases/latest/download/MediSearch.zip",
                settings.ReleaseNotes);

            WriteCache(versionInfo);
            return versionInfo;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            LastError = ShortError(ex);
            UpdateLogger.Error(ex, "Unable to read latest GitHub tag");
            return null;
        }
    }

    private static string ShortError(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpRequestException when httpRequestException.StatusCode is not null =>
                $"GitHub trả về {(int)httpRequestException.StatusCode} {httpRequestException.StatusCode}.",
            TaskCanceledException => "Kết nối GitHub quá thời gian chờ.",
            JsonException => "File version.json không đúng định dạng.",
            _ => exception.Message
        };
    }

    private VersionInfo? TryReadFreshCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
            if (age > TimeSpan.FromMinutes(Math.Max(settings.MetadataCacheMinutes, 1)))
            {
                return null;
            }

            return JsonSerializer.Deserialize<VersionInfo>(File.ReadAllText(_cachePath), JsonOptions);
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unable to read fresh version cache");
            return null;
        }
    }

    private VersionInfo? TryReadAnyCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<VersionInfo>(File.ReadAllText(_cachePath), JsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unable to read fallback version cache");
            return null;
        }
    }

    private void WriteCache(VersionInfo versionInfo)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(versionInfo, JsonOptions));
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unable to write version cache");
        }
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string? TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("body")] string? Body,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] List<GitHubReleaseAsset>? Assets);

    private sealed record GitHubReleaseAsset(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

    private sealed record GitHubTag(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name);
}
