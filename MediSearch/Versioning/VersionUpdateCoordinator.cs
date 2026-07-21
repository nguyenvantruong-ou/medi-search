using System.Net.Http;
using System.Windows;

namespace MediSearch.Versioning;

public sealed class VersionUpdateCoordinator
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<bool> EnsureApplicationCanStartAsync()
    {
        try
        {
            var result = await CheckForUpdateAsync(CancellationToken.None);
            if (result is null || result.Requirement != UpdateRequirement.Required)
            {
                return true;
            }

            var dialog = new UpdateDialog(result) { Owner = CurrentMainWindowOrNull() };
            var accepted = dialog.ShowDialog() == true;
            if (!accepted)
            {
                return result.Requirement != UpdateRequirement.Required;
            }

            var updateStarted = StartUpdate(result.VersionInfo);
            return !updateStarted;
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unexpected startup update check failure");
            MessageBox.Show(
                "Unable to check for updates. The application will continue to start.",
                "MediSearch Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        LastCheckError = null;
        var settings = UpdateConfiguration.Load();
        var metadataClient = new VersionMetadataClient(_httpClient, settings);
        var checker = new VersionChecker(metadataClient);
        var result = await checker.CheckAsync(cancellationToken);
        LastCheckError = checker.LastError;
        return result;
    }

    public string? LastCheckError { get; private set; }

    public bool StartUpdate(VersionInfo versionInfo)
    {
        var progressWindow = new UpdateProgressWindow(versionInfo.DownloadUrl, _httpClient)
        {
            Owner = CurrentMainWindowOrNull()
        };
        progressWindow.ShowDialog();
        return progressWindow.UpdateStarted;
    }

    private static Window? CurrentMainWindowOrNull()
    {
        return Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;
    }
}
