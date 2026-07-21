using System.Configuration;
using System.Data;
using System.Windows;

namespace MediSearch
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var coordinator = new Versioning.VersionUpdateCoordinator();
            if (!await coordinator.EnsureApplicationCanStartAsync())
            {
                Shutdown();
                return;
            }

            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }

}
