using System.IO.Compression;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using RgbStudio.Updates;

string root = Path.GetFullPath(Path.Combine("bin", "openrgb-update-checks", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var progress = new InlineProgress();
int passed = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("OK " + message);
    passed++;
}

async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or BadImageFormatException or OperationCanceledException)
    {
        Check(true, message);
        return;
    }
    throw new InvalidOperationException("No se rechazó: " + message);
}

byte[] Executable(Machine machine = Machine.Amd64)
{
    var metadata = new MetadataBuilder();
    metadata.AddModule(0, metadata.GetOrAddString("Fixture"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    var image = new ManagedPEBuilder(new PEHeaderBuilder(machine: machine,
        imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware),
        new MetadataRootBuilder(metadata), new BlobBuilder());
    var blob = new BlobBuilder();
    image.Serialize(blob);
    return blob.ToArray();
}

byte[] Zip(params (string Name, byte[] Contents, bool Symlink)[] entries)
{
    using var result = new MemoryStream();
    using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var entry in entries)
        {
            ZipArchiveEntry file = zip.CreateEntry(entry.Name);
            if (entry.Symlink) file.ExternalAttributes = unchecked((int)0xA1FF0000);
            using Stream stream = file.Open();
            stream.Write(entry.Contents);
        }
    return result.ToArray();
}

object Release(string version, int size, bool draft = false, bool prerelease = false,
    string? tag = null, string? url = null, string? assetName = null)
{
    tag ??= "release_" + version;
    assetName ??= $"OpenRGB_{version}_Windows_64_abcdef0.zip";
    return new
    {
        tag_name = tag, draft, prerelease,
        assets = new[] { new { name = assetName, size, browser_download_url = url ??
            $"https://codeberg.org/OpenRGB/OpenRGB/releases/download/{tag}/{assetName}" } },
    };
}

var pe = Executable();
var package = Zip(("OpenRGB Windows 64-bit/OpenRGB.exe", pe, false),
    ("OpenRGB Windows 64-bit/platforms/qwindows.dll", new byte[] { 1, 2, 3 }, false));
var source = new FakeSource { Package = package };
source.Releases = JsonSerializer.Serialize(new[]
{
    Release("0.9", package.Length), Release("1.1", package.Length),
    Release("9.0", package.Length, tag: "release_candidate_9.0rc1"),
    Release("8.0", package.Length, draft: true), Release("7.0", package.Length, prerelease: true),
    Release("6.0", package.Length, assetName: "OpenRGB_6.0_Windows_32_abcdef0.zip"),
    Release("5.0", package.Length, url: "https://example.invalid/OpenRGB_5.0_Windows_64_abcdef0.zip"),
});
Check(OpenRgbUpdater.ParseLatestRelease(source.Releases).Version == new Version(1, 1),
    "Selecciona la estable x64 más reciente y omite RC, borradores, x86 y alojamientos ajenos");
await Reject(() => Task.Run(() => OpenRgbUpdater.ParseLatestRelease("[]")), "Una lista vacía no inventa una actualización");
await Reject(() => Task.Run(() => OpenRgbUpdater.ParseLatestRelease("{}")), "Rechaza metadatos que no sean una lista");
await Reject(() => Task.Run(() => OpenRgbUpdater.ParseLatestRelease("[{\"draft\":false,\"prerelease\":false}]")),
    "Los campos ausentes no bloquean la aplicación");

using var http = new HttpClient(source);
var store = new OpenRgbUpdateStore(Path.Combine(root, "managed"));
using var updater = new OpenRgbUpdater(store, http);
OpenRgbUpdateResult first = await updater.CheckAsync(true, progress, default);
Check(first.Installed && first.Installation?.Version == "1.1" && store.IsUsable(first.Installation),
    "Instala un paquete completo y verifica el hash del ejecutable");
Check(Directory.GetFiles(store.Root, "download-*.zip").Length == 0,
    "Elimina la descarga temporal después de instalar");
int requests = source.Requests;
await updater.CheckAsync(false, progress, default);
Check(source.Requests == requests, "Respeta las seis horas entre comprobaciones automáticas");
await updater.CheckAsync(true, progress, default);
Check(!progress.Message.Contains("Descargando") && source.PackageRequests == 1,
    "No descarga de nuevo una versión ya instalada");
store.SetAutomatic(false);
requests = source.Requests;
await updater.CheckAsync(false, progress, default);
Check(source.Requests == requests, "Desactivar las actualizaciones evita solicitudes automáticas");
await updater.CheckAsync(true, progress, default);
Check(source.Requests > requests, "Buscar ahora funciona con el modo automático desactivado");
store.SetAutomatic(true);

string firstExecutable = store.ExecutablePath(first.Installation!);
string firstManifest = File.ReadAllText(store.ManifestPath);
source.Releases = JsonSerializer.Serialize(new[] { Release("2.0", package.Length + 1) });
await Reject(() => updater.CheckAsync(true, progress, default), "Un tamaño de descarga incorrecto no activa la versión");
Check(File.ReadAllText(store.ManifestPath) == firstManifest && store.IsUsable(first.Installation),
    "Un fallo de descarga conserva la instalación y el manifiesto anterior");

source.Package = package[..^2];
source.PackageLengthOverride = package.Length;
source.Releases = JsonSerializer.Serialize(new[] { Release("2.0", package.Length) });
await Reject(() => updater.CheckAsync(true, progress, default), "Una respuesta truncada no activa una instalación incompleta");
source.PackageLengthOverride = null;
source.Package = package;
Check(File.ReadAllText(store.ManifestPath) == firstManifest, "La descarga truncada conserva el manifiesto anterior");

source.MetadataStatus = HttpStatusCode.ServiceUnavailable;
await Reject(() => updater.CheckAsync(true, progress, default), "Un error del servidor de versiones conserva la instalación");
source.MetadataStatus = HttpStatusCode.OK;

using (var cancelDuringDownload = new CancellationTokenSource())
{
    source.PackageOpened = () => cancelDuringDownload.Cancel();
    await Reject(() => updater.CheckAsync(true, progress, cancelDuringDownload.Token), "Cancelar durante la descarga no publica la versión");
    source.PackageOpened = null;
}
Check(File.ReadAllText(store.ManifestPath) == firstManifest, "Cancelar una descarga iniciada conserva el manifiesto anterior");

foreach (string badPath in new[] { "../escape.txt", "C:/escape.txt", "folder/file:stream", "folder./escape.txt", "NUL.txt", "folder/CON", "bad?.txt" })
{
    string archive = Path.Combine(root, Guid.NewGuid() + ".zip");
    File.WriteAllBytes(archive, Zip((badPath, new byte[] { 1 }, false)));
    await Reject(() => Task.Run(() => OpenRgbUpdater.ExtractPackage(archive,
        Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N")), default)), "Rechaza una ruta de ZIP no válida: " + badPath);
}
foreach (var entries in new[]
{
    new[] { ("link", new byte[] { 1 }, true) },
    new[] { ("OpenRGB.exe", pe, false), ("openrgb.exe", pe, false) },
})
{
    string archive = Path.Combine(root, Guid.NewGuid() + ".zip");
    File.WriteAllBytes(archive, Zip(entries));
    await Reject(() => Task.Run(() => OpenRgbUpdater.ExtractPackage(archive,
        Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N")), default)), "Rechaza enlaces simbólicos y rutas duplicadas");
}

var invalidPe = Zip(("OpenRGB.exe", Executable(Machine.I386), false));
source.Package = invalidPe;
source.Releases = JsonSerializer.Serialize(new[] { Release("2.0", invalidPe.Length) });
await Reject(() => updater.CheckAsync(true, progress, default), "Rechaza un ejecutable de 32 bits");
Check(File.ReadAllText(store.ManifestPath) == firstManifest, "Un fallo al validar el ejecutable conserva el manifiesto");

source.Package = package;
source.Releases = JsonSerializer.Serialize(new[] { Release("2.0", package.Length) });
source.Redirect = new Uri("https://example.invalid/payload.zip");
await Reject(() => updater.CheckAsync(true, progress, default), "Bloquea redirecciones fuera del alojamiento oficial");
source.Redirect = null;

using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    await Reject(() => updater.CheckAsync(true, progress, canceled.Token), "Respeta la cancelación antes de modificar archivos");
}
Check(File.ReadAllText(store.ManifestPath) == firstManifest, "Cancelar conserva la versión anterior");

OpenRgbUpdateResult second = await updater.CheckAsync(true, progress, default);
Check(second.Installed && second.Installation?.Version == "2.0"
    && store.ReadInstallations()?.Previous?.Version == "1.1" && File.Exists(firstExecutable),
    "Actualiza sin sobrescribir la versión anterior y guarda su referencia");
File.AppendAllText(store.ExecutablePath(second.Installation!), "alterado");
Check(store.Current()?.Version == "1.1", "Usa el respaldo si el ejecutable actual no coincide con su hash");

File.WriteAllText(store.ManifestPath, "{\"Current\":null}");
Check(store.Current() is null, "Un manifiesto sin instalación se ignora sin bloquear el arranque");
await Reject(() => Task.Run(() => store.ExecutablePath(first.Installation! with { Directory = "../outside" })),
    "Un manifiesto no puede seleccionar ejecutables fuera de la raíz administrada");

using (FileStream busy = new(Path.Combine(store.Root, "update.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    requests = source.Requests;
    await updater.CheckAsync(true, progress, default);
    Check(source.Requests == requests, "El bloqueo entre procesos evita dos instalaciones simultáneas");
}

if (args.Contains("--live", StringComparer.Ordinal))
{
    var liveStore = new OpenRgbUpdateStore(Path.Combine(root, "official-live"));
    using var liveUpdater = new OpenRgbUpdater(liveStore);
    OpenRgbUpdateResult live = await liveUpdater.CheckAsync(true, new InlineProgress { Echo = true }, default);
    Check(live.Installed && liveStore.IsUsable(live.Installation), "Descarga y valida el ZIP oficial sin ejecutar OpenRGB");
    Console.WriteLine("Instalación de prueba: " + liveStore.ExecutablePath(live.Installation!));
}

Console.WriteLine($"{passed} comprobaciones correctas. Archivos de prueba: {root}");

internal sealed class InlineProgress : IProgress<OpenRgbUpdateProgress>
{
    public bool Echo { get; init; }
    internal string Message { get; private set; } = string.Empty;
    public void Report(OpenRgbUpdateProgress value)
    {
        if (Echo && value.Message != Message) Console.WriteLine(value.Message);
        Message = value.Message;
    }
}

internal sealed class FakeSource : HttpMessageHandler
{
    internal byte[] Package { get; set; } = [];
    internal string Releases { get; set; } = "[]";
    internal Uri? Redirect { get; set; }
    internal long? PackageLengthOverride { get; set; }
    internal Action? PackageOpened { get; set; }
    internal HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;
    internal int Requests { get; private set; }
    internal int PackageRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.RequestUri == OpenRgbUpdater.ReleasesUri)
            return Task.FromResult(new HttpResponseMessage(MetadataStatus) { Content = new StringContent(Releases, Encoding.UTF8, "application/json") });
        PackageRequests++;
        var response = new HttpResponseMessage(Redirect is null ? HttpStatusCode.OK : HttpStatusCode.Redirect)
        {
            Content = new ByteArrayContent(Package),
        };
        if (PackageLengthOverride is { } length) response.Content.Headers.ContentLength = length;
        if (Redirect is not null) response.Headers.Location = Redirect;
        PackageOpened?.Invoke();
        return Task.FromResult(response);
    }
}
