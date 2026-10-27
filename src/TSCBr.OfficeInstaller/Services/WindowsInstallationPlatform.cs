using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace TSCBr.OfficeInstaller.Services;

public sealed class WindowsInstallationPlatform : IInstallationPlatform
{
    private static readonly string[] OfficeProcesses =
        ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "MSACCESS", "ONENOTE", "MSPUB", "VISIO", "WINPROJ", "lync"];

    public IDisposable AcquireInstallationLock()
    {
        var companyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TSCBr");
        PrepareDirectory(companyDirectory);
        var directory = Path.Combine(companyDirectory, "OfficeInstaller");
        PrepareDirectory(directory);
        var lockPath = Path.Combine(directory, "installation.lock");
        PrepareFile(lockPath);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            throw new InvalidOperationException("Outra instância da TSCBr já está executando uma operação no Office. Aguarde a conclusão.", ex);
        }
    }

    public void ValidateMachine(bool installing = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new InvalidOperationException("Este instalador requer Windows de 64 bits compatível com o Office LTSC 2024.");

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Feche o aplicativo e execute TSCBr_InstaladorOffice.exe como administrador.");

        var drive = new DriveInfo(Path.GetPathRoot(OfficeInstaller.InstallationDirectory)!);
        if (installing && drive.AvailableFreeSpace < 6L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Libere pelo menos 6 GB na unidade C: antes de instalar o Office.");

        var openApps = new List<string>();
        foreach (var name in OfficeProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) openApps.Add(name);
            foreach (var process in processes) process.Dispose();
        }
        if (openApps.Count > 0)
            throw new InvalidOperationException("Salve seus documentos e feche estes aplicativos antes de continuar: " + string.Join(", ", openApps) + ". Depois, tente novamente.");

        if (!installing) return;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var office = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        var platform = office?.GetValue("Platform") as string;
        var products = office?.GetValue("ProductReleaseIds") as string;
        if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("x64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Há uma instalação Click-to-Run do Office de 32 bits. Desinstale a edição de 32 bits antes de instalar a de 64 bits.");
        if (!string.IsNullOrWhiteSpace(products) && !HasTargetProduct(products))
            throw new InvalidOperationException("Há outra edição Click-to-Run do Office instalada. Remova a edição incompatível em Configurações > Aplicativos antes de instalar o LTSC Professional Plus 2024.");
    }

    public void PrepareDirectory(string path)
    {
        RejectReparsePoints(path);
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var (sid, rights) in AccessRules())
            acl.AddAccessRule(new FileSystemAccessRule(sid, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    public void PrepareFile(string path)
    {
        RejectReparsePoints(path);
        if (!File.Exists(path)) return;
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var (sid, rights) in AccessRules())
            acl.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static IEnumerable<(SecurityIdentifier Sid, FileSystemRights Rights)> AccessRules()
    {
        yield return (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        yield return (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        yield return (new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
    }

    private static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            // GetAttributes also catches directory junctions and symbolic links.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A pasta de instalação contém um link ou redirecionamento. Use uma pasta local comum: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public void VerifyMicrosoftInstaller(string path) => MicrosoftSignature.Verify(path);

    public Task<int> RunSetupAsync(string setupPath, string configPath, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = setupPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/configure");
        start.ArgumentList.Add(configPath);
        return OwnedSetupProcess.RunAsync(start, cancellationToken, ClickToRunService.InterruptAsync);
    }

    public OfficeInventory ReadOfficeInventory() => OfficeInventoryReader.Read();

    public async Task<int> UninstallMsiAsync(string productCode)
    {
        if (!Guid.TryParse(productCode, out var code))
            throw new InvalidDataException("Código de produto MSI inválido.");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "/x", code.ToString("B"), "/passive", "/norestart" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a remoção MSI.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public void DeleteStagedFile(string root, string relativePath)
    {
        var allowedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(allowedRoot, relativePath));
        if (!target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Caminho de limpeza fora da pasta de instalação.");
        RejectReparsePoints(target);
        File.Delete(target); // Only an explicitly tracked file, never a recursive folder deletion.
    }

    public InstalledOffice? ReadInstalledOffice()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var office = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        if (!string.Equals(office?.GetValue("Platform") as string, "x64", StringComparison.OrdinalIgnoreCase) ||
            !HasTargetProduct(office?.GetValue("ProductReleaseIds") as string)) return null;

        var version = office?.GetValue("VersionToReport") as string;
        if (!Version.TryParse(version, out _)) return null;
        var installationPath = office?.GetValue("InstallationPath") as string;
        var locations = new[]
        {
            installationPath is null ? null : Path.Combine(installationPath, "root", "Office16"),
            installationPath is null ? null : Path.Combine(installationPath, "Office16"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office", "root", "Office16")
        };
        var applicationsPresent = locations.Where(x => x is not null).Any(folder =>
            new[] { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE" }.All(file => File.Exists(Path.Combine(folder!, file))));
        return applicationsPresent ? new InstalledOffice(version!) : null;
    }

    private static bool HasTargetProduct(string? products) => products?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains(OfficeConfiguration.ProductId, StringComparer.OrdinalIgnoreCase) == true;
}
