using System.Text.RegularExpressions;

namespace TSCBr.OfficeInstaller.Services;

public static class LogSanitizer
{
    // Logs are shared with support: strip terminal color codes and mask anything shaped like a product key.
    public static string Sanitize(string value)
    {
        var text = Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        return Regex.Replace(text, @"\b[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\b", "[chave omitida]", RegexOptions.IgnoreCase);
    }
}
