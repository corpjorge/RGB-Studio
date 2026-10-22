using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RgbStudio.Updates;

internal sealed record RgbStudioRelease(Version Version, Uri Download, long Size, string Sha256);
internal sealed record RgbStudioUpdate(RgbStudioRelease Release, string Installer);
internal sealed record RgbStudioUpdateProgress(string Message, double? Percent = null);

/// <summary>
/// Solo instala assets del repositorio configurado, con versión estable y el
/// SHA-256 publicado por GitHub. El instalador se ejecuta después de cerrar la app.
/// </summary>
internal sealed class RgbStudioUpdater : IDisposable
{
    internal const string Repository = "corpjorge/RGB-Studio";
    internal static readonly Uri LatestUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Version _currentVersion;
    internal string Root { get; }
    private string SettingsPath => Path.Combine(Root, "settings.json");

    internal RgbStudioUpdater(Version currentVersion, string? root = null, HttpClient? http = null)
    {
        _currentVersion = currentVersion;
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgbStudio", "Updates"));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    internal bool Automatic
    {
        get
        {
            try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath))?.Automatic ?? true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
        }
        set
        {
            Directory.CreateDirectory(Root);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(value)));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }

    internal async Task<RgbStudioUpdate?> CheckAndDownloadAsync(
        IProgress<RgbStudioUpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            progress.Report(new("Buscando una nueva versión de RGB Studio…"));
            using HttpResponseMessage response = await GetAsync(LatestUri, metadataTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                progress.Report(new("Todavía no hay una versión publicada."));
                return null;
            }
            await using Stream metadataStream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            await CopyAsync(metadataStream, metadata, 2 * 1024 * 1024, metadataTimeout.Token).ConfigureAwait(false);
            RgbStudioRelease release = ParseRelease(Encoding.UTF8.GetString(metadata.ToArray()));
            if (release.Version <= _currentVersion)
            {
                progress.Report(new($"RGB Studio {_currentVersion} está actualizado."));
                return null;
            }

            string directory = OpenRgbUpdateStore.Child(Root, release.Version.ToString());
            Directory.CreateDirectory(directory);
            string installer = OpenRgbUpdateStore.Child(directory, AssetName(release.Version));
            if (await Task.Run(() => VerifyInstaller(installer, release), timeout.Token).ConfigureAwait(false))
            {
                progress.Report(new($"RGB Studio {release.Version} listo para instalar.", 100));
                return new(release, installer);
            }

            string temporary = installer + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using HttpResponseMessage package = await GetAsync(release.Download, timeout.Token).ConfigureAwait(false);
                if (package.Content.Headers.ContentLength is { } length && length != release.Size)
                    throw new InvalidDataException("El tamaño del instalador no coincide con el release.");
                progress.Report(new($"Nueva versión {release.Version}. Descargando…", 0));
                await using (Stream input = await package.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    long written = await CopyAsync(input, output, release.Size, timeout.Token,
                        bytes => progress.Report(new($"Descargando RGB Studio {release.Version}…", bytes * 100d / release.Size))).ConfigureAwait(false);
                    if (written != release.Size) throw new InvalidDataException("La descarga del instalador está incompleta.");
                }
                if (!await Task.Run(() => VerifyInstaller(temporary, release), timeout.Token).ConfigureAwait(false))
                    throw new InvalidDataException("El instalador no coincide con el SHA-256 publicado por GitHub.");
                timeout.Token.ThrowIfCancellationRequested();
                File.Move(temporary, installer, overwrite: true);
                progress.Report(new($"RGB Studio {release.Version} listo. Se instalará al salir o con Actualizar y reiniciar.", 100));
                return new(release, installer);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { _gate.Release(); }
    }

    internal static string AssetName(Version version) => $"RGB-Studio-{version}-win-x64-Setup.exe";

    internal static RgbStudioRelease ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("prerelease", out JsonElement prerelease) || prerelease.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String
            || !release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("El release de RGB Studio no es válido.");
        Match stable = Regex.Match(tag.GetString() ?? "", @"^v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
        if (!stable.Success || !Version.TryParse(stable.Groups[1].Value, out Version? version))
            throw new InvalidDataException("La versión de RGB Studio no es estable.");
        string expectedName = AssetName(version);
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement name) || name.GetString() != expectedName
                || !asset.TryGetProperty("size", out JsonElement size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out long bytes) || bytes is <= 0 or > MaxInstallerBytes
                || !asset.TryGetProperty("digest", out JsonElement digest) || digest.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(digest.GetString() ?? "", @"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)
                || !asset.TryGetProperty("browser_download_url", out JsonElement url) || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? uri)
                || uri.ToString() != $"https://github.com/{Repository}/releases/download/v{version}/{expectedName}") continue;
            return new(version, uri, bytes, digest.GetString()![7..]);
        }
        throw new InvalidDataException("El release no incluye el instalador x64 con su SHA-256 de GitHub.");
    }

    internal static bool VerifyInstaller(string path, RgbStudioRelease release)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length != release.Size
                || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            stream.Position = 0;
            using var pe = new PEReader(stream);
            return pe.PEHeaders.PEHeader is not null
                && pe.PEHeaders.CoffHeader.Machine is Machine.I386 or Machine.Amd64
                && pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage)
                && !pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    internal bool StartInstallerAfterExit(RgbStudioUpdate update, string applicationDirectory)
    {
        string helper = Path.Combine(applicationDirectory, "Update-RgbStudio.ps1");
        if (!File.Exists(Path.Combine(applicationDirectory, "rgbstudio-installed.marker")) || !File.Exists(helper)) return false;
        string expected = OpenRgbUpdateStore.Child(OpenRgbUpdateStore.Child(Root, update.Release.Version.ToString()), AssetName(update.Release.Version));
        if (!Path.GetFullPath(update.Installer).Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !VerifyInstaller(update.Installer, update.Release)) throw new InvalidDataException("El instalador preparado ya no es válido.");

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", helper,
                     "-Installer", update.Installer, "-ExpectedSha256", update.Release.Sha256,
                     "-WaitProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-Destination", Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) is not null;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || !(uri.Host is "github.com" or "api.github.com"
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("El instalador redirige fuera de GitHub.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("RgbStudio/" + _currentVersion);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            try
            {
                if (!(response.StatusCode == HttpStatusCode.NotFound && uri == LatestUri)) response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Demasiadas redirecciones al descargar RGB Studio.");
    }

    private static async Task<long> CopyAsync(Stream input, Stream output, long limit, CancellationToken token, Action<long>? progress = null)
    {
        byte[] buffer = new byte[81920];
        long count = 0, lastReported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            count += read;
            if (count > limit) throw new InvalidDataException("La descarga supera el tamaño permitido.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (count - lastReported >= 256 * 1024 || count == limit) { progress?.Invoke(count); lastReported = count; }
        }
        return count;
    }

    private sealed record Preferences(bool Automatic = true);
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
