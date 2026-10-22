using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RgbStudio.Updates;

/// <summary>Arranca OpenRGB desde su carpeta, reutilizando cualquier servidor activo.</summary>
internal sealed class OpenRgbHost
{
    internal static async Task<bool> IsListeningAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, 6742, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    internal async Task<string?> EnsureStartedAsync(OpenRgbUpdateStore store, CancellationToken cancellationToken)
    {
        if (await IsListeningAsync(cancellationToken)) return null;
        OpenRgbInstallation? managed = await Task.Run(store.Current, cancellationToken);
        string? executable = managed is not null ? store.ExecutablePath(managed) : FindLegacy();
        if (executable is null) return "Preparando OpenRGB. La descarga automática lo iniciará cuando termine.";
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--server");
            start.ArgumentList.Add("--startminimized");
            using Process? process = Process.Start(start);
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(500, cancellationToken);
                if (await IsListeningAsync(cancellationToken)) return null;
                if (process?.HasExited == true) break;
            }
            return "OpenRGB no inició su servidor. Revisa PawnIO y vuelve a pulsar Reconectar.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Se canceló el permiso de OpenRGB. Pulsa Reconectar para volver a iniciarlo.";
        }
    }

    private static string? FindLegacy()
    {
        // Las copias instaladas usan solo la instalación administrada. Este
        // respaldo conserva el flujo de desarrollo del repositorio original.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "rgbstudio-installed.marker"))) return null;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            if (!Directory.Exists(Path.Combine(directory.FullName, "App"))
                || !File.Exists(Path.Combine(directory.FullName, "Iniciar.ps1"))) continue;
            foreach (string name in new[] { "OpenRGB-Custom-Build-v2", "OpenRGB" })
            {
                string path = Path.GetFullPath(Path.Combine(directory.FullName, "..", name, "OpenRGB.exe"));
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}
