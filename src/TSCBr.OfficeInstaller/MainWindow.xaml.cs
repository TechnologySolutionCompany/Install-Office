using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using TSCBr.OfficeInstaller.Services;
using TSCBr.OfficeInstaller.Controls;

namespace TSCBr.OfficeInstaller;

public partial class MainWindow : Window
{
    private readonly IOfficeInstaller _installer;
    private readonly Func<OfficeInventory, bool> _confirmRemoval;
    private readonly OfficeInventory? _initialInventory;
    private OfficeInventory _inventory = OfficeInventory.Empty;
    private CancellationTokenSource? _installationCancellation;
    private bool _busy, _checking, _removing, _detectionFailed;
    private string? _lastLogPath;
    private bool _operationFailed;
    private string? _operationFailure;
    private AboutView? _aboutView;
    private readonly Func<Func<AboutContext>, AboutView> _createAboutView;
    private readonly Action<Uri> _openLink;
    private string? CurrentLogPath => _busy ? _installer.LogPath : _lastLogPath;

    public MainWindow() : this(new global::TSCBr.OfficeInstaller.Services.OfficeInstaller()) { }

    public MainWindow(IOfficeInstaller installer, Func<OfficeInventory, bool>? confirmRemoval = null,
        OfficeInventory? initialInventory = null, Func<Func<AboutContext>, AboutView>? aboutViewFactory = null,
        Action<Uri>? openLink = null)
    {
        _installer = installer;
        _openLink = openLink ?? SupportActions.OpenLink;
        _createAboutView = aboutViewFactory ?? (context => new AboutView(context));
        _confirmRemoval = confirmRemoval ?? ConfirmRemoval;
        _initialInventory = initialInventory;
        InitializeComponent();
        InstallButton.IsEnabled = false;
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Motion.Reveal(InstallSurface);
        Motion.Reveal(ActivationInfoSurface, 70);
        Motion.Reveal(StatusBorder, 110);
        if (_initialInventory is not null)
        {
            _inventory = _initialInventory;
            ShowInventoryStatus();
            ApplyInventoryControls();
            return;
        }
        await RefreshDetectionAsync(updateStatus: true);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && !_checking) await RefreshDetectionAsync(updateStatus: true);
    }

    private async Task RefreshDetectionAsync(bool updateStatus)
    {
        _checking = true;
        ApplyInventoryControls();
        try
        {
            _inventory = await _installer.DetectOfficeAsync();
            _detectionFailed = false;
            if (updateStatus) ShowInventoryStatus();
        }
        catch (Exception ex)
        {
            _detectionFailed = true;
            StatusTitle.Text = "Não foi possível verificar o Office";
            StatusDetail.Text = ex.Message + " Clique em Atualizar para tentar novamente.";
            BadgeText.Text = "ATENÇÃO";
            SetResultColor("#F07878");
        }
        finally { _checking = false; ApplyInventoryControls(); }
    }

    private void ShowInventoryStatus()
    {
        SetResultColor("#00D4FF");
        StatusTitle.Text = _inventory.IsInstalled ? "Microsoft Office encontrado" : "Tudo pronto para instalar";
        StatusDetail.Text = _inventory.IsInstalled
            ? string.Join(" · ", _inventory.Products.Select(x => x.Name).Distinct().Take(4)) + "."
            : "Office LTSC 2024 · 64 bits. A instalação requer internet.";
        BadgeText.Text = _inventory.IsInstalled ? "INSTALADO" : "PRONTO";
    }

    private void ApplyInventoryControls()
    {
        UpdateLoading();
        var available = !_busy && !_checking && !_detectionFailed;
        InstallButton.IsEnabled = available && !_inventory.IsInstalled;
        RefreshButton.IsEnabled = !_busy && !_checking;
        UninstallButton.Visibility = !_busy && _inventory.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = available;
        if (!_busy)
        {
            InstallLabel.Text = _inventory.IsInstalled ? "Microsoft Office instalado" : "Instalar Microsoft Office";
            InstallSubtitle.Text = _inventory.IsInstalled ? "Use Desinstalar todo o Office para remover" : "LTSC Professional Plus 2024 · 64 bits";
        }
    }

    private void UpdateLoading()
    {
        var loading = _busy || _checking;
        InstallBusy.IsActive = loading;
        StatusBusy.IsActive = loading;
        InstallChevron.Visibility = InstallBusy.IsActive ? Visibility.Collapsed : Visibility.Visible;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutView is null)
        {
            _aboutView = _createAboutView(GetAboutContext);
            _aboutView.BackRequested += (_, _) =>
            {
                AboutHost.Visibility = Visibility.Collapsed;
                ContentScroll.Visibility = Visibility.Visible;
                AboutButton.Focus();
            };
            AboutHost.Content = _aboutView;
        }
        ContentScroll.Visibility = Visibility.Collapsed;
        AboutHost.Visibility = Visibility.Visible;
        _aboutView.RefreshStatus();
    }

    public AboutContext GetAboutContext() => new(
        ApplicationInfo.ReadHealth(_operationFailed, _detectionFailed ? StatusDetail.Text : _operationFailure, _busy, _detectionFailed),
        CurrentLogPath);

    private void RecordOperationState(bool failed, string? detail = null)
    {
        _operationFailed = failed;
        _operationFailure = failed ? detail : null;
    }

    private void RefreshLogButtons()
    {
        var available = CurrentLogPath is not null && File.Exists(CurrentLogPath);
        OpenLogButton.IsEnabled = available;
        CopyLogButton.IsEnabled = available;
    }

    // Activation is not automated: this only opens Microsoft's official documentation.
    private void ActivationGuide_Click(object sender, RoutedEventArgs e)
    {
        try { _openLink(ApplicationLinks.OfficialActivationGuide); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "Não foi possível abrir o navegador padrão.\n\n" + ApplicationLinks.OfficialActivationGuide.AbsoluteUri,
                "Ativação do Office", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checking || _detectionFailed || _inventory.IsInstalled) return;
        await RunOperationAsync(removing: false);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checking || _detectionFailed || !_inventory.IsInstalled) return;
        if (!_confirmRemoval(_inventory)) return;
        await RunOperationAsync(removing: true);
    }

    private bool ConfirmRemoval(OfficeInventory inventory) => MessageBox.Show(this,
        "Deseja desinstalar todo o Microsoft Office deste computador?\n\n" +
        string.Join("\n", inventory.Products.Select(x => "• " + x.Name).Distinct().Take(8)) +
        "\n\nTodos os aplicativos Office detectados serão removidos, incluindo Visio e Project, se presentes. Seus documentos pessoais serão mantidos. Salve os documentos e feche os aplicativos antes de continuar.",
        "Confirmar desinstalação do Office", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task RunOperationAsync(bool removing)
    {
        _busy = true;
        _removing = removing;
        CopyLogButton.Content = "Copiar log";
        _installationCancellation = removing ? null : new CancellationTokenSource();
        ApplyInventoryControls();
        InstallLabel.Text = removing ? "Removendo Microsoft Office" : "Instalando Microsoft Office";
        BadgeText.Text = "EM ANDAMENTO";
        SetResultColor("#00D4FF");
        StopButton.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;
        StopButton.IsEnabled = !removing;
        StopButton.Content = "Parar e remover instalação";
        var progress = new Progress<InstallProgress>(ShowProgress);
        try
        {
            if (removing)
            {
                var result = await Task.Run(() => _installer.UninstallAsync(progress));
                RecordOperationState(!result.RemovalVerified, "Reinicie o Windows para concluir a desinstalação e confira o resultado.");
                StatusTitle.Text = result.RemovalVerified ? "Microsoft Office desinstalado com sucesso!" : "Reinicie para concluir a desinstalação";
                StatusDetail.Text = result.RemovalVerified
                    ? "A remoção dos produtos Office foi confirmada. Seus documentos pessoais foram mantidos."
                    : "O instalador solicitou reinicialização. A remoção completa ainda não foi confirmada.";
                if (result.RestartRequired && result.RemovalVerified) StatusDetail.Text += " Reinicie o Windows para concluir.";
                BadgeText.Text = result.RestartRequired ? "REINICIE" : "REMOVIDO";
                SetResultColor(result.RemovalVerified ? "#22C55E" : "#F0B85A");
            }
            else
            {
                var token = _installationCancellation!.Token;
                var result = await Task.Run(() => _installer.InstallAsync(progress, token));
                RecordOperationState(!result.InstallationVerified, "Reinicie o Windows para concluir a instalação e confira o resultado.");
                StatusTitle.Text = result.InstallationVerified ? "Microsoft Office instalado com sucesso!" : "Reinicialização necessária";
                StatusDetail.Text = result.InstallationVerified
                    ? "Office LTSC Professional Plus 2024 · 64 bits · versão " + result.Version + "."
                    : "O instalador solicitou uma reinicialização. A presença do Office ainda não foi confirmada.";
                if (result.RestartRequired && result.InstallationVerified) StatusDetail.Text += " Reinicie o Windows para concluir.";
                BadgeText.Text = result.RestartRequired ? "REINICIE" : "CONCLUÍDO";
                SetResultColor(result.InstallationVerified ? "#22C55E" : "#F0B85A");
            }
        }
        catch (InstallationStoppedException ex)
        {
            RecordOperationState(!ex.Cleanup.RemovalVerified, ex.Message);
            StatusTitle.Text = ex.Cleanup.RemovalVerified ? "Instalação interrompida e removida" : "Instalação parada — reinicie o Windows";
            StatusDetail.Text = ex.Message;
            BadgeText.Text = ex.Cleanup.RestartRequired ? "REINICIE" : "CANCELADO";
            SetResultColor("#F0B85A");
        }
        catch (OperationCanceledException ex)
        {
            RecordOperationState(failed: true, ex.Message);
            StatusTitle.Text = "Operação cancelada";
            StatusDetail.Text = ex.Message;
            BadgeText.Text = "CANCELADO";
            SetResultColor("#F0B85A");
        }
        catch (Exception ex)
        {
            RecordOperationState(failed: true, ex.Message);
            StatusTitle.Text = removing ? "Não foi possível concluir a desinstalação" : "Não foi possível concluir a instalação";
            StatusDetail.Text = ex.Message;
            BadgeText.Text = "ATENÇÃO";
            SetResultColor("#F07878");
        }
        finally
        {
            _lastLogPath = _installer.LogPath;
            _busy = false;
            _installationCancellation?.Dispose();
            _installationCancellation = null;
            StopButton.Visibility = Visibility.Collapsed;
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            RefreshLogButtons();
            await RefreshDetectionAsync(updateStatus: false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy || _removing || _installationCancellation is null || _installationCancellation.IsCancellationRequested) return;
        StopButton.IsEnabled = false;
        StopButton.Content = "Parando e removendo…";
        StatusTitle.Text = "Interrompendo a instalação";
        StatusDetail.Text = "O instalador será interrompido. Aguarde a remoção dos componentes adicionados.";
        _installationCancellation.Cancel();
    }

    private void ShowProgress(InstallProgress progress)
    {
        if (!_busy) return;
        if (StatusTitle.Text != progress.Title) Motion.PhaseChanged(StatusBorder);
        StatusTitle.Text = progress.Title;
        StatusDetail.Text = progress.Detail;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = !progress.Percent.HasValue;
        if (progress.Percent.HasValue) InstallProgress.Value = progress.Percent.Value;
        StopButton.IsEnabled = !_removing && progress.CanStop && _installationCancellation?.IsCancellationRequested == false;
        RefreshLogButtons();
    }

    private void SetResultColor(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        StatusTitle.Foreground = brush;
        BadgeText.Foreground = brush;
        StatusBorder.BorderBrush = brush;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = CurrentLogPath;
        if (logPath is null || !File.Exists(logPath)) return;
        try
        {
            SupportActions.OpenLog(logPath);
        }
        catch (Win32Exception) { MessageBox.Show("O log foi salvo em:\n" + logPath, "Log de operação", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = CurrentLogPath;
        if (logPath is null || !File.Exists(logPath)) return;
        try
        {
            Clipboard.SetText(SupportActions.ReadLog(logPath));
            CopyLogButton.Content = "Copiado!";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "Não foi possível copiar o log. Tente novamente ou use Ver log.\n\n" + ex.Message,
                "Copiar log", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Author_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { MessageBox.Show("Não foi possível abrir o navegador padrão.", "TSCBr"); }
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        MessageBox.Show(this, _removing
            ? "Aguarde a conclusão da desinstalação para fechar o aplicativo."
            : "Use Parar e remover instalação e aguarde a limpeza para fechar o aplicativo.",
            "Operação em andamento", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
