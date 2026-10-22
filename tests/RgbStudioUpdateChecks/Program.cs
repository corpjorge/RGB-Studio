using System.IO;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RgbStudio.Updates;

string root = Path.GetFullPath(Path.Combine("bin", "rgbstudio-update-checks", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
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
    catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
    { Check(true, message); return; }
    throw new InvalidOperationException("No se rechazó: " + message);
}
byte[] Executable(Machine machine)
{
    var metadata = new MetadataBuilder();
    metadata.AddModule(0, metadata.GetOrAddString("Fixture"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    var image = new ManagedPEBuilder(new PEHeaderBuilder(machine: machine, imageCharacteristics: Characteristics.ExecutableImage),
        new MetadataRootBuilder(metadata), new BlobBuilder());
    var blob = new BlobBuilder();
    image.Serialize(blob);
    return blob.ToArray();
}
byte[] package = Executable(Machine.I386); // Inno puede usar un bootstrap x86 para instalar una app x64.
string digest = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
string Metadata(string version = "1.0.1", string? hash = null, string? url = null, bool draft = false, bool prerelease = false, string? tag = null)
    => JsonSerializer.Serialize(new
    {
        tag_name = tag ?? "v" + version, draft, prerelease,
        assets = new[] { new { name = $"RGB-Studio-{version}-win-x64-Setup.exe", size = package.Length,
            digest = hash ?? "sha256:" + digest,
            browser_download_url = url ?? $"https://github.com/corpjorge/RGB-Studio/releases/download/v{version}/RGB-Studio-{version}-win-x64-Setup.exe" } },
    });

Check(RgbStudioUpdater.ParseRelease(Metadata()).Version == new Version(1, 0, 1), "Reconoce un release estable con el instalador esperado");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease(Metadata(draft: true))), "Rechaza releases en borrador");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease(Metadata(prerelease: true))), "Rechaza prereleases");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease(Metadata(tag: "v1.0.1-rc1"))), "Rechaza tags de candidatos a release");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease(Metadata(hash: ""))), "No instala assets sin SHA-256 publicado por GitHub");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease(Metadata(url: "https://github.com/other/repo/releases/download/v1.0.1/setup.exe"))),
    "Rechaza instaladores de otros repositorios");
await Reject(() => Task.Run(() => RgbStudioUpdater.ParseRelease("{}")), "Rechaza metadatos incompletos");

var source = new FakeSource { Metadata = Metadata(), Package = package };
using var http = new HttpClient(source);
using var updater = new RgbStudioUpdater(new Version(1, 0, 0), root, http);
var progress = new InlineProgress();
Check(updater.Automatic, "Las actualizaciones están activadas por defecto");
updater.Automatic = false;
using (var other = new RgbStudioUpdater(new Version(1, 0, 0), root, http))
    Check(!other.Automatic, "La preferencia se conserva entre sesiones");
updater.Automatic = true;
RgbStudioUpdate? ready = await updater.CheckAndDownloadAsync(progress, default);
Check(ready is not null && File.Exists(ready.Installer) && RgbStudioUpdater.VerifyInstaller(ready.Installer, ready.Release),
    "Descarga el instalador y valida su tamaño, hash y formato PE");
Check(source.Downloads == 1, "La actualización se descarga una sola vez");
await updater.CheckAndDownloadAsync(progress, default);
Check(source.Downloads == 1, "Reutiliza una descarga íntegra al volver a comprobar");
Check(!updater.StartInstallerAfterExit(ready!, Path.Combine(root, "development")),
    "No ejecuta el instalador automáticamente en una copia de desarrollo");

source.Metadata = Metadata("1.0.0");
Check(await updater.CheckAndDownloadAsync(progress, default) is null, "No reinstala la versión actual");
source.Metadata = Metadata("0.9.0");
Check(await updater.CheckAndDownloadAsync(progress, default) is null, "No degrada la aplicación a versiones anteriores");
source.MetadataStatus = HttpStatusCode.NotFound;
Check(await updater.CheckAndDownloadAsync(progress, default) is null, "Un repositorio sin releases no bloquea la aplicación");
source.MetadataStatus = HttpStatusCode.ServiceUnavailable;
await Reject(() => updater.CheckAndDownloadAsync(progress, default), "Un error de GitHub deja intacta la aplicación");
source.MetadataStatus = HttpStatusCode.OK;

File.AppendAllText(ready!.Installer, "alterado");
Check(!RgbStudioUpdater.VerifyInstaller(ready.Installer, ready.Release), "Detecta un instalador alterado en la caché");
source.Metadata = Metadata();
ready = await updater.CheckAndDownloadAsync(progress, default);
Check(RgbStudioUpdater.VerifyInstaller(ready!.Installer, ready.Release) && source.Downloads == 2,
    "Vuelve a descargar una copia alterada");
source.Metadata = Metadata("2.0.0", "sha256:" + new string('0', 64));
await Reject(() => updater.CheckAndDownloadAsync(progress, default), "Un SHA-256 incorrecto impide preparar la actualización");
Check(!Directory.GetFiles(root, "*.download", SearchOption.AllDirectories).Any(), "No deja descargas parciales como instaladores listos");
Check(RgbStudioUpdater.VerifyInstaller(ready.Installer, ready.Release), "Una actualización fallida conserva la descarga anterior");

source.Metadata = Metadata("2.0.0");
source.Redirect = new Uri("https://example.invalid/setup.exe");
await Reject(() => updater.CheckAndDownloadAsync(progress, default), "Bloquea redirecciones fuera de GitHub");
source.Redirect = null;
source.Package = package[..^1];
await Reject(() => updater.CheckAndDownloadAsync(progress, default), "Rechaza una descarga incompleta");
source.Package = package;
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    await Reject(() => updater.CheckAndDownloadAsync(progress, canceled.Token), "Respeta la cancelación sin ejecutar procesos");
}

if (args.Contains("--live", StringComparer.Ordinal))
{
    using var live = new RgbStudioUpdater(new Version(0, 0, 0), Path.Combine(root, "live"));
    RgbStudioUpdate? downloaded = await live.CheckAndDownloadAsync(new InlineProgress { Echo = true }, default);
    Check(downloaded is not null && RgbStudioUpdater.VerifyInstaller(downloaded.Installer, downloaded.Release),
        "Detecta y descarga el release publicado desde GitHub sin ejecutarlo");
}
Console.WriteLine($"{passed} comprobaciones correctas. No se han iniciado instaladores ni accedido al hardware.");

internal sealed class InlineProgress : IProgress<RgbStudioUpdateProgress>
{
    public bool Echo { get; init; }
    private string _last = "";
    public void Report(RgbStudioUpdateProgress value)
    {
        if (Echo && value.Message != _last) Console.WriteLine(value.Message);
        _last = value.Message;
    }
}
internal sealed class FakeSource : HttpMessageHandler
{
    internal string Metadata { get; set; } = "{}";
    internal byte[] Package { get; set; } = [];
    internal int Downloads { get; private set; }
    internal HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;
    internal Uri? Redirect { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == RgbStudioUpdater.LatestUri)
            return Task.FromResult(new HttpResponseMessage(MetadataStatus) { Content = new StringContent(Metadata, Encoding.UTF8, "application/json") });
        Downloads++;
        var response = new HttpResponseMessage(Redirect is null ? HttpStatusCode.OK : HttpStatusCode.Redirect) { Content = new ByteArrayContent(Package) };
        if (Redirect is not null) response.Headers.Location = Redirect;
        return Task.FromResult(response);
    }
}
