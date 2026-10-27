namespace TSCBr.OfficeInstaller.Services;

public sealed record InstallProgress(string Title, string Detail, double? Percent = null, bool CanStop = true);

public sealed record InstalledOffice(string Version);

public sealed record InstallResult(bool RestartRequired, bool InstallationVerified, string? Version);

public enum OfficeTechnology { ClickToRun, Msi }
public sealed record OfficeProduct(string Id, string Name, OfficeTechnology Technology);
public sealed record OfficeInventory(IReadOnlyList<OfficeProduct> Products)
{
    public bool IsInstalled => Products.Count > 0;
    public static OfficeInventory Empty { get; } = new(Array.Empty<OfficeProduct>());
}
public sealed record RemovalResult(bool RemovalVerified, bool RestartRequired);

public sealed class InstallationStoppedException(RemovalResult cleanup)
    : OperationCanceledException(cleanup.RemovalVerified
        ? "A instalação foi interrompida e a remoção do Office adicionado foi confirmada."
        : "A instalação foi interrompida. Reinicie o Windows para concluir a remoção e confira o estado do Office.")
{
    public RemovalResult Cleanup { get; } = cleanup;
}

public interface IOfficeInstaller
{
    string? LogPath { get; }
    Task<InstallResult> InstallAsync(IProgress<InstallProgress> progress, CancellationToken cancellationToken = default);
    Task<OfficeInventory> DetectOfficeAsync();
    Task<RemovalResult> UninstallAsync(IProgress<InstallProgress> progress);
}

// Windows operations are replaceable so verification never needs to run the real setup.
public interface IInstallationPlatform
{
    IDisposable AcquireInstallationLock();
    void ValidateMachine(bool installing = true);
    void PrepareDirectory(string path);
    void PrepareFile(string path);
    void VerifyMicrosoftInstaller(string path);
    Task<int> RunSetupAsync(string setupPath, string configPath, string workingDirectory, CancellationToken cancellationToken = default);
    Task<int> UninstallMsiAsync(string productCode);
    OfficeInventory ReadOfficeInventory();
    void DeleteStagedFile(string root, string relativePath);
    InstalledOffice? ReadInstalledOffice();
}
