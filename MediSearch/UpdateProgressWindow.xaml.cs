using System.Net.Http;
using System.Windows;
using MediSearch.Versioning;

namespace MediSearch;

public partial class UpdateProgressWindow : Window
{
    private readonly string _downloadUrl;
    private readonly HttpClient _httpClient;

    public bool UpdateStarted { get; private set; }

    public UpdateProgressWindow(string downloadUrl, HttpClient httpClient)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
        _httpClient = httpClient;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = $"Downloaded {value:0}%";
            });

            var downloader = new UpdateDownloader(_httpClient);
            var zipPath = await downloader.DownloadAsync(_downloadUrl, progress, CancellationToken.None);

            StatusText.Text = "Download complete. Restarting into updater...";
            UpdaterLauncher.Launch(zipPath);
            UpdateStarted = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Update download failed");
            StatusText.Text = $"Update failed: {ex.Message}";
            CloseButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
