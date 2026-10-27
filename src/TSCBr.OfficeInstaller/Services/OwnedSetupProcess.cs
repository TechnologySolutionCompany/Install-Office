using System.ComponentModel;
using System.Diagnostics;

namespace TSCBr.OfficeInstaller.Services;

public static class OwnedSetupProcess
{
    public static async Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken,
        Func<Task> interruptOfficeService)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador da Microsoft.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            // Click-to-Run is launched by SCM, so it is not necessarily a child of setup.exe.
            await interruptOfficeService();
            throw;
        }
    }
}
