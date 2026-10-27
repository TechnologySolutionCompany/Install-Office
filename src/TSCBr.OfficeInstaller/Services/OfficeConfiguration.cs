using System.Xml;
using System.Xml.Linq;

namespace TSCBr.OfficeInstaller.Services;

public static class OfficeConfiguration
{
    public const string ProductId = "ProPlus2024Volume";
    public const string SourceFileName = "Configuração.xml";
    public const string RuntimeFileName = "TSCBr-Office2024.xml";
    public const string RemovalFileName = "TSCBr-RemoverOffice.xml";

    public static XDocument CreateForRemoval(bool allProducts) => new(new XElement("Configuration",
        allProducts
            ? new XElement("Remove", new XAttribute("All", "TRUE"))
            : new XElement("Remove", new XAttribute("All", "FALSE"),
                new XElement("Product", new XAttribute("ID", ProductId))),
        new XElement("Display", new XAttribute("Level", "None"), new XAttribute("AcceptEULA", "TRUE")),
        new XElement("Property", new XAttribute("Name", "FORCEAPPSHUTDOWN"), new XAttribute("Value", "FALSE"))));

    public static XDocument CreateForInstallation(Stream source)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1_000_000
        });
        var document = XDocument.Load(reader);
        var root = document.Root;
        var add = root?.Element("Add");
        if (root?.Name != "Configuration" || add is null ||
            (string?)add.Attribute("OfficeClientEdition") != "64" ||
            (string?)add.Attribute("Channel") != "PerpetualVL2024" ||
            add.Elements("Product").Count() != 1 ||
            (string?)add.Element("Product")?.Attribute("ID") != ProductId)
        {
            throw new InvalidDataException("A configuração local deve ser do Office LTSC Professional Plus 2024, 64 bits, canal PerpetualVL2024.");
        }

        // Use the supplied XML, including languages, file formats and RemoveMSI.
        // Activation is outside this release. Never execute the original AUTOACTIVATE=1.
        foreach (var property in root.Elements("Property")
                     .Where(x => (string?)x.Attribute("Name") == "AUTOACTIVATE").ToList())
            property.Remove();
        root.Add(new XElement("Property", new XAttribute("Name", "AUTOACTIVATE"), new XAttribute("Value", "0")));

        // Microsoft displays its own installation UI and applicable license terms.
        root.Elements("Display").Remove();
        root.Add(new XElement("Display", new XAttribute("Level", "Full"), new XAttribute("AcceptEULA", "FALSE")));

        var shutdown = root.Elements("Property").Where(x => (string?)x.Attribute("Name") == "FORCEAPPSHUTDOWN").ToList();
        shutdown.ForEach(x => x.Remove());
        root.Add(new XElement("Property", new XAttribute("Name", "FORCEAPPSHUTDOWN"), new XAttribute("Value", "FALSE")));
        return document;
    }
}
