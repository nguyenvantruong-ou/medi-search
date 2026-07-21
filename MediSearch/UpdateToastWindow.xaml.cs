using System.Windows;
using System.Windows.Input;
using MediSearch.Versioning;

namespace MediSearch;

public partial class UpdateToastWindow : Window
{
    private readonly UpdateCheckResult _result;
    private bool _isUpdating;

    public UpdateToastWindow(UpdateCheckResult result)
    {
        InitializeComponent();
        _result = result;
        VersionText.Text = $"Version {_result.LatestVersion} đã sẵn sàng. Hiện tại: {_result.LocalVersion}.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 18;
        Top = area.Bottom - Height - 18;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void Toast_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        Hide();
        var coordinator = new VersionUpdateCoordinator();
        coordinator.StartUpdate(_result.VersionInfo);
        Close();
    }
}
