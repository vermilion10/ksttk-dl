using System.Windows;
using KsttkDl.App.Infrastructure;
using KsttkDl.App.Theme;

namespace KsttkDl.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // a fault in one view is reported rather than taking the whole app down
        DispatcherUnhandledException += (_, args) =>
        {
            AppServices.Current.AppendLog("Error: " + args.Exception);
            AppServices.Current.Notify("Something went wrong", args.Exception.Message, ToastKind.Error);
            args.Handled = true;
        };

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
