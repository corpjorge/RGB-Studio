using System.IO.Compression;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RgbStudio.Updates;

internal sealed record OpenRgbRelease(Version Version, string Tag, Uri Download, long Size);
internal sealed record OpenRgbUpdateProgress(string Message, double? Percent = null);
internal sealed record OpenRgbUpdateResult(OpenRgbInstallation? Installation, bool Installed);

/// <summary>
/// Consulta las publicaciones oficiales y prepara una instalación completa antes
/// de cambiar el manifiesto. Un fallo nunca invalida la última versión utilizable.
/// </summary>
internal sealed class OpenRgbUpdater : IDisposable
{
    internal static readonly Uri ReleasesUri = new(
        "https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases?limit=50");
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private readonly OpenRgbUpdateStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal OpenRgbUpdater(OpenRgbUpdateStore store, HttpClient? http = null)
    {
        _store = store;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    internal async Task<OpenRgbUpdateResult> CheckAsync(bool force,
        IProgress<OpenRgbUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(null, false);
        try
        {
            OpenRgbUpdatePreferences preferences = _store.Preferences;
            if (!force && !preferences.Automatic)
            {
                progress.Report(new("Actualizaciones automáticas desactivadas."));
                return new(null, false);
            }

            // También se excluyen otros procesos; el lanzador y otra instancia
            // nunca deben preparar o activar la misma versión a la vez.
            Directory.CreateDirectory(_store.Root);
            using FileStream? updateLock = TryLock();
            if (updateLock is null)
            {
                progress.Report(new("Otra instancia está actualizando OpenRGB."));
                return new(null, false);
            }

            OpenRgbInstallation? current = _store.Current();
            if (!force && preferences.LastCheckUtc is { } last
                && DateTimeOffset.UtcNow - last >= TimeSpan.Zero
                && DateTimeOffset.UtcNow - last < CheckInterval)
            {
                ReportInstalled(current, progress);
                return new(current, false);
            }

            progress.Report(new("Buscando una versión estable de OpenRGB…"));
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await GetAsync(ReleasesUri, metadataTimeout.Token).ConfigureAwait(false);
            await using Stream metadataStream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            await CopyBoundedAsync(metadataStream, metadata, 2 * 1024 * 1024, metadataTimeout.Token).ConfigureAwait(false);
            string json = System.Text.Encoding.UTF8.GetString(metadata.ToArray());
            OpenRgbRelease latest = ParseLatestRelease(json);
            _store.RecordCheck(DateTimeOffset.UtcNow);

            if (current is not null && Version.Parse(current.Version) >= latest.Version)
            {
                progress.Report(new($"OpenRGB {current.Version} preparado. No hay una versión estable más reciente."));
                return new(current, false);
            }

            OpenRgbInstallation installed = await InstallAsync(latest, current, progress, cancellationToken).ConfigureAwait(false);
            progress.Report(new($"OpenRGB {installed.Version} instalado. Se usará al próximo inicio del servidor.", 100));
            return new(installed, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static OpenRgbRelease ParseLatestRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("La lista de versiones de OpenRGB no es válida.");
        var releases = new List<OpenRgbRelease>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind != JsonValueKind.False
                || !item.TryGetProperty("prerelease", out JsonElement prerelease) || prerelease.ValueKind != JsonValueKind.False
                || !item.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string tag = tagElement.GetString() ?? string.Empty;
            Match stable = Regex.Match(tag, @"^release_(?<version>\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);
            // Algunos RC oficiales no llevan prerelease=true; se comprueba
            // también el tag para no instalar RC, hotfix de RC ni pipelines.
            if (!stable.Success || !Version.TryParse(stable.Groups["version"].Value, out Version? version)) continue;

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out JsonElement assetName) || assetName.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("browser_download_url", out JsonElement url) || url.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("size", out JsonElement assetSize) || assetSize.ValueKind != JsonValueKind.Number
                    || !assetSize.TryGetInt64(out long size)) continue;
                string name = assetName.GetString() ?? string.Empty;
                if (!Regex.IsMatch(name, @"^OpenRGB_" + Regex.Escape(version.ToString()) +
                    @"_Windows_64_[a-fA-F0-9]+\.zip$", RegexOptions.CultureInvariant)) continue;
                if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? download)
                    || download.Scheme != Uri.UriSchemeHttps
                    || !download.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase)
                    || download.AbsolutePath != $"/OpenRGB/OpenRGB/releases/download/{tag}/{name}"
                    || !string.IsNullOrEmpty(download.Query) || !string.IsNullOrEmpty(download.Fragment))
                    continue;

                if (size is > 0 and <= MaxPackageBytes) releases.Add(new(version, tag, download, size));
            }
        }

        return releases.OrderByDescending(release => release.Version).FirstOrDefault()
            ?? throw new InvalidDataException("No se encontró un paquete oficial estable de Windows x64.");
    }

    private FileStream? TryLock()
    {
        try
        {
            return new FileStream(Path.Combine(_store.Root, "update.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }

    private static void ReportInstalled(OpenRgbInstallation? current, IProgress<OpenRgbUpdateProgress> progress)
        => progress.Report(new(current is null
            ? "Comprobación reciente. Usa Buscar ahora para volver a intentarlo."
            : $"OpenRGB {current.Version} preparado para el próximo inicio. Comprobación cada 6 horas."));

    private async Task<OpenRgbInstallation> InstallAsync(OpenRgbRelease release, OpenRgbInstallation? current,
        IProgress<OpenRgbUpdateProgress> progress, CancellationToken cancellationToken)
    {
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
        cancellationToken = downloadTimeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string versions = Path.Combine(_store.Root, "versions");
        string directoryName = release.Version + "-" + id;
        string staging = OpenRgbUpdateStore.Child(versions, directoryName);
        string downloadPath = OpenRgbUpdateStore.Child(_store.Root, "download-" + id + ".zip");
        bool activated = false;
        try
        {
            using HttpResponseMessage package = await GetAsync(release.Download, cancellationToken).ConfigureAwait(false);
            if (package.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("El tamaño del paquete no coincide con la publicación oficial.");

            progress.Report(new($"Descargando OpenRGB {release.Version}…", 0));
            await using (var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            await using (Stream input = await package.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                long written = await CopyBoundedAsync(input, output, release.Size, cancellationToken,
                    bytes => progress.Report(new($"Descargando OpenRGB {release.Version}…", bytes * 100d / release.Size))).ConfigureAwait(false);
                if (written != release.Size) throw new InvalidDataException("La descarga de OpenRGB está incompleta.");
            }

            progress.Report(new($"Preparando OpenRGB {release.Version}…"));
            // La descompresión y el cálculo de hashes no bloquean la ventana.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(staging);
                ExtractPackage(downloadPath, staging, cancellationToken);
                string[] executables = Directory.GetFiles(staging, "OpenRGB.exe", SearchOption.AllDirectories);
                if (executables.Length != 1) throw new InvalidDataException("El paquete debe contener un único OpenRGB.exe.");
                ValidateExecutable(executables[0]);
                string relativeExecutable = Path.GetRelativePath(staging, executables[0]);
                string executableHash = Hash(executables[0]);
                string packageHash = Hash(downloadPath);
                cancellationToken.ThrowIfCancellationRequested();
                // La carpeta única no se ofrece al lanzador hasta publicar el
                // manifiesto. No se renombra: Windows puede tener un antivirus
                // leyendo los archivos recién extraídos y bloquear ese cambio.
                var installation = new OpenRgbInstallation(release.Version.ToString(), directoryName,
                    relativeExecutable, executableHash, packageHash, release.Download.ToString(), DateTimeOffset.UtcNow);
                _store.Activate(installation, current);
                activated = true;
                return installation;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(downloadPath)) File.Delete(downloadPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // Solo se elimina el staging generado en esta operación, después
            // de verificar otra vez su ubicación bajo la raíz administrada.
            if (!activated && Directory.Exists(staging)
                && staging == OpenRgbUpdateStore.Child(versions, directoryName))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal static void ExtractPackage(string zipPath, string destination, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is 0 or > 10000) throw new InvalidDataException("Contenido de ZIP no válido.");
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string target = OpenRgbUpdateStore.Child(destination, relative.TrimEnd('\\', '/'));
            if (!paths.Add(target) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("El ZIP contiene rutas duplicadas o enlaces simbólicos.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("El ZIP supera el tamaño permitido.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Entrada de ZIP demasiado grande.");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Entrada de ZIP incompleta.");
        }
    }

    internal static void ValidateExecutable(string path)
    {
        using FileStream executable = File.OpenRead(path);
        using var reader = new PEReader(executable);
        if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.CoffHeader.Machine != Machine.Amd64
            || (reader.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
            || (reader.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
            throw new InvalidDataException("OpenRGB.exe no es un ejecutable Windows x64 válido.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".codeberg.org", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("La descarga redirige fuera del alojamiento oficial de OpenRGB.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("RgbStudio/1.0");
            HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Demasiadas redirecciones al descargar OpenRGB.");
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit,
        CancellationToken cancellationToken, Action<long>? report = null)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        long lastReported = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("La descarga supera el tamaño permitido.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (total - lastReported >= 256 * 1024 || total == limit)
            {
                report?.Invoke(total);
                lastReported = total;
            }
        }
        return total;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
