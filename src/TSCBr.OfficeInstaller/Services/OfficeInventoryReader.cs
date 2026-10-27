using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace TSCBr.OfficeInstaller.Services;

public static class OfficeInventoryReader
{
    // Registry reads only. Win32_Product is deliberately avoided: enumeration can repair MSI packages.
    public static OfficeInventory Read()
    {
        var products = new Dictionary<string, OfficeProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var clickToRun = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            foreach (var id in ((clickToRun?.GetValue("ProductReleaseIds") as string) ?? "")
                         .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Regex.IsMatch(id, @"^[a-zA-Z0-9_.-]+$")) continue;
                products.TryAdd("c2r:" + id, new OfficeProduct(id, ProductName(id), OfficeTechnology.ClickToRun));
            }

            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(keyName);
                var name = entry?.GetValue("DisplayName") as string;
                var publisher = entry?.GetValue("Publisher") as string;
                if (!IsOfficeEntry(name, publisher)) continue;
                if (entry?.GetValue("WindowsInstaller") is int flag && flag == 1 && Guid.TryParse(keyName, out var code))
                {
                    var id = code.ToString("B").ToUpperInvariant();
                    products.TryAdd("msi:" + id, new OfficeProduct(id, name!, OfficeTechnology.Msi));
                }
                else if (!products.Values.Any(p => p.Technology == OfficeTechnology.ClickToRun) &&
                         (entry?.GetValue("UninstallString") as string)?.Contains("OfficeClickToRun", StringComparison.OrdinalIgnoreCase) == true)
                {
                    products.TryAdd("c2r:" + keyName, new OfficeProduct(keyName, name!, OfficeTechnology.ClickToRun));
                }
            }
        }
        if (products.Count == 0)
        {
            // Detect executable remnants as well as registry records after a forced interruption.
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase);
            if (folders.Any(folder => new[] { "root\\Office16", "Office16" }.Any(subfolder =>
                    new[] { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE", "MSACCESS.EXE", "VISIO.EXE", "WINPROJ.EXE" }
                        .Any(file => File.Exists(Path.Combine(folder, "Microsoft Office", subfolder, file))))))
                products.Add("partial", new OfficeProduct("UnregisteredOffice", "Microsoft Office (instalação parcial)", OfficeTechnology.ClickToRun));
        }
        return new OfficeInventory(products.Values.OrderBy(x => x.Name).ToArray());
    }

    public static bool IsOfficeEntry(string? name, string? publisher) =>
        publisher?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true &&
        name is not null && Regex.IsMatch(name, @"^Microsoft\s+(Office|365|Visio|Project)(\s|$)", RegexOptions.IgnoreCase);

    private static string ProductName(string id) => id switch
    {
        OfficeConfiguration.ProductId => "Microsoft Office LTSC Professional Plus 2024",
        "Standard2024Volume" => "Microsoft Office LTSC Standard 2024",
        "O365ProPlusRetail" => "Microsoft 365 Apps para empresas",
        "O365BusinessRetail" => "Microsoft 365 Apps para negócios",
        _ => id
    };
}
