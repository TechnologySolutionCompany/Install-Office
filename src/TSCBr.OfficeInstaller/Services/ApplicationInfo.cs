using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TSCBr.OfficeInstaller.Services;

public static class ApplicationInfo
{
    public static string Version { get; } = typeof(ApplicationInfo).Assembly.GetName().Version?.ToString(3) ?? "—";
    public static string DisplayVersion => "v" + Version;
    public static string BuildCaption => DisplayVersion + " · TSCBr Technology Solution Company Br";
    public static string Runtime => RuntimeInformation.FrameworkDescription + " · WPF";
    public static string Platform => "Windows " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static ApplicationHealth ReadHealth(bool operationFailed, string? detail, bool busy, bool detectionFailed)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var compatible = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
                         RuntimeInformation.OSArchitecture == Architecture.X64;
        return new(administrator, compatible, operationFailed || detectionFailed, detail, busy);
    }
}

public sealed record ApplicationHealth(bool IsAdministrator, bool IsCompatible, bool OperationFailed,
    string? Detail = null, bool IsBusy = false)
{
    public bool IsHealthy => IsAdministrator && IsCompatible && !OperationFailed;
    public string Status => IsHealthy ? "Funcionando" : "Requer atenção";
    public string Description => !IsAdministrator ? "O aplicativo precisa ser executado como administrador."
        : !IsCompatible ? "O computador precisa de Windows x64 compatível com o Office LTSC 2024."
        : OperationFailed ? Detail ?? "A última operação apresentou uma falha. Consulte o log para mais informações."
        : IsBusy ? "Uma operação do Office está em andamento. Aguarde a conclusão."
        : "Aplicativo pronto, com permissões de administrador e plataforma compatível.";
}

public sealed record AboutContext(ApplicationHealth Health, string? LogPath)
{
    public string DiagnosticText()
    {
        var text = new StringBuilder();
        text.AppendLine("TSCBr — Install Office");
        text.AppendLine("Versão: " + ApplicationInfo.Version);
        text.AppendLine("Plataforma: " + ApplicationInfo.Platform);
        text.AppendLine("Windows: " + RuntimeInformation.OSDescription);
        text.AppendLine("Runtime: " + ApplicationInfo.Runtime);
        text.AppendLine("Administrador: " + (Health.IsAdministrator ? "Sim" : "Não"));
        text.AppendLine("Funcionabilidade: " + Health.Status);
        text.AppendLine("Estado: " + LogSanitizer.Sanitize(Health.Description));
        text.AppendLine("Log disponível: " + (LogPath is not null && File.Exists(LogPath) ? "Sim" : "Não"));
        text.AppendLine("Data: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        return text.ToString();
    }
}
