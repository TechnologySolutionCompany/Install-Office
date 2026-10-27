namespace TSCBr.OfficeInstaller.Services;

public sealed class OfficeInstaller : IOfficeInstaller
{
    public const string InstallationDirectory = @"C:\MS OFFICE SETUP";
    private readonly IInstallationPlatform _platform;
    private readonly EmbeddedPayload _payload;
    private readonly InstallationLog _log;
    private readonly string _destination;
    private readonly TimeSpan _verificationWait;
    private int _running;

    public string? LogPath => _log.Path;

    public OfficeInstaller() : this(new WindowsInstallationPlatform(), new EmbeddedPayload(),
        new InstallationLog(), InstallationDirectory, TimeSpan.FromSeconds(30)) { }

    public OfficeInstaller(IInstallationPlatform platform, EmbeddedPayload payload, InstallationLog log,
        string destination, TimeSpan verificationWait)
    {
        _platform = platform;
        _payload = payload;
        _log = log;
        _destination = destination;
        _verificationWait = verificationWait;
    }

    public Task<OfficeInventory> DetectOfficeAsync() => Task.Run(_platform.ReadOfficeInventory);

    public Task<InstallResult> InstallAsync(IProgress<InstallProgress> progress, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            using var machineLock = _platform.AcquireInstallationLock();
            _log.Start(_platform);
            var newFiles = NewStagingFiles();
            var setupStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new("Verificando o computador", "Conferindo permissões, espaço disponível e aplicativos abertos."));
                _platform.ValidateMachine();
                // An existing Office cannot be restored by uninstalling a replacement.
                if (_platform.ReadOfficeInventory().IsInstalled)
                    throw new InvalidOperationException("O Office já está instalado. Use a opção de desinstalar todo o Office antes de iniciar uma nova instalação.");
                using (var original = _payload.Open(OfficeConfiguration.SourceFileName))
                    OfficeConfiguration.CreateForInstallation(original);
                _log.Write("Pré-requisitos conferidos. Nenhum Office anterior detectado.");
                await _payload.ExtractAsync(_destination, _platform, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var runtime = Path.Combine(_destination, OfficeConfiguration.RuntimeFileName);
                using (var original = _payload.Open(OfficeConfiguration.SourceFileName))
                    SaveConfiguration(OfficeConfiguration.CreateForInstallation(original), runtime);
                var setup = VerifiedSetup();
                cancellationToken.ThrowIfCancellationRequested();

                progress.Report(new("Instalando Microsoft Office", "Acompanhe a janela da Microsoft. Para interromper e remover esta instalação, use o botão Parar."));
                _log.Write("Iniciando instalação oficial. Ativação automática desabilitada.");
                setupStarted = true;
                var code = await _platform.RunSetupAsync(setup, runtime, _destination, cancellationToken);
                _log.Write("Instalador encerrado com código " + code + ".");
                if (code == 1602) throw new OperationCanceledException("Instalação cancelada na janela da Microsoft.");
                CheckExitCode(code, "instalar");
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new("Verificando a instalação", "Confirmando o Office LTSC Professional Plus 2024 de 64 bits."));
                var installed = await PollAsync(_platform.ReadInstalledOffice, value => value is not null, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var restart = NeedsRestart(code);
                if (installed is null && !restart)
                    throw new InvalidOperationException("O instalador encerrou, mas o Office LTSC 2024 de 64 bits não foi confirmado. Consulte o log.");
                _log.Write(installed is null ? "Reinicialização necessária; instalação não confirmada." : "Instalação confirmada. Versão: " + installed.Version);
                return new InstallResult(restart, installed is not null, installed?.Version);
            }
            catch (OperationCanceledException)
            {
                // Keep the machine lock through rollback. Cleanup itself cannot be canceled.
                _log.Write("Parada solicitada. Iniciando limpeza desta instalação.");
                progress.Report(new("Removendo a instalação interrompida", "Aguarde a limpeza oficial do Office. O aplicativo continuará aberto até o resultado.", CanStop: false));
                try
                {
                    var cleanup = setupStarted ? await RemoveCoreAsync(allProducts: false, progress) : new RemovalResult(true, false);
                    RemoveNewStagingFiles(newFiles);
                    _log.Write(cleanup.RemovalVerified ? "Parada e remoção confirmadas." : "Parada concluída; remoção depende de reinicialização.");
                    throw new InstallationStoppedException(cleanup);
                }
                catch (InstallationStoppedException) { throw; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("A instalação foi interrompida, mas a limpeza não foi concluída. Use Desinstalar todo o Office ou reinicie o Windows e tente novamente. Detalhe: " + ex.Message, ex);
                }
            }
        });

    public Task<RemovalResult> UninstallAsync(IProgress<InstallProgress> progress) => RunExclusiveAsync(async () =>
    {
        using var machineLock = _platform.AcquireInstallationLock();
        _log.Start(_platform);
        _platform.ValidateMachine(installing: false);
        progress.Report(new("Desinstalando todo o Office", "Removendo os aplicativos Office, Visio e Project detectados. Aguarde a conclusão.", CanStop: false));
        var newFiles = NewStagingFiles();
        var result = await RemoveCoreAsync(allProducts: true, progress);
        RemoveNewStagingFiles(newFiles);
        _log.Write(result.RemovalVerified ? "Desinstalação completa confirmada." : "Desinstalação depende de reinicialização.");
        return result;
    });

    private async Task<RemovalResult> RemoveCoreAsync(bool allProducts, IProgress<InstallProgress> progress)
    {
        var before = _platform.ReadOfficeInventory();
        var restart = false;
        // An interrupted setup can leave partial C2R content without product registration.
        if (!allProducts || before.Products.Any(x => x.Technology == OfficeTechnology.ClickToRun))
        {
            await _payload.ExtractAsync(_destination, _platform, new CleanupProgress(progress));
            var configuration = Path.Combine(_destination, OfficeConfiguration.RemovalFileName);
            SaveConfiguration(OfficeConfiguration.CreateForRemoval(allProducts), configuration);
            var code = await _platform.RunSetupAsync(VerifiedSetup(), configuration, _destination);
            _log.Write("Remoção Click-to-Run retornou " + code + ". Escopo: " + (allProducts ? "todo o Office" : OfficeConfiguration.ProductId));
            CheckExitCode(code, "remover");
            restart |= NeedsRestart(code);
        }
        if (allProducts)
        {
            // Use native MSI product codes; never execute uninstall command strings from the registry.
            foreach (var product in _platform.ReadOfficeInventory().Products.Where(x => x.Technology == OfficeTechnology.Msi))
            {
                progress.Report(new("Removendo versão anterior do Office", product.Name, CanStop: false));
                var code = await _platform.UninstallMsiAsync(product.Id);
                _log.Write("Remoção MSI " + product.Id + " retornou " + code + ".");
                if (code is not (1605 or 1614)) CheckExitCode(code, "remover a versão MSI");
                restart |= NeedsRestart(code);
            }
        }

        progress.Report(new("Verificando a remoção", "Conferindo se os produtos selecionados foram removidos.", CanStop: false));
        bool IsRemoved(OfficeInventory inventory) => allProducts ? !inventory.IsInstalled :
            !inventory.Products.Any(x => x.Technology == OfficeTechnology.ClickToRun &&
                (x.Id.Equals(OfficeConfiguration.ProductId, StringComparison.OrdinalIgnoreCase) || x.Id == "UnregisteredOffice"));
        var after = await PollAsync(_platform.ReadOfficeInventory, IsRemoved, CancellationToken.None);
        if (!IsRemoved(after) && !restart)
            throw new InvalidOperationException("Ainda há componentes do Office registrados: " + string.Join(", ", after.Products.Select(x => x.Name)) + ". Consulte o log.");
        return new RemovalResult(IsRemoved(after), restart);
    }

    private async Task<T> PollAsync<T>(Func<T> read, Func<T, bool> ready, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + _verificationWait;
        do
        {
            token.ThrowIfCancellationRequested();
            var value = read();
            if (ready(value) || DateTime.UtcNow >= deadline) return value;
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        } while (true);
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma operação do Office em andamento neste aplicativo.");
        try { return await operation(); }
        catch (Exception ex) { _log.TryWrite(ex.GetType().Name + ": " + ex.Message); throw; }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private string[] NewStagingFiles() => _payload.Files.Concat(new[] { OfficeConfiguration.RuntimeFileName, OfficeConfiguration.RemovalFileName })
        .Where(relative => !File.Exists(Path.Combine(_destination, relative))).ToArray();

    private void RemoveNewStagingFiles(IEnumerable<string> files)
    {
        foreach (var relative in files) _platform.DeleteStagedFile(_destination, relative);
    }

    private string VerifiedSetup()
    {
        var path = Path.Combine(_destination, "setup.exe");
        _platform.VerifyMicrosoftInstaller(path);
        return path;
    }

    private void SaveConfiguration(System.Xml.Linq.XDocument document, string path)
    {
        _platform.PrepareFile(path);
        document.Save(path);
        _platform.PrepareFile(path);
    }

    private static bool NeedsRestart(int code) => code is 3010 or 1641;
    private static void CheckExitCode(int code, string action)
    {
        if (code is not (0 or 3010 or 1641))
            throw new InvalidOperationException($"O instalador retornou o código {code} ao {action} o Office. Consulte o log.");
    }

    private sealed class CleanupProgress(IProgress<InstallProgress> inner) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress progress) => inner.Report(progress with { CanStop = false });
    }
}
