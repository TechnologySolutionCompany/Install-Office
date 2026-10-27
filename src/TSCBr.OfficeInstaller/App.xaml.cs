using System.Windows;
using TSCBr.OfficeInstaller.Services;

namespace TSCBr.OfficeInstaller;

public partial class App : Application
{
    public Func<IOfficeInstaller> InstallerFactory { get; init; } = () => new Services.OfficeInstaller();

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Não foi possível concluir a operação. Feche o aplicativo e tente novamente.\n\n" + args.Exception.Message,
                "TSCBr — Instalador do Office", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        if (MainWindow is not null) return;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow();
        MainWindow = splash;
        splash.Show();
        try
        {
            var installer = InstallerFactory();
            var minimumDisplay = Task.Delay(650);
            var inventory = await StartupPreparation.PrepareAsync(installer, new Progress<string>(splash.SetStatus));
            await minimumDisplay;
            if (!splash.IsVisible) { Shutdown(); return; }
            var window = new MainWindow(installer, initialInventory: inventory);
            MainWindow = window;
            window.Show();
            splash.Close();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            splash.Close();
            MessageBox.Show("Não foi possível preparar as ferramentas do Office.\n\n" + ex.Message,
                "TSCBr — Inicialização", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
