using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TSCBr.OfficeInstaller.Services;

internal static class ClickToRunService
{
    public static async Task InterruptAsync()
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, "ClickToRunSvc", 0x34); // Query, stop and start only this service.
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1060) return; // Not installed yet.
            throw new Win32Exception(error);
        }
        var status = ReadStatus(service);
        if (status.State != 1)
        {
            if (!ControlService(service, 1, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not (1061 or 1062 or 1052)) throw new Win32Exception(error);
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((status = ReadStatus(service)).State != 1 && DateTime.UtcNow < deadline) await Task.Delay(200);
            if (status.State != 1 && status.ProcessId != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(checked((int)status.ProcessId));
                    if (!process.ProcessName.Equals("OfficeClickToRun", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("O processo do serviço Office não pôde ser identificado para interromper a instalação.");
                    MicrosoftSignature.Verify(process.MainModule!.FileName);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (ArgumentException) { } // Already exited.
                catch (InvalidOperationException) when (ReadStatus(service).State == 1) { }
            }
            await WaitForState(service, 1);
        }

        // The official removal needs the service; never leave it disabled or permanently stopped.
        if (!StartService(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1056) throw new Win32Exception(error);
        }
        await WaitForState(service, 4);
    }

    private static ServiceStatusProcess ReadStatus(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    private static async Task WaitForState(ServiceHandle service, uint state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (ReadStatus(service).State != state)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("O serviço de instalação do Office não respondeu. Reinicie o Windows e use a opção de desinstalação.");
            await Task.Delay(200);
        }
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(ServiceHandle service, int count, IntPtr arguments);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
