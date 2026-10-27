using System.Diagnostics;
using System.Text;

namespace TSCBr.OfficeInstaller.Services;

public static class SupportActions
{
    public static void OpenLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("O link precisa usar HTTPS.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void OpenLog(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"))
        { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    public static string ReadLog(string path)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var truncated = input.Length > maximumBytes;
        if (truncated) input.Seek(-maximumBytes, SeekOrigin.End);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // Start at the next full line if the tail begins inside a UTF-8 sequence or a product key.
        if (truncated) reader.ReadLine();
        return (truncated ? "[Trecho final do log — limitado a 2 MiB]\n" : "") + LogSanitizer.Sanitize(reader.ReadToEnd());
    }
}
