using System.Text.Json.Serialization;

namespace MediSearch.Versioning;

public sealed record VersionInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("minimumVersion")] string MinimumVersion,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("releaseNotes")] string ReleaseNotes);
