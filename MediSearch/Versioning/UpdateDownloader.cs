using System.IO;
using System.Net.Http;

namespace MediSearch.Versioning;

public sealed class UpdateDownloader(HttpClient httpClient)
{
    public async Task<string> DownloadAsync(
        string downloadUrl,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var downloadDirectory = Path.Combine(Path.GetTempPath(), "MediSearch", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDirectory);
        var zipPath = Path.Combine(downloadDirectory, "MediSearch.zip");

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("MediSearch-Updater/1.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(zipPath);

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;
            if (totalBytes is > 0)
            {
                progress.Report(downloadedBytes * 100d / totalBytes.Value);
            }
        }

        if (new FileInfo(zipPath).Length == 0)
        {
            throw new InvalidOperationException("The update download completed but the ZIP file is empty.");
        }

        progress.Report(100);
        return zipPath;
    }
}
