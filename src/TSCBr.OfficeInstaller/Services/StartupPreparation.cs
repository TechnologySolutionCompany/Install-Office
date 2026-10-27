namespace TSCBr.OfficeInstaller.Services;

public static class StartupPreparation
{
    public static async Task<OfficeInventory> PrepareAsync(IOfficeInstaller installer, IProgress<string> progress)
    {
        progress.Report("Carregando as ferramentas locais…");
        await Task.Run(() =>
        {
            var payload = new EmbeddedPayload();
            if (!payload.Files.Contains("setup.exe") || !payload.Files.Contains(OfficeConfiguration.SourceFileName))
                throw new InvalidDataException("Os arquivos de instalação não estão completos neste executável.");
            using var configuration = payload.Open(OfficeConfiguration.SourceFileName);
            OfficeConfiguration.CreateForInstallation(configuration);
        });
        progress.Report("Verificando o Office neste computador…");
        var inventory = await installer.DetectOfficeAsync();
        progress.Report("Ferramentas prontas.");
        return inventory;
    }
}
