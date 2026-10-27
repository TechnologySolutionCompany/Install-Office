using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;

namespace TSCBr.OfficeInstaller.Services;

public static class MicrosoftSignature
{
    public static void Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var trust = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UIChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = pointer,
                ProviderFlags = 0x1000 | 0x10 // Cache-only verification, no online revocation fetch.
            };
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
            if (result != 0)
                throw new SecurityException($"A assinatura digital do setup.exe não pôde ser validada (0x{result:X8}). Verifique os arquivos originais e os certificados do Windows.");

            // X509CertificateLoader reads DER/PEM certificates, not Authenticode PE files.
            // Keep the signed-file API here, only after WinVerifyTrust has succeeded.
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signed);
            if (!certificate.GetNameInfo(X509NameType.SimpleName, false).Equals("Microsoft Corporation", StringComparison.Ordinal))
                throw new SecurityException("O setup.exe não foi assinado pela Microsoft Corporation.");
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr SIPClient;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }
}
