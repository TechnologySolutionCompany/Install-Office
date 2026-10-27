using System.Reflection;
using System.Security.Cryptography;

namespace TSCBr.OfficeInstaller.Services;

public sealed class EmbeddedPayload
{
    private const string Prefix = "TSCBr.Payload/";
    private readonly Assembly _assembly = typeof(EmbeddedPayload).Assembly;

    public IReadOnlyList<string> Files => _assembly.GetManifestResourceNames()
        .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(x => x[Prefix.Length..].Replace('\\', '/'))
        .Order(StringComparer.Ordinal).ToArray();

    public Stream Open(string relativePath)
    {
        var resource = _assembly.GetManifestResourceNames().SingleOrDefault(x =>
            x.StartsWith(Prefix, StringComparison.Ordinal) &&
            x[Prefix.Length..].Replace('\\', '/') == relativePath);
        return resource is null ? throw new FileNotFoundException("Arquivo ausente no pacote: " + relativePath)
            : _assembly.GetManifestResourceStream(resource)!;
    }

    public async Task ExtractAsync(string destination, IInstallationPlatform platform,
        IProgress<InstallProgress> progress, CancellationToken cancellationToken = default)
    {
        var files = Files;
        if (!files.Contains("setup.exe") || !files.Contains(OfficeConfiguration.SourceFileName))
            throw new InvalidDataException("O executável está sem os arquivos necessários para instalar o Office.");

        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        platform.PrepareDirectory(destination);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = files[index];
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O pacote contém um caminho fora da pasta de instalação.");

            platform.PrepareDirectory(Path.GetDirectoryName(target)!);
            platform.PrepareFile(target);
            await using (var input = Open(relative))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken);
            platform.PrepareFile(target);

            await using var expected = Open(relative);
            await using var actual = File.OpenRead(target);
            var expectedHash = await SHA256.HashDataAsync(expected, cancellationToken);
            var actualHash = await SHA256.HashDataAsync(actual, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new InvalidDataException("Não foi possível conferir a cópia de " + relative + ". Tente novamente.");
            progress.Report(new("Preparando os arquivos", "Copiando e conferindo os arquivos em C:\\MS OFFICE SETUP.",
                100.0 * (index + 1) / files.Count));
        }
    }
}
