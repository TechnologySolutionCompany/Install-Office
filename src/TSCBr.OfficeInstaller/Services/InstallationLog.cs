using System.Text;

namespace TSCBr.OfficeInstaller.Services;

public sealed class InstallationLog
{
    private readonly string _directory;
    public string? Path { get; private set; }

    public InstallationLog(string? directory = null) => _directory = directory ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TSCBr", "OfficeInstaller", "Logs");

    public void Start(IInstallationPlatform platform, string operation = "instalacao")
    {
        platform.PrepareDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, $"{operation}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        Write("TSCBr Office Installer " + ApplicationInfo.Version + " — início da operação. Nenhuma chave de produto é registrada neste log.");
        platform.PrepareFile(Path);
    }

    public void Write(string message)
    {
        if (Path is null) return;
        File.AppendAllText(Path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
    }

    public void TryWrite(string message)
    {
        try { Write(message); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
