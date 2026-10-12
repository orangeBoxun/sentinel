using System.Windows;

namespace Sentinel.Setup;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        new MainWindow().Show();
    }
}
