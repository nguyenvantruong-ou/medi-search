using System.Windows;
using MediSearch.Versioning;

namespace MediSearch;

public partial class UpdateDialog : Window
{
    private readonly UpdateCheckResult _result;

    public UpdateDialog(UpdateCheckResult result)
    {
        InitializeComponent();
        _result = result;
        ConfigureContent();
    }

    private void ConfigureContent()
    {
        if (_result.Requirement == UpdateRequirement.Required)
        {
            TitleText.Text = "This version is no longer supported.";
            MessageText.Text =
                $"Current version: {_result.LocalVersion}\n" +
                $"Minimum supported version: {_result.MinimumVersion}\n\n" +
                "Please download the latest version to continue.";
            LaterButton.Content = "Exit";
        }
        else
        {
            TitleText.Text = $"A newer version ({_result.LatestVersion}) is available.";
            MessageText.Text =
                $"Current version: {_result.LocalVersion}\n\n" +
                "Would you like to update now?";
            LaterButton.Content = "Later";
        }

        ReleaseNotesBox.Text = _result.VersionInfo.ReleaseNotes;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
