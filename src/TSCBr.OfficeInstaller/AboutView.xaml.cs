using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TSCBr.OfficeInstaller.Controls;
using TSCBr.OfficeInstaller.Services;

namespace TSCBr.OfficeInstaller;

public partial class AboutView : UserControl
{
    private readonly Func<AboutContext> _context;
    private readonly Action<Uri> _openLink;
    private readonly Action<string> _copyText;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _displayedLogPath;
    public event EventHandler? BackRequested;
    public bool IsRefreshing => _refresh.IsEnabled;

    public AboutView(Func<AboutContext> context, Action<Uri>? openLink = null, Action<string>? copyText = null)
    {
        _context = context;
        _openLink = openLink ?? SupportActions.OpenLink;
        _copyText = copyText ?? Clipboard.SetText;
        InitializeComponent();
        VersionValue.Text = ApplicationInfo.Version;
        PlatformValue.Text = ApplicationInfo.Platform;
        RuntimeValue.Text = ApplicationInfo.Runtime;
        _refresh.Tick += Refresh_Tick;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        ArrangeCards();
        Motion.Reveal(InfoCard);
        Motion.Reveal(PlatformCard, 40);
        Motion.Reveal(DeveloperCard, 80);
        Motion.Reveal(HelpCard, 120);
        if (IsVisible) _refresh.Start();
    }

    private void Refresh_Tick(object? sender, EventArgs e) => RefreshStatus();

    public void RefreshStatus()
    {
        var context = _context();
        if (!string.Equals(_displayedLogPath, context.LogPath, StringComparison.OrdinalIgnoreCase))
        {
            SupportFeedback.Text = "";
            _displayedLogPath = context.LogPath;
        }
        var color = context.Health.IsHealthy ? "#22C55E" : "#F07878";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        HealthDot.Fill = brush;
        HealthValue.Foreground = brush;
        HealthValue.Text = context.Health.Status;
        HealthDetail.Text = context.Health.Description;
        HealthIndicator.ToolTip = context.Health.Description;
        AboutCopyLogButton.ToolTip = context.LogPath is not null && File.Exists(context.LogPath)
            ? "Copiar o log da última operação" : "Nenhum log disponível nesta sessão";
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e) { if (IsLoaded) ArrangeCards(); }

    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible) { RefreshStatus(); ArrangeCards(); _refresh.Start(); }
        else _refresh.Stop();
    }

    private void ArrangeCards()
    {
        var narrow = ActualWidth < 700;
        CardsGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(PlatformCard, narrow ? 1 : 0);
        Grid.SetColumn(PlatformCard, narrow ? 0 : 1);
        Grid.SetRow(DeveloperCard, narrow ? 2 : 1);
        Grid.SetRow(HelpCard, narrow ? 3 : 1);
        Grid.SetColumn(HelpCard, narrow ? 0 : 1);
        InfoCard.Margin = narrow ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 7, 14);
        PlatformCard.Margin = narrow ? new Thickness(0, 0, 0, 14) : new Thickness(7, 0, 0, 14);
        DeveloperCard.Margin = narrow ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 7, 0);
        HelpCard.Margin = narrow ? new Thickness(0) : new Thickness(7, 0, 0, 0);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var uri = (((Button)sender).Tag as string) switch
        {
            "LinkedIn" => ApplicationLinks.LinkedIn,
            "GitHub" => ApplicationLinks.GitHub,
            "Website" => ApplicationLinks.Website,
            _ => null
        };
        if (uri is null) return;
        try { _openLink(uri); SupportFeedback.Text = "Link aberto no aplicativo ou navegador padrão."; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { SupportFeedback.Text = "Não foi possível abrir o link. Verifique o navegador padrão do Windows."; }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = _context().LogPath;
        if (logPath is null || !File.Exists(logPath)) { SupportFeedback.Text = "Nenhum log disponível nesta sessão. Execute uma operação para gerar o log."; return; }
        try
        {
            _copyText(SupportActions.ReadLog(logPath));
            SupportFeedback.Text = "Log copiado. Você pode colar na conversa com o suporte.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { SupportFeedback.Text = "Não foi possível copiar o log. Verifique o arquivo ou tente novamente."; }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void View_Unloaded(object sender, RoutedEventArgs e) => _refresh.Stop();
}
