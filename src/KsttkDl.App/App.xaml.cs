using System.Windows;
using KsttkDl.App.Infrastructure;
using KsttkDl.App.Theme;

namespace KsttkDl.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = AppServices.Current;
        ThemeManager.Initialize(services.Settings.Theme);
        services.ApplyNetwork();

        new MainWindow().Show();
        _ = services.LoadLibraryAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Current.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
