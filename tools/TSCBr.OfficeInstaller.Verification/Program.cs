using System.IO;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using TSCBr.OfficeInstaller;
using TSCBr.OfficeInstaller.Services;
using TSCBr.OfficeInstaller.Controls;
using Installer = TSCBr.OfficeInstaller.Services.OfficeInstaller;

internal static class Program
{
    private static int _checks;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--startup-preview") return RunStartupPreview(args[1]);
        // Harmless child process used solely to verify real ProcessStartInfo argument passing.
        if (args.FirstOrDefault() == "/configure")
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "stub-arguments.json"), JsonSerializer.Serialize(args));
            return 17002;
        }
        if (args.FirstOrDefault() == "--wait-for-stop")
        {
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            Thread.Sleep(30000);
            return 0;
        }
        var artifacts = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts");
        Directory.CreateDirectory(artifacts);
        try
        {
            RunServiceChecks().GetAwaiter().GetResult();
            RunStartupChild(artifacts).GetAwaiter().GetResult();
            RunUiChecks(artifacts);
            Console.WriteLine($"PASS: {_checks} verificações. Nenhum instalador real foi executado. Prévias: {artifacts}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunServiceChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "TSCBr-Verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Intentionally retained in TEMP for diagnosis; never delete a computed tree.
        var payload = new EmbeddedPayload();
        Check(payload.Files.Contains("setup.exe") && payload.Files.Contains(OfficeConfiguration.SourceFileName),
            "Os arquivos locais devem estar incorporados ao aplicativo.");
        using (var bundled = payload.Open(OfficeConfiguration.SourceFileName))
        {
            var source = XDocument.Load(bundled);
            Check(!source.Descendants().Any(x => x.Attribute("PIDKEY") is not null),
                "A configuração publicada não deve conter chave de produto.");
        }

        var platform = new FakePlatform();
        var folder = Path.Combine(root, "success", "MS OFFICE SETUP");
        var installer = CreateInstaller(platform, payload, folder);
        var result = await installer.InstallAsync(new InlineProgress());
        Check(result.InstallationVerified && !result.RestartRequired, "Sucesso exige produto verificado.");
        Check(platform.Calls == 1 && platform.LastWorkingDirectory == folder, "O fluxo deve executar o instalador uma vez na pasta correta.");
        Check(platform.LastSetup == Path.Combine(folder, "setup.exe") &&
              platform.LastConfiguration == Path.Combine(folder, OfficeConfiguration.RuntimeFileName), "O fluxo deve usar o setup e o XML de execução, com caminhos completos.");
        foreach (var relative in payload.Files)
        {
            await using var source = payload.Open(relative);
            await using var copied = File.OpenRead(Path.Combine(folder, relative));
            var sourceHash = await SHA256.HashDataAsync(source);
            var copiedHash = await SHA256.HashDataAsync(copied);
            Check(CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash),
                "A cópia local deve preservar " + relative);
        }

        var xml = XDocument.Load(platform.LastConfiguration!);
        Check(xml.Root!.Elements("Property").Single(x => (string?)x.Attribute("Name") == "AUTOACTIVATE").Attribute("Value")!.Value == "0",
            "A instalação não deve solicitar ativação automática.");
        Check((string?)xml.Root.Element("Display")?.Attribute("Level") == "Full", "A janela oficial do Office deve permanecer visível.");
        Check(xml.Root.Element("AppSettings")?.Elements("User").Count() == 3 && xml.Root.Element("RemoveMSI") is not null,
            "Formatos dos documentos e opções do XML fornecido devem ser preservados.");
        var log = File.ReadAllText(installer.LogPath!);
        Check(!log.Contains("PIDKEY", StringComparison.OrdinalIgnoreCase) &&
              !System.Text.RegularExpressions.Regex.IsMatch(log, @"\b[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\b"),
            "O log não deve expor chaves de produto.");

        var processTest = new WindowsInstallationPlatform();
        var stub = Path.Combine(AppContext.BaseDirectory, "TSCBr.OfficeInstaller.Verification.exe");
        var accentedConfig = Path.Combine(folder, "Configuração de teste.xml");
        var processExit = await processTest.RunSetupAsync(stub, accentedConfig, folder);
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(folder, "stub-arguments.json")))!;
        Check(processExit == 17002, "A execução real de um processo de teste deve preservar o código de saída.");
        Check(arguments.SequenceEqual(new[] { "/configure", accentedConfig }), "Espaços e acentos devem chegar ao processo como um único argumento correto.");

        var absent = new FakePlatform { Installed = null };
        await MustFail<InvalidOperationException>(() => CreateInstaller(absent, payload, Path.Combine(root, "missing")).InstallAsync(new InlineProgress()),
            "Código zero sem produto instalado não pode ser sucesso.");
        var failed = new FakePlatform { ExitCode = 17002 };
        await MustFail<InvalidOperationException>(() => CreateInstaller(failed, payload, Path.Combine(root, "failed")).InstallAsync(new InlineProgress()),
            "Erro do instalador deve ser propagado.");
        var canceled = new FakePlatform { ExitCode = 1602 };
        await MustFail<InstallationStoppedException>(() => CreateInstaller(canceled, payload, Path.Combine(root, "canceled")).InstallAsync(new InlineProgress()),
            "Cancelamento na janela Microsoft deve remover o produto parcial e não pode ser sucesso de instalação.");
        var reboot = new FakePlatform { ExitCode = 3010, Installed = null };
        var pending = await CreateInstaller(reboot, payload, Path.Combine(root, "reboot")).InstallAsync(new InlineProgress());
        Check(pending.RestartRequired && !pending.InstallationVerified, "Reinicialização pendente sem produto ainda não confirma a instalação.");
        var untrusted = new FakePlatform { RejectSignature = true };
        await MustFail<SecurityException>(() => CreateInstaller(untrusted, payload, Path.Combine(root, "untrusted")).InstallAsync(new InlineProgress()),
            "Uma assinatura inválida deve interromper o fluxo.");
        Check(untrusted.Calls == 0, "Um instalador não confiável nunca deve ser iniciado.");

        var waiting = new FakePlatform { Gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var concurrent = CreateInstaller(waiting, payload, Path.Combine(root, "concurrent"));
        var first = concurrent.InstallAsync(new InlineProgress());
        await WaitUntil(() => waiting.Calls == 1);
        await MustFail<InvalidOperationException>(() => concurrent.InstallAsync(new InlineProgress()), "Cliques concorrentes devem ser bloqueados.");
        waiting.Gate.SetResult(0);
        await first;
        Check(waiting.Calls == 1, "Cliques concorrentes não podem abrir outro setup.");

        using (var wrongProduct = new MemoryStream(Encoding.UTF8.GetBytes(
                   "<Configuration><Add OfficeClientEdition=\"64\" Channel=\"Current\"><Product ID=\"O365ProPlusRetail\" /></Add></Configuration>")))
        {
            try { OfficeConfiguration.CreateForInstallation(wrongProduct); throw new Exception("Configuração errada aceita."); }
            catch (InvalidDataException) { _checks++; }
        }

        // Real, read-only validation of the bundled Microsoft executable and a tampered copy.
        MicrosoftSignature.Verify(Path.Combine(folder, "setup.exe"));
        _checks++;
        var tampered = Path.Combine(root, "tampered-setup.exe");
        File.Copy(Path.Combine(folder, "setup.exe"), tampered);
        using (var file = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 4096;
            var value = file.ReadByte();
            file.Position = 4096;
            file.WriteByte((byte)(value ^ 0xFF));
        }
        try { MicrosoftSignature.Verify(tampered); throw new Exception("Executável adulterado aceito."); }
        catch (SecurityException) { _checks++; }
        await RunRemovalChecks(root, payload);
        RunAboutServiceChecks(root);
        Console.WriteLine("PASS: fluxo de instalação, arquivos, configuração, erros, concorrência e assinaturas. Destino de teste: " + root);
    }

    private static void RunAboutServiceChecks(string root)
    {
        Check(new ApplicationHealth(true, true, false).IsHealthy, "Estado pronto com administrador deve ser saudável.");
        Check(!new ApplicationHealth(false, true, false).IsHealthy &&
              new ApplicationHealth(false, true, false).Description.Contains("administrador"), "Falta de administrador deve indicar falha e explicar a causa.");
        Check(!new ApplicationHealth(true, false, false).IsHealthy, "Plataforma incompatível deve indicar falha.");
        Check(!new ApplicationHealth(true, true, true, "Falha de teste").IsHealthy,
            "Falha de operação deve permanecer visível no indicador.");
        Check(new ApplicationHealth(true, true, false, IsBusy: true).IsHealthy,
            "Uma operação em andamento, sem falha, não deve indicar defeito.");
        var diagnostics = new AboutContext(new(true, true, true, "Chave: AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"), null).DiagnosticText();
        Check(diagnostics.Contains(ApplicationInfo.Version) && diagnostics.Contains(ApplicationInfo.Runtime) &&
              diagnostics.Contains("Administrador: Sim") && !diagnostics.Contains("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"),
            "Diagnóstico deve incluir versão, runtime, permissões e ocultar chaves.");
        var path = Path.Combine(root, "log-para-suporte.txt");
        File.WriteAllText(path, "Diagnóstico\nAAAAA-BBBBB-CCCCC-DDDDD-EEEEE\nFim do log.");
        Check(SupportActions.ReadLog(path).Contains("[chave omitida]") && SupportActions.ReadLog(path).Contains("Fim do log."),
            "Cópia de log deve manter o diagnóstico e ocultar chaves.");
        File.WriteAllText(path, new string('a', 2 * 1024 * 1024 + 50) + "\nÚltima linha do log.");
        var tail = SupportActions.ReadLog(path);
        Check(tail.Length < 2 * 1024 * 1024 + 100 && tail.Contains("Última linha do log.") && tail.StartsWith("[Trecho final"),
            "Cópia de logs grandes deve ser limitada e preservar linhas completas no final.");
        Check(ApplicationLinks.Website.AbsoluteUri == "https://www.tscbr.com.br/" &&
              ApplicationLinks.GitHub.AbsoluteUri == "https://github.com/dev-estevao-miguel",
            "Links devem usar o site e o perfil informados.");
        var guide = ApplicationLinks.OfficialActivationGuide;
        Check(guide.Scheme == Uri.UriSchemeHttps && guide.Host == "learn.microsoft.com",
            "A orientação de ativação deve apontar somente para a documentação oficial da Microsoft.");
    }

    private static async Task RunRemovalChecks(string root, EmbeddedPayload payload)
    {
        var target = new OfficeProduct(OfficeConfiguration.ProductId, "Office LTSC 2024", OfficeTechnology.ClickToRun);
        var visio = new OfficeProduct("VisioPro2024Volume", "Visio 2024", OfficeTechnology.ClickToRun);
        var legacy = new OfficeProduct("{90160000-0011-0000-1000-0000000FF1CE}", "Microsoft Office Professional Plus 2016", OfficeTechnology.Msi);
        var full = new FakePlatform { Inventory = new OfficeInventory([target, visio, legacy]) };
        var removed = await CreateInstaller(full, payload, Path.Combine(root, "uninstall-all")).UninstallAsync(new InlineProgress());
        Check(removed.RemovalVerified && !full.Inventory.IsInstalled, "A desinstalação completa deve conferir a ausência do Office.");
        Check((string?)full.RemovalConfiguration!.Root!.Element("Remove")!.Attribute("All") == "TRUE" && full.RemovedMsi.SequenceEqual(new[] { legacy.Id }),
            "Remoção completa deve incluir todos os produtos C2R e pacotes MSI detectados.");

        var leftover = new FakePlatform { Inventory = new OfficeInventory([target]), PreserveAfterRemoval = true };
        await MustFail<InvalidOperationException>(() => CreateInstaller(leftover, payload, Path.Combine(root, "leftovers")).UninstallAsync(new InlineProgress()),
            "Retorno zero com Office ainda presente não pode declarar remoção completa.");
        var restart = new FakePlatform { Inventory = new OfficeInventory([target]), PreserveAfterRemoval = true, RemovalExitCode = 3010 };
        var pending = await CreateInstaller(restart, payload, Path.Combine(root, "remove-restart")).UninstallAsync(new InlineProgress());
        Check(pending.RestartRequired && !pending.RemovalVerified, "Reinicialização pendente deve ser informada sem confirmar remoção.");
        var preexisting = new FakePlatform { Inventory = new OfficeInventory([target]) };
        await MustFail<InvalidOperationException>(() => CreateInstaller(preexisting, payload, Path.Combine(root, "existing")).InstallAsync(new InlineProgress()),
            "A instalação cancelável não deve substituir um Office anterior.");
        Check(preexisting.Calls == 0, "Office preexistente exige desinstalação confirmada, sem iniciar outro setup.");

        var folder = Path.Combine(root, "stop-and-cleanup");
        Directory.CreateDirectory(folder);
        const string retained = OfficeConfiguration.SourceFileName;
        await using (var original = payload.Open(retained))
        await using (var output = File.Create(Path.Combine(folder, retained))) await original.CopyToAsync(output);
        File.WriteAllText(Path.Combine(folder, "arquivo-do-usuario.txt"), "preservar");
        var stopped = new FakePlatform { Gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using (var cancellation = new CancellationTokenSource())
        {
            var job = CreateInstaller(stopped, payload, folder).InstallAsync(new InlineProgress(), cancellation.Token);
            await WaitUntil(() => stopped.Calls == 1);
            cancellation.Cancel();
            await MustFail<InstallationStoppedException>(() => job, "Parada deve aguardar a remoção do produto parcial.");
        }
        Check(stopped.CancellationObserved && stopped.Calls == 2 && !stopped.Inventory.IsInstalled, "Parada deve cancelar o setup e executar a limpeza oficial.");
        var scopedRemoval = stopped.RemovalConfiguration!.Root!.Element("Remove")!;
        Check((string?)scopedRemoval.Attribute("All") == "FALSE" && (string?)scopedRemoval.Element("Product")?.Attribute("ID") == OfficeConfiguration.ProductId &&
              stopped.RemovalConfiguration.Root.Element("RemoveMSI") is null,
            "Limpeza de cancelamento deve remover somente o produto desta instalação.");
        Check(File.Exists(Path.Combine(folder, retained)) && File.ReadAllText(Path.Combine(folder, "arquivo-do-usuario.txt")) == "preservar" &&
              !File.Exists(Path.Combine(folder, "setup.exe")) && !File.Exists(Path.Combine(folder, OfficeConfiguration.RuntimeFileName)),
            "Limpeza deve excluir somente arquivos novos rastreados e preservar arquivos anteriores.");

        var copyCancellation = new FakePlatform();
        using (var cancellation = new CancellationTokenSource())
        {
            var job = CreateInstaller(copyCancellation, payload, Path.Combine(root, "stop-copy")).InstallAsync(
                new InlineProgress(p => { if (p.Percent.HasValue) cancellation.Cancel(); }), cancellation.Token);
            await MustFail<InstallationStoppedException>(() => job, "Parada durante a cópia deve limpar os arquivos sem iniciar o Office.");
        }
        Check(copyCancellation.Calls == 0, "Parada antes da execução não deve executar instalação ou desinstalação.");
        var failedCleanup = new FakePlatform { Gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously), RemovalExitCode = 17002 };
        using (var cancellation = new CancellationTokenSource())
        {
            var job = CreateInstaller(failedCleanup, payload, Path.Combine(root, "cleanup-failed")).InstallAsync(new InlineProgress(), cancellation.Token);
            await WaitUntil(() => failedCleanup.Calls == 1);
            cancellation.Cancel();
            await MustFail<InvalidOperationException>(() => job, "Falha de limpeza deve ser exposta, sem confirmar remoção.");
        }

        Check(OfficeInventoryReader.IsOfficeEntry("Microsoft Office Professional Plus 2016", "Microsoft Corporation") &&
              !OfficeInventoryReader.IsOfficeEntry("Microsoft Edge", "Microsoft Corporation") &&
              !OfficeInventoryReader.IsOfficeEntry("Microsoft Office Tools", "Outra empresa"),
            "A descoberta deve limitar a remoção aos produtos Office da Microsoft.");
        var windows = new WindowsInstallationPlatform();
        await MustFail<InvalidDataException>(() => windows.UninstallMsiAsync("not-a-product-code"), "MSI inválido não deve ser executado.");
        try { windows.DeleteStagedFile(folder, "..\\outside.txt"); throw new Exception("Caminho de limpeza externo aceito."); }
        catch (IOException) { _checks++; }

        var ready = Path.Combine(root, "process-ready.txt");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TSCBr.OfficeInstaller.Verification.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--wait-for-stop");
        start.ArgumentList.Add(ready);
        var interruptedService = 0;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var process = OwnedSetupProcess.RunAsync(start, cancellation.Token, () => { interruptedService++; return Task.CompletedTask; });
            await WaitUntil(() => File.Exists(ready));
            var pid = int.Parse(File.ReadAllText(ready));
            cancellation.Cancel();
            await MustFail<OperationCanceledException>(() => process, "A parada real deve encerrar o processo filho de teste.");
            var stillRunning = true;
            try { using var child = Process.GetProcessById(pid); stillRunning = !child.HasExited; }
            catch (ArgumentException) { stillRunning = false; }
            Check(!stillRunning && interruptedService == 1, "Processo encerrado e etapa de interrupção do serviço chamada exatamente uma vez.");
        }
        Console.WriteLine("PASS: parada forçada de processo de teste, limpeza, desinstalação, escopos e reinicialização.");
    }

    private static Installer CreateInstaller(FakePlatform platform, EmbeddedPayload payload, string folder) =>
        new(platform, payload, new InstallationLog(Path.Combine(folder, "logs")), folder, TimeSpan.Zero);

    private static void RunUiChecks(string artifacts)
    {
        var application = new App();
        application.InitializeComponent();
        var backend = new UiInstaller();
        var openedLinks = new List<Uri>();
        var window = new MainWindow(backend, openLink: openedLinks.Add)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };
        Exception? failure = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var installButton = (Button)window.FindName("InstallButton");
                var guideButton = (Button)window.FindName("ActivationGuideButton");
                var authorLink = (Hyperlink)window.FindName("AuthorLink");
                var progressBar = (ProgressBar)window.FindName("InstallProgress");
                var badgeText = (TextBlock)window.FindName("BadgeText");
                var statusTitle = (TextBlock)window.FindName("StatusTitle");
                Check(installButton.IsEnabled && guideButton.IsEnabled && window.FindName("ActivateButton") is null,
                    "Instalação disponível, orientação oficial de ativação visível e nenhuma ativação automatizada.");
                guideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(openedLinks.Count == 1 && openedLinks[0] == ApplicationLinks.OfficialActivationGuide,
                    "O botão de ativação deve apenas abrir a documentação oficial da Microsoft.");
                Check(authorLink.NavigateUri.AbsoluteUri == "https://www.linkedin.com/in/estev%C3%A3o-miguel-a22057171/", "Link do autor correto.");
                Check(window.Width <= 520 && window.Height <= 460, "A janela deve iniciar compacta.");
                await Task.Delay(420);
                Check(((Border)window.FindName("InstallSurface")).Opacity > 0.99 && ((Border)window.FindName("ActivationInfoSurface")).Opacity > 0.99,
                    "Áreas devem terminar a entrada sem perder sua opacidade de base.");
                SavePreview(window, Path.Combine(artifacts, "01-pronto.png"));
                await RunAboutUiChecks(artifacts);
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                await Task.Delay(100);
                SavePreview(window, Path.Combine(artifacts, "02-tamanho-minimo.png"));
                Check(((ScrollViewer)window.FindName("ContentScroll")).ViewportWidth > 0, "Conteúdo redimensionável deve permanecer navegável.");
                window.Width = 520;
                window.Height = 460;
                installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => backend.Calls == 1 && progressBar.Visibility == Visibility.Visible);
                var busy = (BusyIndicator)window.FindName("InstallBusy");
                var statusBusy = (BusyIndicator)window.FindName("StatusBusy");
                Check(busy.IsActive && statusBusy.IsActive, "Indicadores devem acompanhar a instalação.");
                if (SystemParameters.ClientAreaAnimation)
                {
                    await WaitUntil(() => busy.IsAnimating);
                    var rotation = (RotateTransform)busy.FindName("SpinnerRotation");
                    var angle = rotation.Angle;
                    await Task.Delay(180);
                    Check(Math.Abs(angle - rotation.Angle) > 0.1, "O indicador deve girar durante a operação.");
                }
                else Check(!busy.IsAnimating, "Indicador deve respeitar a preferência de reduzir animações do Windows.");
                Check(!installButton.IsEnabled && !((Button)window.FindName("RefreshButton")).IsEnabled, "Interface bloqueia ações durante a instalação.");
                installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(backend.Calls == 1, "O evento de clique repetido não executa outra instalação.");
                SavePreview(window, Path.Combine(artifacts, "03-instalando.png"));
                backend.Completion.SetResult(new InstallResult(false, true, "16.0.00000.00000"));
                await WaitUntil(() => badgeText.Text == "CONCLUÍDO");
                await WaitUntil(() => !busy.IsActive && !statusBusy.IsActive);
                Check(!busy.IsAnimating && !statusBusy.IsAnimating, "Animações contínuas devem parar após a operação.");
                Check(statusTitle.Text.Contains("sucesso") && !installButton.IsEnabled, "Sucesso confirmado aparece na interface.");
                SavePreview(window, Path.Combine(artifacts, "04-concluido.png"));
                await WaitUntil(() => ((Button)window.FindName("UninstallButton")).Visibility == Visibility.Visible);
                Check(((Button)window.FindName("UninstallButton")).IsEnabled, "Desinstalação fica disponível após detectar o Office instalado.");

                var confirmationAllowed = false;
                var confirmations = 0;
                var detectedBackend = new UiInstaller
                {
                    Inventory = new OfficeInventory([new OfficeProduct(OfficeConfiguration.ProductId, "Microsoft Office LTSC Professional Plus 2024", OfficeTechnology.ClickToRun)]),
                    RemovalCompletion = new TaskCompletionSource<RemovalResult>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                var detectedWindow = new MainWindow(detectedBackend, _ => { confirmations++; return confirmationAllowed; })
                {
                    ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
                };
                detectedWindow.Show();
                var uninstallButton = (Button)detectedWindow.FindName("UninstallButton");
                await WaitUntil(() => uninstallButton.Visibility == Visibility.Visible && uninstallButton.IsEnabled);
                Check(!((Button)detectedWindow.FindName("InstallButton")).IsEnabled, "Office instalado bloqueia a reinstalação sem remoção prévia.");
                await Task.Delay(420);
                SavePreview(detectedWindow, Path.Combine(artifacts, "06-office-detectado.png"));
                uninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(confirmations == 1 && detectedBackend.RemovalCalls == 0, "Recusar confirmação não deve executar remoção.");
                confirmationAllowed = true;
                uninstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => detectedBackend.RemovalCalls == 1 && ((BusyIndicator)detectedWindow.FindName("InstallBusy")).IsActive);
                Check(((BusyIndicator)detectedWindow.FindName("StatusBusy")).IsActive, "A desinstalação também deve mostrar carregamento.");
                SavePreview(detectedWindow, Path.Combine(artifacts, "11-desinstalando.png"));
                detectedBackend.RemovalCompletion.SetResult(new RemovalResult(true, false));
                await WaitUntil(() => detectedBackend.RemovalCalls == 1 && ((TextBlock)detectedWindow.FindName("BadgeText")).Text == "REMOVIDO");
                await WaitUntil(() => ((Button)detectedWindow.FindName("InstallButton")).IsEnabled);
                Check(confirmations == 2 && uninstallButton.Visibility == Visibility.Collapsed, "Confirmar remove o Office e volta a oferecer instalação.");
                SavePreview(detectedWindow, Path.Combine(artifacts, "07-office-removido.png"));
                detectedWindow.Close();

                var stopBackend = new UiInstaller();
                var stopWindow = new MainWindow(stopBackend)
                {
                    ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
                };
                stopWindow.Show();
                var stopInstall = (Button)stopWindow.FindName("InstallButton");
                await WaitUntil(() => stopInstall.IsEnabled);
                stopInstall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var stopButton = (Button)stopWindow.FindName("StopButton");
                await WaitUntil(() => stopBackend.Calls == 1 && stopButton.IsEnabled);
                Check(stopButton.Visibility == Visibility.Visible, "Botão de parar deve aparecer durante a instalação.");
                stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => ((TextBlock)stopWindow.FindName("BadgeText")).Text == "CANCELADO" && stopInstall.IsEnabled);
                Check(stopButton.Visibility == Visibility.Collapsed && ((TextBlock)stopWindow.FindName("StatusTitle")).Text.Contains("removida"),
                    "A interface só confirma parada e remoção após o resultado da limpeza.");
                SavePreview(stopWindow, Path.Combine(artifacts, "08-instalacao-parada.png"));
                stopWindow.Close();

                var brandImage = (Image)window.FindName("BrandLogo");
                foreach (var source in new ImageSource[] { brandImage.Source, window.Icon })
                {
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, 110, 120));
                    var bitmap = new RenderTargetBitmap(110, 120, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var pixels = new byte[110 * 120 * 4];
                    bitmap.CopyPixels(pixels, 110 * 4, 0);
                    Check(pixels[3] == 0 && Enumerable.Range(0, 110 * 120).Any(i => pixels[i * 4 + 3] > 0),
                        "As logos usadas na tela devem ter conteúdo e fundo transparente.");
                }

                var errorBackend = new UiInstaller();
                var errorWindow = new MainWindow(errorBackend)
                {
                    ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
                };
                errorWindow.Show();
                var retryButton = (Button)errorWindow.FindName("InstallButton");
                await WaitUntil(() => retryButton.IsEnabled);
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => errorBackend.Calls == 1);
                errorBackend.Completion.SetException(new InvalidOperationException("Salve seus documentos e feche o Word antes de instalar. Depois, clique em instalar novamente."));
                await WaitUntil(() => ((TextBlock)errorWindow.FindName("BadgeText")).Text == "ATENÇÃO");
                Check(retryButton.IsEnabled && ((TextBlock)errorWindow.FindName("StatusTitle")).Text.Contains("Não foi possível"), "Um erro deve aparecer na tela e permitir nova tentativa.");
                SavePreview(errorWindow, Path.Combine(artifacts, "05-erro.png"));
                var detail = (TextBlock)errorWindow.FindName("StatusDetail");
                detail.Text = string.Join(" ", Enumerable.Repeat("Detalhes de uma mensagem longa de instalação.", 35));
                errorWindow.Width = errorWindow.MinWidth;
                errorWindow.Height = errorWindow.MinHeight;
                errorWindow.UpdateLayout();
                Check(((ScrollViewer)errorWindow.FindName("ContentScroll")).ScrollableHeight > 0,
                    "Mensagens longas devem permitir rolagem no tamanho mínimo.");
                errorWindow.Close();
                Console.WriteLine("PASS: interface WPF, estados, controles, link e prévias.");
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        };
        application.Run(window);
        if (failure is not null) throw new Exception("Falha na verificação da interface.", failure);
    }

    private static void SavePreview(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static async Task RunAboutUiChecks(string artifacts)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "TSCBr-about-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(logPath, "Log inerte para testar a interface de suporte.\nAAAAA-BBBBB-CCCCC-DDDDD-EEEEE\n");
        var context = new AboutContext(new ApplicationHealth(true, true, false), logPath);
        var opened = new List<Uri>();
        string? copied = null;
        var backend = new UiInstaller();
        AboutView? about = null;
        var host = new MainWindow(backend, aboutViewFactory: _ => about = new AboutView(() => context, opened.Add, value => copied = value))
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        try
        {
            host.Show();
            await WaitUntil(() => ((Button)host.FindName("InstallButton")).IsEnabled);
            var aboutButton = (Button)host.FindName("AboutButton");
            Check(aboutButton.IsEnabled && aboutButton.Content is TextBlock { Text: "SOBRE" } label &&
                  label.FontWeight == FontWeights.Bold && ((SolidColorBrush)label.Foreground).Color == Color.FromRgb(0, 212, 255),
                "SOBRE deve estar em maiúsculas, ciano e negrito no cabeçalho.");
            Check(!((Button)host.FindName("CopyLogButton")).IsEnabled, "Copiar log inicial deve aguardar uma operação com log.");
            var windowCount = Application.Current.Windows.Count;
            var initialSize = new Size(host.Width, host.Height);
            var initialStatus = ((TextBlock)host.FindName("StatusTitle")).Text;
            aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => about?.IsLoaded == true);
            Check(Application.Current.Windows.Count == windowCount && Window.GetWindow(about) == host &&
                  ((ScrollViewer)host.FindName("ContentScroll")).Visibility == Visibility.Collapsed &&
                  new Size(host.Width, host.Height) == initialSize,
                "Sobre deve abrir dentro da mesma janela, mantendo o tamanho original.");
            await Task.Delay(430);
            Check(about!.Background is SolidColorBrush background && background.Color != Colors.White &&
                  about.Foreground is SolidColorBrush foreground && foreground.Color == Colors.White,
                "Sobre deve manter o tema escuro e texto legível.");
            Check(((StackPanel)about.FindName("HelpActions")).Children.Count == 2 &&
                  about.FindName("AboutCopyLogButton") is Button,
                "Ajuda deve ter exatamente duas ações: site e copiar log.");
            Check(about.FindName("SupportWhatsAppButton") is null && about.FindName("DeveloperWhatsAppButton") is null,
                "A versão pública não deve expor números de telefone.");
            var healthValue = (TextBlock)about.FindName("HealthValue");
            var healthDot = (System.Windows.Shapes.Ellipse)about.FindName("HealthDot");
            Check(((TextBlock)about.FindName("VersionValue")).Text == ApplicationInfo.Version &&
                  ((TextBlock)about.FindName("RuntimeValue")).Text == ApplicationInfo.Runtime,
                "Sobre deve usar versão e runtime reais do executável.");
            Check(healthValue.Text == "Funcionando" && ((SolidColorBrush)healthDot.Fill).Color == Color.FromRgb(34, 197, 94),
                "Indicador saudável deve ser verde.");
            var badge = (Image)((Border)about.FindName("DeveloperBadge")).Child;
            Check(badge.Source is BitmapSource bitmap && bitmap.PixelWidth > 0, "A marca deve estar incorporada e ser carregada sem arquivos externos.");
            SavePreview(host, Path.Combine(artifacts, "16-sobre-compacto.png"));
            foreach (var (name, uri) in new[]
                     { ("LinkedInButton", ApplicationLinks.LinkedIn), ("GitHubButton", ApplicationLinks.GitHub),
                       ("WebsiteButton", ApplicationLinks.Website) })
            {
                var button = (Button)about.FindName(name);
                Check(button.Content is Image && button.ToolTip is not null, "Links sociais devem usar somente ícones, com descrição acessível.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened.Last() == uri, "Clique deve encaminhar ao destino correto sem enviar mensagens automaticamente.");
            }
            var copyLog = (Button)about.FindName("AboutCopyLogButton");
            copyLog.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(copied?.Contains("Log inerte") == true && copied.Contains("[chave omitida]") &&
                  !copied.Contains("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"), "Copiar log deve copiar o log real, com chaves omitidas, em vez do diagnóstico.");
            context = new AboutContext(new(false, true, false), logPath);
            about.RefreshStatus();
            Check(healthValue.Text == "Requer atenção" && ((SolidColorBrush)healthDot.Fill).Color == Color.FromRgb(240, 120, 120) &&
                  ((TextBlock)about.FindName("HealthDetail")).Text.Contains("administrador"), "Ausência de administrador deve ficar vermelha e explicar o motivo.");
            SavePreview(host, Path.Combine(artifacts, "17-sobre-falha.png"));
            context = new AboutContext(new(true, true, true, "Falha simulada de conexão."), null);
            about.RefreshStatus();
            copied = null;
            copyLog.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBlock)about.FindName("HealthDetail")).Text.Contains("Falha simulada") && copied is null &&
                  ((TextBlock)about.FindName("SupportFeedback")).Text.Contains("Nenhum log disponível"),
                "Ausência de log deve explicar o motivo sem copiar informações fictícias.");
            context = new AboutContext(new(true, true, false), logPath);
            await WaitUntil(() => healthValue.Text == "Funcionando");
            Check(about.IsRefreshing, "Indicador deve atualizar enquanto Sobre estiver visível.");
            host.Width = 820;
            host.Height = 720;
            await Task.Delay(100);
            host.UpdateLayout();
            Check(Grid.GetColumn((Border)about.FindName("HelpCard")) == 1,
                "Sobre deve aproveitar uma janela larga sem criar outra janela.");
            SavePreview(host, Path.Combine(artifacts, "15-sobre.png"));
            ((ScrollViewer)about.FindName("AboutScroll")).ScrollToEnd();
            host.UpdateLayout();
            SavePreview(host, Path.Combine(artifacts, "19-sobre-ajuda.png"));
            host.Width = host.MinWidth;
            host.Height = host.MinHeight;
            await Task.Delay(100);
            host.UpdateLayout();
            Check(Grid.GetColumn((Border)about.FindName("HelpCard")) == 0 &&
                  Grid.GetRow((Border)about.FindName("DeveloperCard")) == 2 &&
                  ((ScrollViewer)about.FindName("AboutScroll")).ScrollableHeight > 0,
                "Sobre deve usar uma coluna com rolagem no tamanho mínimo.");
            ((Button)about.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!about.IsRefreshing && ((ScrollViewer)host.FindName("ContentScroll")).IsVisible &&
                  ((TextBlock)host.FindName("StatusTitle")).Text == initialStatus,
                "Voltar deve restaurar a tela de instalação e parar o timer da área oculta.");
            aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(((ContentControl)host.FindName("AboutHost")).Content, about) && about.IsRefreshing &&
                  Application.Current.Windows.Count == windowCount, "Reabrir Sobre deve reutilizar a área dentro da mesma janela.");
            ((Button)about.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)host.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => backend.Calls == 1);
            aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            backend.Completion.SetResult(new InstallResult(false, true, "16.0.00000.00000"));
            await WaitUntil(() => ((TextBlock)host.FindName("BadgeText")).Text == "CONCLUÍDO");
            ((Button)about.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(backend.Calls == 1 && ((TextBlock)host.FindName("StatusTitle")).Text.Contains("instalado com sucesso"),
                "Consultar Sobre durante a instalação deve preservar o processo e seu resultado.");
        }
        finally { host.Close(); }
    }

    private static async Task RunStartupChild(string artifacts)
    {
        var start = new ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory, "TSCBr.OfficeInstaller.Verification.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--startup-preview");
        start.ArgumentList.Add(artifacts);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        if (child.ExitCode != 0) throw new Exception(await error);
        Console.Write(await output);
        Check(child.ExitCode == 0, "Fluxo real da splash para a janela principal deve funcionar com backend simulado.");
    }

    private static int RunStartupPreview(string artifacts)
    {
        var backend = new UiInstaller();
        var application = new App { InstallerFactory = () => backend };
        application.InitializeComponent();
        Exception? failure = null;
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await WaitUntil(() => application.MainWindow is SplashWindow);
                var splash = (SplashWindow)application.MainWindow;
                splash.ShowInTaskbar = false;
                splash.Left = -10000;
                splash.Top = -10000;
                await Task.Delay(80);
                Check(((ProgressBar)splash.FindName("StartupLoading")).IsIndeterminate, "Splash deve mostrar carregamento.");
                Check(((BusyIndicator)splash.FindName("StartupBusy")).IsActive, "Splash deve ter indicador de carregamento ativo.");
                SavePreview(splash, System.IO.Path.Combine(artifacts, "09-carregamento.png"));
                await WaitUntil(() => application.MainWindow is MainWindow);
                var window = (MainWindow)application.MainWindow;
                window.ShowInTaskbar = false;
                window.Left = -10000;
                window.Top = -10000;
                await WaitUntil(() => ((Button)window.FindName("InstallButton")).IsEnabled);
                await Task.Delay(420);
                Check(!splash.IsVisible && backend.DetectionCalls == 1, "Splash deve fechar e a janela principal deve reutilizar a detecção inicial.");
                SavePreview(window, System.IO.Path.Combine(artifacts, "10-inicializado.png"));
                Console.WriteLine("PASS: splash, preparação local e transição para janela compacta.");
            }
            catch (Exception ex) { failure = ex; }
            finally { application.Shutdown(failure is null ? 0 : 1); }
        }));
        application.Run();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static async Task MustFail<T>(Func<Task> operation, string message) where T : Exception
    {
        try { await operation(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("O estado esperado não foi alcançado.");
            await Task.Delay(20);
        }
    }

    private sealed class InlineProgress(Action<InstallProgress>? action = null) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => action?.Invoke(value);
    }

    private sealed class FakePlatform : IInstallationPlatform
    {
        public int ExitCode { get; init; }
        public int RemovalExitCode { get; init; }
        public bool PreserveAfterRemoval { get; init; }
        public InstalledOffice? Installed { get; init; } = new("16.0.17932.00000");
        public OfficeInventory Inventory = OfficeInventory.Empty;
        public bool RejectSignature { get; init; }
        public TaskCompletionSource<int>? Gate { get; init; }
        public int Calls;
        public string? LastSetup;
        public string? LastConfiguration;
        public string? LastWorkingDirectory;
        public XDocument? RemovalConfiguration;
        public bool CancellationObserved;
        public List<string> RemovedMsi = new();
        public IDisposable AcquireInstallationLock() => new MemoryStream();
        public void ValidateMachine(bool installing = true) { }
        public void PrepareDirectory(string path) => Directory.CreateDirectory(path);
        public void PrepareFile(string path) { }
        public void VerifyMicrosoftInstaller(string path) { if (RejectSignature) throw new SecurityException("Assinatura inválida."); }
        public async Task<int> RunSetupAsync(string setupPath, string configPath, string workingDirectory, CancellationToken cancellationToken = default)
        {
            LastSetup = setupPath;
            LastConfiguration = configPath;
            LastWorkingDirectory = workingDirectory;
            Interlocked.Increment(ref Calls);
            var configuration = XDocument.Load(configPath);
            if (configuration.Root?.Element("Remove") is { } removal)
            {
                RemovalConfiguration = configuration;
                if (!PreserveAfterRemoval && RemovalExitCode is 0 or 3010)
                    Inventory = new OfficeInventory(Inventory.Products.Where(p => p.Technology == OfficeTechnology.Msi ||
                        ((string?)removal.Attribute("All") != "TRUE" && p.Id != OfficeConfiguration.ProductId)).ToArray());
                return RemovalExitCode;
            }
            Inventory = new OfficeInventory([new OfficeProduct(OfficeConfiguration.ProductId, "Office LTSC 2024", OfficeTechnology.ClickToRun)]);
            try { return Gate is null ? ExitCode : await Gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        public InstalledOffice? ReadInstalledOffice() => Installed;
        public OfficeInventory ReadOfficeInventory() => Inventory;
        public void DeleteStagedFile(string root, string relativePath) => new WindowsInstallationPlatform().DeleteStagedFile(root, relativePath);
        public Task<int> UninstallMsiAsync(string productCode)
        {
            RemovedMsi.Add(productCode);
            if (!PreserveAfterRemoval)
                Inventory = new OfficeInventory(Inventory.Products.Where(p => p.Id != productCode).ToArray());
            return Task.FromResult(RemovalExitCode);
        }
    }

    private sealed class UiInstaller : IOfficeInstaller
    {
        public string? LogPath => null;
        public int Calls;
        public int RemovalCalls;
        public int DetectionCalls;
        public OfficeInventory Inventory = OfficeInventory.Empty;
        public TaskCompletionSource<InstallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RemovalResult>? RemovalCompletion { get; init; }
        public Task<OfficeInventory> DetectOfficeAsync()
        {
            Interlocked.Increment(ref DetectionCalls);
            return Task.FromResult(Inventory);
        }
        public async Task<RemovalResult> UninstallAsync(IProgress<InstallProgress> progress)
        {
            Interlocked.Increment(ref RemovalCalls);
            progress.Report(new InstallProgress("Desinstalando Microsoft Office", "Aguarde a remoção dos aplicativos Office.", CanStop: false));
            var result = RemovalCompletion is null ? new RemovalResult(true, false) : await RemovalCompletion.Task;
            if (result.RemovalVerified) Inventory = OfficeInventory.Empty;
            return result;
        }
        public async Task<InstallResult> InstallAsync(IProgress<InstallProgress> progress, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            progress.Report(new InstallProgress("Instalando Microsoft Office", "Acompanhe a janela oficial da Microsoft. O download e a instalação podem levar alguns minutos."));
            try
            {
                var result = await Completion.Task.WaitAsync(cancellationToken);
                if (result.InstallationVerified) Inventory = new OfficeInventory([new OfficeProduct(OfficeConfiguration.ProductId, "Microsoft Office LTSC Professional Plus 2024", OfficeTechnology.ClickToRun)]);
                return result;
            }
            catch (OperationCanceledException) { throw new InstallationStoppedException(new RemovalResult(true, false)); }
        }
    }
}
