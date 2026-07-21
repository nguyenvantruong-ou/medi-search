using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using MediSearch.Versioning;

namespace MediSearch;

public partial class MainWindow : Window
{
    private const string RequiredExtractorVersion = "image-price-screenshot-v5";
    private readonly HttpClient _httpClient = new();
    private Process? _backendProcess;
    private UpdateCheckResult? _availableUpdate;
    private readonly string _providerUrlConfigPath = FindProviderUrlConfigPath();
    private readonly List<string> _urls =
    [
        "https://example.com/search?q={keyword}"
    ];

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppVersion.Current}";
        LoadProviderUrls();
        RenderUrls();
        SetApiStatus("API chưa kiểm tra");
        ProviderStatusList.Items.Add("Sẵn sàng. API nền sẽ tự khởi động khi bạn tìm kiếm.");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ShowOptionalUpdateToastAsync();
    }

    private async Task ShowOptionalUpdateToastAsync()
    {
        try
        {
            UpdateStatusText.Text = "Đang kiểm tra cập nhật...";
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateNowButton.IsEnabled = false;
            CheckVersionButton.IsEnabled = false;

            var coordinator = new VersionUpdateCoordinator();
            var result = await coordinator.CheckForUpdateAsync(CancellationToken.None);
            if (result?.Requirement != UpdateRequirement.Optional)
            {
                UpdateStatusText.Visibility = Visibility.Collapsed;
                UpdateNowButton.Content = "Kiểm tra cập nhật";
                UpdateNowButton.IsEnabled = true;
                UpdateNowButton.Visibility = Visibility.Collapsed;
                CheckVersionButton.IsEnabled = true;
                return;
            }

            _availableUpdate = result;
            UpdateStatusText.Text = $"Có bản mới {result.LatestVersion}";
            UpdateNowButton.Content = $"Cập nhật {result.LatestVersion}";
            UpdateNowButton.IsEnabled = true;
            UpdateNowButton.Visibility = Visibility.Visible;
            CheckVersionButton.IsEnabled = true;

            var toast = new UpdateToastWindow(result)
            {
                Owner = this
            };
            toast.Show();
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Unable to show optional update toast");
            UpdateStatusText.Visibility = Visibility.Collapsed;
            UpdateNowButton.Content = "Kiểm tra cập nhật";
            UpdateNowButton.IsEnabled = true;
            UpdateNowButton.Visibility = Visibility.Collapsed;
            CheckVersionButton.IsEnabled = true;
        }
    }

    private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            await CheckUpdateFromButtonAsync();
            return;
        }

        UpdateNowButton.IsEnabled = false;
        UpdateNowButton.Content = "Đang cập nhật...";
        var coordinator = new VersionUpdateCoordinator();
        coordinator.StartUpdate(_availableUpdate.VersionInfo);
        UpdateNowButton.IsEnabled = true;
        UpdateNowButton.Content = $"Cập nhật {_availableUpdate.LatestVersion}";
    }

    private async void CheckVersionButton_Click(object sender, RoutedEventArgs e)
    {
        _availableUpdate = null;
        await CheckUpdateFromButtonAsync();
    }

    private async Task CheckUpdateFromButtonAsync()
    {
        UpdateNowButton.IsEnabled = false;
        CheckVersionButton.IsEnabled = false;
        UpdateNowButton.Content = "Đang kiểm tra...";
        UpdateStatusText.Text = "Đang kiểm tra cập nhật...";
        UpdateStatusText.Visibility = Visibility.Visible;

        try
        {
            var coordinator = new VersionUpdateCoordinator();
            var result = await coordinator.CheckForUpdateAsync(CancellationToken.None);
            if (result is null)
            {
                UpdateStatusText.Text = string.IsNullOrWhiteSpace(coordinator.LastCheckError)
                    ? "Không kiểm tra được phiên bản mới trên GitHub"
                    : $"Không kiểm tra được phiên bản mới: {coordinator.LastCheckError}";
                UpdateNowButton.Content = "Kiểm tra lại";
                return;
            }

            if (result.Requirement == UpdateRequirement.Current)
            {
                UpdateStatusText.Text = $"Đã là bản mới nhất ({result.LocalVersion})";
                UpdateNowButton.Content = "Kiểm tra lại";
                return;
            }

            _availableUpdate = result;
            UpdateStatusText.Text = $"Có bản mới {result.LatestVersion}";
            UpdateNowButton.Content = $"Cập nhật {result.LatestVersion}";
            UpdateNowButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateLogger.Error(ex, "Manual update check failed");
            UpdateStatusText.Text = $"Không kiểm tra được: {ex.Message}";
            UpdateNowButton.Content = "Kiểm tra lại";
        }
        finally
        {
            UpdateNowButton.IsEnabled = true;
            CheckVersionButton.IsEnabled = true;
        }
    }

    private void AddUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("Nhập URL tùy chỉnh trước khi thêm.");
            return;
        }

        if (_urls.Any(existing => string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)))
        {
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("URL này đã có trong danh sách.");
            return;
        }

        _urls.Add(url);
        UrlBox.Text = "";
        SaveProviderUrls();
        RenderUrls();
        ProviderStatusList.Items.Clear();
        ProviderStatusList.Items.Add($"Đã thêm URL. Hiện có {_urls.Count} URL dùng để tìm kiếm.");
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SearchButton.IsEnabled == false)
        {
            return;
        }

        e.Handled = true;
        await SearchAsync();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AddUrlButton_Click(sender, e);
    }

    private async Task SearchAsync()
    {
        var keyword = KeywordBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            MessageBox.Show("Nhập tên thuốc hoặc hoạt chất cần tìm.", "Medicine Quick Search", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SearchButton.IsEnabled = false;
        SearchButton.Content = "Đang tìm...";
        ProviderStatusList.Items.Clear();
        ResultsList.ItemsSource = null;
        ResultCountText.Text = "";
        ProviderStatusList.Items.Add("Đang gọi dịch vụ tìm kiếm Playwright...");

        try
        {
            var apiBase = ApiBaseBox.Text.Trim().TrimEnd('/');
            SetApiStatus("Đang kiểm tra API...");
            await EnsureBackendIsRunningAsync(apiBase);
            SetApiStatus("API sẵn sàng");
            var response = await _httpClient.PostAsJsonAsync($"{apiBase}/api/search", new SearchRequest(keyword, _urls));

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {error}");
            }

            var payload = await response.Content.ReadFromJsonAsync<SearchResponse>();
            ProviderStatusList.Items.Clear();

            foreach (var provider in payload?.Providers ?? [])
            {
                ProviderStatusList.Items.Add($"{provider.Provider} - {TranslateStatus(provider.Status)} - {provider.ResultCount} kết quả - {provider.ElapsedMs} ms");
            }

            var results = NormalizeResultUrls(payload?.Results ?? [], apiBase);
            ResultsList.ItemsSource = results;
            ResultCountText.Text = $"{results.Count} kết quả";

            if (OpenWebCheck.IsChecked == true)
            {
                Process.Start(new ProcessStartInfo(apiBase) { UseShellExecute = true });
            }

            await RefreshLogsAsync(apiBase);
        }
        catch (Exception ex)
        {
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("Tìm kiếm thất bại.");
            ProviderStatusList.Items.Add(ex.Message);
            SetApiStatus($"API lỗi: {ex.Message}");
            await RefreshLogsAsync(ApiBaseBox.Text.Trim().TrimEnd('/'));
            MessageBox.Show(
                "Tìm kiếm chưa chạy được.\n\n" +
                "Nếu đây là lần đầu chạy, Playwright có thể cần cài Chromium:\n" +
                "powershell -ExecutionPolicy Bypass -File .\\MedicineQuickSearch\\bin\\Debug\\net8.0\\playwright.ps1 install chromium\n\n" +
                ex.Message,
                "Medicine Quick Search",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SearchButton.IsEnabled = true;
            SearchButton.Content = "Tìm kiếm";
        }
    }

    private async void StopBackendButton_Click(object sender, RoutedEventArgs e)
    {
        StopBackendButton.IsEnabled = false;
        RestartBackendButton.IsEnabled = false;
        try
        {
            var apiBase = ApiBaseBox.Text.Trim().TrimEnd('/');
            SetApiStatus("Đang dừng API...");
            await StopBackendAsync(apiBase);
            SetApiStatus("API đã dừng");
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("Đã dừng API nền.");
        }
        catch (Exception ex)
        {
            SetApiStatus($"API lỗi: {ex.Message}");
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add($"Không dừng được API: {ex.Message}");
        }
        finally
        {
            StopBackendButton.IsEnabled = true;
            RestartBackendButton.IsEnabled = true;
        }
    }

    private async void RestartBackendButton_Click(object sender, RoutedEventArgs e)
    {
        StopBackendButton.IsEnabled = false;
        RestartBackendButton.IsEnabled = false;
        try
        {
            var apiBase = ApiBaseBox.Text.Trim().TrimEnd('/');
            SetApiStatus("Đang khởi động lại API...");
            await StopBackendAsync(apiBase);
            await EnsureBackendIsRunningAsync(apiBase);
            SetApiStatus("API sẵn sàng");
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("Đã khởi động lại API nền.");
        }
        catch (Exception ex)
        {
            SetApiStatus($"API lỗi: {ex.Message}");
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add($"Không khởi động lại được API: {ex.Message}");
        }
        finally
        {
            StopBackendButton.IsEnabled = true;
            RestartBackendButton.IsEnabled = true;
        }
    }

    private void RenderUrls()
    {
        UrlList.ItemsSource = null;
        UrlList.ItemsSource = _urls;
    }

    private void SetApiStatus(string message)
    {
        ApiStatusText.Text = message;
    }

    private static List<MedicineResult> NormalizeResultUrls(List<MedicineResult> results, string apiBase)
    {
        return results
            .Select(result => result with
            {
                ScreenshotUrl = BuildApiUrl(result.ScreenshotUrl, apiBase)
            })
            .ToList();
    }

    private static string? BuildApiUrl(string? value, string apiBase)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        return $"{apiBase.TrimEnd('/')}/{value.TrimStart('/')}";
    }

    private void RemoveUrlMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu contextMenu } ||
            contextMenu.PlacementTarget is not FrameworkElement { DataContext: string url })
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Bạn có chắc muốn xóa URL này khỏi danh sách tìm kiếm không?\n\n{url}",
            "Medicine Quick Search",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _urls.RemoveAll(existing => string.Equals(existing, url, StringComparison.OrdinalIgnoreCase));
        SaveProviderUrls();
        RenderUrls();
        ProviderStatusList.Items.Clear();
        ProviderStatusList.Items.Add($"Đã xóa URL. Còn {_urls.Count} URL dùng để tìm kiếm.");
    }

    private void LoadProviderUrls()
    {
        try
        {
            if (!File.Exists(_providerUrlConfigPath))
            {
                SaveProviderUrls();
                return;
            }

            var urls = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_providerUrlConfigPath));
            var cleanUrls = urls?
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleanUrls is { Count: > 0 })
            {
                _urls.Clear();
                _urls.AddRange(cleanUrls);
            }
        }
        catch (Exception ex)
        {
            ProviderStatusList.Items.Add($"Không đọc được file URL: {ex.Message}");
        }
    }

    private void SaveProviderUrls()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_providerUrlConfigPath)!);
        var json = JsonSerializer.Serialize(_urls, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_providerUrlConfigPath, json);
    }

    private void ResultLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Uri?.AbsoluteUri))
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }

    private async void RefreshLogButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLogsAsync(ApiBaseBox.Text.Trim().TrimEnd('/'));
    }

    private async Task RefreshLogsAsync(string apiBase)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apiBase) || !await IsBackendHealthyAsync(apiBase))
            {
                LogBox.Text = "API nền chưa sẵn sàng.";
                return;
            }

            LogBox.Text = await _httpClient.GetStringAsync($"{apiBase.TrimEnd('/')}/api/logs");
            LogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogBox.Text = $"Không đọc được nhật ký: {ex.Message}";
        }
    }

    private async Task EnsureBackendIsRunningAsync(string apiBase)
    {
        var existingHealth = await GetBackendHealthAsync(apiBase);
        if (string.Equals(existingHealth?.ExtractorVersion, RequiredExtractorVersion, StringComparison.Ordinal))
        {
            SetApiStatus($"API sẵn sàng ({RequiredExtractorVersion})");
            return;
        }

        if (existingHealth is not null &&
            !string.Equals(existingHealth.ExtractorVersion, RequiredExtractorVersion, StringComparison.Ordinal))
        {
            SetApiStatus($"API cũ đang chạy ({existingHealth.ExtractorVersion ?? "không có version"}), đang sửa...");
            ProviderStatusList.Items.Clear();
            ProviderStatusList.Items.Add("Đang dừng API cũ để khởi động bản mới...");
            await StopBackendAsync(apiBase);
            existingHealth = null;
        }

        if (existingHealth is not null)
        {
            throw new InvalidOperationException(
                $"API nền tại {apiBase} đang là bản cũ ({existingHealth.ExtractorVersion ?? "không có version"}). " +
                "Hãy tắt process MedicineQuickSearch/.NET Host đang chạy rồi tìm kiếm lại để app khởi động backend mới.");
        }

        ProviderStatusList.Items.Clear();
        ProviderStatusList.Items.Add("Đang khởi động API Playwright cục bộ...");

        SetApiStatus("Đang khởi động API...");

        var backendPath = FindBackendExecutablePath();
        if (backendPath is null)
        {
            throw new InvalidOperationException("Không tìm thấy API MedicineQuickSearch đã build. Hãy build solution một lần rồi chạy lại.");
        }

        var apiUri = new Uri(apiBase);
        var backendOutput = new StringBuilder();
        var backendLogPath = Path.Combine(AppContext.BaseDirectory, "logs", "backend-start.log");
        Directory.CreateDirectory(Path.GetDirectoryName(backendLogPath)!);
        await File.AppendAllTextAsync(
            backendLogPath,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} Starting backend: {backendPath} --urls {apiBase}{Environment.NewLine}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{backendPath}\" --urls \"{apiBase}\"",
            WorkingDirectory = Path.GetDirectoryName(backendPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (_backendProcess is null || _backendProcess.HasExited)
        {
            _backendProcess = Process.Start(startInfo);
            if (_backendProcess is not null)
            {
                _backendProcess.OutputDataReceived += (_, e) => AppendBackendProcessOutput(backendOutput, backendLogPath, e.Data);
                _backendProcess.ErrorDataReceived += (_, e) => AppendBackendProcessOutput(backendOutput, backendLogPath, e.Data);
                _backendProcess.BeginOutputReadLine();
                _backendProcess.BeginErrorReadLine();
            }
        }
        else
        {
            await File.AppendAllTextAsync(
                backendLogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} Reusing backend process PID={_backendProcess.Id}{Environment.NewLine}");
        }

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500);
            SetApiStatus($"Đang khởi động API... {attempt + 1}/60");
            if (_backendProcess is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"API nền đã thoát ngay khi khởi động. ExitCode={_backendProcess.ExitCode}. DLL={backendPath}. Log={backendLogPath}. {GetBackendOutputTail(backendOutput)}");
            }

            var health = await GetBackendHealthAsync(apiBase);
            if (string.Equals(health?.ExtractorVersion, RequiredExtractorVersion, StringComparison.Ordinal))
            {
                ProviderStatusList.Items.Clear();
                ProviderStatusList.Items.Add($"API nền đã sẵn sàng tại {apiUri.Host}:{apiUri.Port}.");
                return;
            }

            if (health is not null)
            {
                throw new InvalidOperationException(
                    $"API nền đã chạy nhưng sai phiên bản: {health.ExtractorVersion ?? "không có version"}, cần {RequiredExtractorVersion}. " +
                    $"DLL={backendPath}. Log={backendLogPath}.");
            }
        }

        throw new TimeoutException($"API nền khởi động quá lâu. DLL={backendPath}. Log={backendLogPath}. {GetBackendOutputTail(backendOutput)}");
    }

    private static void AppendBackendProcessOutput(StringBuilder buffer, string logPath, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(line);
        }

        File.AppendAllText(logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
    }

    private static string GetBackendOutputTail(StringBuilder buffer)
    {
        string[] lines;
        lock (buffer)
        {
            lines = buffer.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (lines.Length == 0)
        {
            return "Backend chưa ghi stdout/stderr.";
        }

        return string.Join(" | ", lines.TakeLast(8));
    }

    private async Task StopBackendAsync(string apiBase)
    {
        try
        {
            using var _ = await _httpClient.PostAsync($"{apiBase.TrimEnd('/')}/api/backend/stop", null);
        }
        catch
        {
            // Older backend versions do not have the stop endpoint; kill by port below.
        }

        if (_backendProcess is { HasExited: false })
        {
            _backendProcess.Kill(entireProcessTree: true);
            await _backendProcess.WaitForExitAsync();
            _backendProcess = null;
        }

        await KillProcessListeningOnApiPortAsync(apiBase);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await GetBackendHealthAsync(apiBase) is null)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private static async Task KillProcessListeningOnApiPortAsync(string apiBase)
    {
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri))
        {
            return;
        }

        var pid = await FindListeningProcessIdAsync(uri.Port);
        if (pid is null || pid == Environment.ProcessId)
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "taskkill",
            Arguments = $"/PID {pid.Value} /F /T",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is not null)
        {
            await process.WaitForExitAsync();
        }
    }

    private static async Task<int?> FindListeningProcessIdAsync(int port)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });

        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 ||
                !parts[1].EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[4], out var pid))
            {
                continue;
            }

            return pid;
        }

        return null;
    }

    private static string TranslateStatus(string status)
    {
        return status switch
        {
            "success" => "thành công",
            "timeout" => "quá thời gian",
            "failed" => "thất bại",
            _ => status
        };
    }

    private async Task<bool> IsBackendHealthyAsync(string apiBase)
    {
        var health = await GetBackendHealthAsync(apiBase);
        return string.Equals(health?.ExtractorVersion, RequiredExtractorVersion, StringComparison.Ordinal);
    }

    private async Task<HealthResponse?> GetBackendHealthAsync(string apiBase)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{apiBase.TrimEnd('/')}/health");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<HealthResponse>();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindBackendExecutablePath()
    {
        var candidates = new List<string>();
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            candidates.Add(Path.Combine(current.FullName, "MedicineQuickSearch", "bin", "Debug", "net8.0", "MedicineQuickSearch.dll"));
            candidates.Add(Path.Combine(current.FullName, "MedicineQuickSearch", "bin", "Release", "net8.0", "MedicineQuickSearch.dll"));

            current = current.Parent;
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "MedicineQuickSearch", "MedicineQuickSearch.dll"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindProviderUrlConfigPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var projectFile = Path.Combine(current.FullName, "MediSearch.csproj");
            if (File.Exists(projectFile))
            {
                return Path.Combine(current.FullName, "provider-urls.json");
            }

            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "provider-urls.json");
    }

    private sealed record SearchRequest(string Keyword, IReadOnlyList<string> Urls);
    private sealed record SearchResponse(string Keyword, DateTimeOffset SearchedAt, List<MedicineResult> Results, List<ProviderStatus> Providers, bool FromCache);
    private sealed record MedicineResult(string Provider, string Title, string? Price, string? Url, string? ImageUrl, string? ScreenshotUrl, string? Snippet, int MatchCount);
    private sealed record ProviderStatus(string Provider, string Url, string Status, int ResultCount, long ElapsedMs, string? Message);
    private sealed record HealthResponse(string Status, string? ExtractorVersion);
}
