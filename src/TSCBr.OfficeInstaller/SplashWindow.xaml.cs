using System.Windows;

namespace TSCBr.OfficeInstaller;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public void SetStatus(string text) => StartupStatus.Text = text;
}
