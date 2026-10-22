using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RgbStudio;

/// <summary>
/// Vigila que aplicacion esta en primer plano y saca su color del icono del
/// ejecutable.
///
/// Sacarlo del icono evita tener que mantener una lista de aplicaciones y sus
/// colores: funciona con lo que sea que tengas instalado, incluso con lo que
/// instales manana.
///
/// Corre sobre el hilo de la ventana a proposito. Solo lee el proceso en primer
/// plano y, la primera vez que ve cada aplicacion, decodifica un icono; lo del
/// hardware lo hace despues quien recibe el evento, por su cuenta.
/// </summary>
internal sealed class AppWatcher
{
    /// <summary>La aplicacion que hay delante y el color que la representa.</summary>
    internal readonly record struct AppColor(string Name, Color Color);

    internal event EventHandler<AppColor>? Changed;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // Decodificar el icono cuesta lo suyo, y se vuelve a la misma aplicacion
    // constantemente. Se guarda el resultado, null incluido.
    private readonly Dictionary<string, Color?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private string _current = string.Empty;

    internal AppWatcher() => _timer.Tick += (_, _) => Poll();

    internal void Start()
    {
        _current = string.Empty;
        _timer.Start();
        Poll();
    }

    internal void Stop() => _timer.Stop();

    private void Poll()
    {
        string? path = ForegroundExecutable();
        if (path is null) return;

        // La propia ventana no cuenta. Si contara, elegir un color a mano seria
        // imposible: al pulsar aqui se sobrescribiria con el color de RGB Studio.
        if (path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
        if (path.Equals(_current, StringComparison.OrdinalIgnoreCase)) return;

        _current = path;

        if (!_cache.TryGetValue(path, out Color? color))
        {
            color = IconColor(path);
            _cache[path] = color;
        }

        // Icono sin color propio (los de consola, por ejemplo): se deja el
        // anterior. Irse a gris cada vez que pasas por una terminal es peor
        // que no cambiar nada.
        if (color is null) return;

        Changed?.Invoke(this, new AppColor(Path.GetFileNameWithoutExtension(path), color.Value));
    }

    // ------------------------------------------------------------- el proceso

    private static string? ForegroundExecutable()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;

        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return null;

        // QUERY_LIMITED_INFORMATION basta para leer la ruta y funciona tambien
        // con procesos elevados; pedir acceso completo fallaria en la mitad.
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- el color

    /// <summary>
    /// Color dominante del icono, o null si no tiene ninguno.
    ///
    /// La media de todos los pixeles no sirve: da un pardo grisaceo siempre.
    /// Aqui se agrupan por tono, descartando grises y casi negros, y gana el
    /// grupo de mas peso. Al final se sube la saturacion, porque un color
    /// apagado en un LED se ve blanco.
    /// </summary>
    private static Color? IconColor(string exePath)
    {
        if (ExtractIconEx(exePath, 0, out IntPtr large, out IntPtr small, 1) == 0) return null;

        IntPtr icon = large != IntPtr.Zero ? large : small;
        if (icon == IntPtr.Zero) return null;

        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;

            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            const int Buckets = 24;
            var weight = new double[Buckets];
            var sumSat = new double[Buckets];
            var sumVal = new double[Buckets];

            // El tono es un angulo: promediarlo como numero mezclaria el rojo
            // 350 con el rojo 10 y daria verde. Se promedian sus componentes.
            var sumX = new double[Buckets];
            var sumY = new double[Buckets];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;                    // transparente

                (double h, double s, double v) = ToHsv(pixels[i + 2], pixels[i + 1], pixels[i]);
                if (s < 0.25 || v < 0.20) continue;                   // gris, blanco o casi negro

                int bucket = (int)(h / 360 * Buckets) % Buckets;
                double w = s * v;                                     // lo vivo pesa mas
                double rad = h * Math.PI / 180;

                weight[bucket] += w;
                sumSat[bucket] += s * w;
                sumVal[bucket] += v * w;
                sumX[bucket] += Math.Cos(rad) * w;
                sumY[bucket] += Math.Sin(rad) * w;
            }

            int best = Array.IndexOf(weight, weight.Max());
            if (weight[best] <= 0) return null;

            double hue = Math.Atan2(sumY[best], sumX[best]) * 180 / Math.PI;
            if (hue < 0) hue += 360;

            return FromHsv(hue,
                           Math.Max(sumSat[best] / weight[best], 0.85),
                           Math.Max(sumVal[best] / weight[best], 0.90));
        }
        catch (Exception)
        {
            // Un ejecutable sin icono legible no debe tumbar el seguimiento.
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    private static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = 60 * (((gd - bd) / delta % 6 + 6) % 6);
            else if (max == gd) h = 60 * ((bd - rd) / delta + 2);
            else h = 60 * ((rd - gd) / delta + 4);
        }

        return (h, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb((byte)Math.Round((r + m) * 255),
                             (byte)Math.Round((g + m) * 255),
                             (byte)Math.Round((b + m) * 255));
    }

    // ---------------------------------------------------------------- Win32

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
                                                         StringBuilder name, ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index,
                                             out IntPtr large, out IntPtr small, uint count);
}
