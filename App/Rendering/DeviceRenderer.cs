using OpenRGB.NET;
using RgbStudio.Rgb;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;   // desambigua frente a OpenRGB.NET.Color

namespace RgbStudio.Rendering;

/// <summary>Que ha pulsado el usuario: un LED suelto, una zona entera, o nada.</summary>
internal readonly record struct Pick(int ZoneIndex, int LedIndex);

/// <summary>
/// Dibuja cada dispositivo segun lo que el propio OpenRGB sabe de el.
///
/// El teclado no hay que mapearlo a mano: su zona es de tipo Matrix y trae la
/// rejilla de filas y columnas, ademas del nombre de cada tecla. La RAM es una
/// tira lineal de 5 LEDs. Solo la placa necesita catalogo propio, porque sus
/// zonas son "Single" con nombres genericos y sin ninguna pista de posicion.
/// </summary>
internal static class DeviceRenderer
{
    private const double CanvasWidth = 1000;
    private const double CanvasHeight = 700;

    private static readonly Brush Face = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
    private static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD4));
    private static readonly BitmapSource KeyboardOriginal = LoadProductOriginal(
        "HyperXAlloyEliteRGB.png", new Rect(0.0625, 0.3125, 0.875, 0.35), 1400);
    private static readonly ImageSource KeyboardPhoto = CreateProductPhoto(
        KeyboardOriginal, transparentWhite: true, darkenColor: false);
    private static readonly IReadOnlyDictionary<string, Rect> KeyboardLayout = CreateKeyboardLayout();
    private static readonly BitmapSource D80Original = LoadProductOriginal(
        "XpgSpectrixD80.png", new Rect(0.025, 0.208, 0.95, 0.556), 1500);
    private static readonly ImageSource D80Photo = CreateProductPhoto(
        D80Original, transparentWhite: false, darkenColor: true, neutralizeD80: true);
    private static readonly BitmapSource D41Original = LoadProductOriginal(
        "XpgSpectrixD41.png", new Rect(0.025, 0.194, 0.95, 0.50), 1400);
    private static readonly ImageSource D41Photo = CreateProductPhoto(
        D41Original, transparentWhite: true, darkenColor: true, neutralizeD41: true);
    private static readonly BitmapSource MotherboardOriginal = LoadMotherboardOriginal();
    private static readonly ImageSource MotherboardPhoto = LoadMotherboardPhoto();
    private static readonly Dictionary<int, ImageSource> MotherboardMasks = [];

    /// <summary>
    /// Dibuja el dispositivo y devuelve los elementos pulsables.
    ///
    /// <paramref name="onResize"/> recibe (zona, cuantos LED) cuando se declara
    /// la longitud de una tira desde el propio mapa.
    /// </summary>
    internal static Dictionary<Pick, Border> Draw(Canvas canvas, Device device, string displayName,
                                                  MouseButtonEventHandler onClick,
                                                  Action<int, int>? onResize = null)
    {
        canvas.Children.Clear();
        var shapes = new Dictionary<Pick, Border>();

        switch (device.Type)
        {
            case DeviceType.Keyboard:
                DrawKeyboard(canvas, device, shapes, onClick);
                break;
            case DeviceType.Dram:
                DrawDram(canvas, device, displayName, shapes, onClick);
                break;
            case DeviceType.Motherboard when ZoneCatalog.For(device.Name).Count > 0:
                DrawMotherboard(canvas, device, shapes, onClick, onResize);
                break;
            default:
                DrawZoneGrid(canvas, device, shapes, onClick);
                break;
        }

        return shapes;
    }

    // -------------------------------------------------------------- teclado

    private static void DrawKeyboard(Canvas canvas, Device device,
                                     Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick)
    {
        var offsets = RgbService.ZoneOffsets(device);

        const double imageLeft = 35;
        const double imageTop = 135;
        const double imageWidth = 930;
        const double imageHeight = 372;
        var keyboardPhoto = new Image
        {
            Source = KeyboardPhoto,
            Width = imageWidth,
            Height = imageHeight,
            Stretch = Stretch.Fill,
        };
        Canvas.SetLeft(keyboardPhoto, imageLeft);
        Canvas.SetTop(keyboardPhoto, imageTop);
        canvas.Children.Add(keyboardPhoto);

        // Teclas multimedia arriba, como en el teclado real.
        Zone? media = device.Zones.FirstOrDefault(z => z.Name.Contains("Media", StringComparison.OrdinalIgnoreCase));
        if (media is not null && media.LedCount > 0)
        {
            DrawPhotoStrip(canvas, device, media, offsets[media.Index], shapes, onClick,
                           x: 700, y: 153, width: 166, height: 28);
        }

        Zone? keys = device.Zones.FirstOrDefault(z => z.MatrixMap is not null);
        if (keys is not null)
        {
            int start = offsets[keys.Index];
            const double keyOffsetX = -5;
            const double keyOffsetY = -4;
            // La posicion del mapa corresponde al marco exterior de cada tecla.
            // Dejamos visible ese marco y centramos el color sobre la tecla real;
            // esto evita que las filas F y las flechas parezcan solapadas.
            const double keyInsetX = 5;
            const double keyInsetY = 3;
            for (int i = 0; i < keys.LedCount; i++)
            {
                int led = start + i;
                if (led >= device.Leds.Length) break;

                string name = device.Leds[led].Name.Replace("Key: ", string.Empty);
                if (!KeyboardLayout.TryGetValue(name, out Rect position)) continue;

                Border key = MakePhotoCell(
                    position.Width * imageWidth - keyInsetX * 2,
                    position.Height * imageHeight - keyInsetY * 2,
                    4);
                key.ToolTip = name;
                Attach(key, new Pick(keys.Index, led), shapes, onClick);

                Canvas.SetLeft(key, imageLeft + position.X * imageWidth + keyOffsetX + keyInsetX);
                Canvas.SetTop(key, imageTop + position.Y * imageHeight + keyOffsetY + keyInsetY);
                canvas.Children.Add(key);
            }
        }

        // La barra RGB superior tiene 18 segmentos controlables.
        Zone? strip = device.Zones.FirstOrDefault(z => z.Name.Contains("Strip", StringComparison.OrdinalIgnoreCase));
        if (strip is not null && strip.LedCount > 0)
            DrawPhotoStrip(canvas, device, strip, offsets[strip.Index], shapes, onClick,
                           x: 70, y: 199, width: 860, height: 13);

        canvas.Children.Add(Caption(
            "HyperX Alloy Elite RGB — pulsa una tecla o un segmento de luz para seleccionarlo",
            62, 535));
    }

    /// <summary>
    /// Posiciones normalizadas sobre la fotografia concreta del Alloy Elite.
    /// La matriz de OpenRGB tiene columnas uniformes, pero el teclado real no:
    /// Tab, Shift, espacio, Enter y los huecos entre bloques tienen otros anchos.
    /// </summary>
    private static IReadOnlyDictionary<string, Rect> CreateKeyboardLayout()
    {
        var result = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        const double keyHeight = 0.093;
        const double gap = 0.004;

        void Add(string name, double x, double y, double width, double height = keyHeight)
            => result[name] = new Rect(x, y, width, height);

        void AddUnitRow(double x, double y, double width,
                        (string Name, double Units)[] keys)
        {
            double total = keys.Sum(key => key.Units);
            double unit = width / total;
            double cursor = x;
            foreach ((string name, double units) in keys)
            {
                double keyWidth = unit * units;
                Add(name, cursor + gap / 2, y, keyWidth - gap);
                cursor += keyWidth;
            }
        }

        Add("Escape", 0.042, 0.222, 0.038);
        string[][] functionGroups =
        [
            ["F1", "F2", "F3", "F4"],
            ["F5", "F6", "F7", "F8"],
            ["F9", "F10", "F11", "F12"],
        ];
        // Centros medidos sobre la fotografia: los huecos entre grupos F no
        // son iguales al paso uniforme que informa la matriz de OpenRGB.
        double[] groupStarts = [0.1235, 0.3092, 0.4956];
        const double functionStep = 0.0414;
        for (int group = 0; group < functionGroups.Length; group++)
            for (int i = 0; i < functionGroups[group].Length; i++)
                Add(functionGroups[group][i], groupStarts[group] + i * functionStep, 0.222, 0.041);

        const double systemX = 0.6725;
        const double separatedStep = 0.0414;
        Add("Print Screen", systemX, 0.222, 0.040);
        Add("Scroll Lock", systemX + separatedStep, 0.222, 0.040);
        Add("Pause/Break", systemX + separatedStep * 2, 0.222, 0.040);

        // La fotografia deja menos margen a la izquierda que la matriz
        // uniforme de OpenRGB. El ancho conserva el borde derecho real.
        const double mainX = 0.041;
        const double mainWidth = 0.611;
        AddUnitRow(mainX, 0.352, mainWidth,
        [
            ("`", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1),
            ("6", 1), ("7", 1), ("8", 1), ("9", 1), ("0", 1), ("-", 1),
            ("=", 1), ("Backspace", 2),
        ]);
        AddUnitRow(mainX, 0.457, mainWidth,
        [
            ("Tab", 1.5), ("Q", 1), ("W", 1), ("E", 1), ("R", 1), ("T", 1),
            ("Y", 1), ("U", 1), ("I", 1), ("O", 1), ("P", 1), ("[", 1),
            ("]", 1), ("\\ (ANSI)", 1.5),
        ]);
        AddUnitRow(mainX, 0.562, mainWidth,
        [
            ("Caps Lock", 1.75), ("A", 1), ("S", 1), ("D", 1), ("F", 1),
            ("G", 1), ("H", 1), ("J", 1), ("K", 1), ("L", 1), (";", 1),
            ("'", 1), ("Enter", 2.25),
        ]);
        AddUnitRow(mainX, 0.667, mainWidth,
        [
            ("Left Shift", 2.25), ("Z", 1), ("X", 1), ("C", 1), ("V", 1),
            ("B", 1), ("N", 1), ("M", 1), (",", 1), (".", 1), ("/", 1),
            ("Right Shift", 2.75),
        ]);
        AddUnitRow(mainX, 0.772, mainWidth,
        [
            ("Left Control", 1.25), ("Left Windows", 1.25), ("Left Alt", 1.25),
            ("Space", 6.25), ("Right Alt", 1.25), ("Right Windows", 1.25),
            ("Menu", 1.25), ("Right Control", 1.25),
        ]);

        const double navX = 0.672;
        const double navCell = 0.040;
        string[] navTop = ["Insert", "Home", "Page Up"];
        string[] navMiddle = ["Delete", "End", "Page Down"];
        for (int i = 0; i < 3; i++)
        {
            Add(navTop[i], navX + i * separatedStep, 0.352, navCell);
            Add(navMiddle[i], navX + i * separatedStep, 0.457, navCell);
        }
        Add("Up Arrow", navX + separatedStep, 0.667, navCell);
        Add("Left Arrow", navX, 0.772, navCell);
        Add("Down Arrow", navX + separatedStep, 0.772, navCell);
        Add("Right Arrow", navX + separatedStep * 2, 0.772, navCell);

        const double padX = 0.8075;
        const double padCell = 0.040;
        string[][] padRows =
        [
            ["Num Lock", "Number Pad /", "Number Pad *", "Number Pad -"],
            ["Number Pad 7", "Number Pad 8", "Number Pad 9"],
            ["Number Pad 4", "Number Pad 5", "Number Pad 6"],
            ["Number Pad 1", "Number Pad 2", "Number Pad 3"],
        ];
        double[] padY = [0.352, 0.457, 0.562, 0.667];
        for (int row = 0; row < padRows.Length; row++)
            for (int col = 0; col < padRows[row].Length; col++)
                Add(padRows[row][col], padX + col * separatedStep, padY[row], padCell);

        Add("Number Pad +", padX + separatedStep * 3, 0.457, padCell, keyHeight * 2 + 0.012);
        Add("Number Pad Enter", padX + separatedStep * 3, 0.667, padCell, keyHeight * 2 + 0.012);
        Add("Number Pad 0", padX, 0.772, padCell * 2 + 0.004);
        Add("Number Pad .", padX + separatedStep * 2, 0.772, padCell);

        return result;
    }

    private static void DrawPhotoStrip(Canvas canvas, Device device, Zone zone, int start,
                                       Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick,
                                       double x, double y, double width, double height)
    {
        double cell = width / zone.LedCount;
        for (int i = 0; i < zone.LedCount; i++)
        {
            int led = start + i;
            if (led >= device.Leds.Length) break;

            Border segment = MakePhotoCell(cell - 1, height, Math.Min(4, height / 2));
            segment.ToolTip = device.Leds[led].Name;
            Attach(segment, new Pick(zone.Index, led), shapes, onClick);
            Canvas.SetLeft(segment, x + i * cell);
            Canvas.SetTop(segment, y);
            canvas.Children.Add(segment);
        }
    }

    private static Border MakePhotoCell(double width, double height, double radius) => new()
    {
        Width = Math.Max(4, width),
        Height = Math.Max(4, height),
        CornerRadius = new CornerRadius(radius),
        Background = Brushes.Transparent,
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
    };

    /// <summary>Carga y recorta una fotografia sin conservar los grandes margenes del archivo original.</summary>
    private static BitmapSource LoadProductOriginal(string fileName, Rect crop, int decodeWidth)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.DecodePixelWidth = decodeWidth;
        source.UriSource = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        int x = Math.Clamp((int)(crop.X * source.PixelWidth), 0, source.PixelWidth - 1);
        int y = Math.Clamp((int)(crop.Y * source.PixelHeight), 0, source.PixelHeight - 1);
        int width = Math.Clamp((int)(crop.Width * source.PixelWidth), 1, source.PixelWidth - x);
        int height = Math.Clamp((int)(crop.Height * source.PixelHeight), 1, source.PixelHeight - y);
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// Genera una base neutra. En fotografias de fondo blanco solo se elimina
    /// el blanco conectado al borde, por lo que se conservan letras y reflejos.
    /// </summary>
    private static ImageSource CreateProductPhoto(BitmapSource original, bool transparentWhite,
                                                  bool darkenColor, bool neutralizeD41 = false,
                                                  bool neutralizeD80 = false)
    {
        int width = original.PixelWidth;
        int height = original.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        original.CopyPixels(pixels, stride, 0);
        bool[] background = transparentWhite ? FindWhiteBackground(pixels, width, height) : [];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (background.Length > 0 && background[i / 4])
            {
                pixels[i + 3] = 0;
                continue;
            }

            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            int gray = (54 * r + 183 * g + 19 * b) >> 8;
            if (darkenColor && IsColorful(r, g, b)) gray = (int)(gray * 0.24);
            if (neutralizeD41)
            {
                int pixel = i / 4;
                double normalizedX = pixel % width / (double)width;
                double normalizedY = pixel / width / (double)height;
                if (IsD41Diffuser(normalizedX, normalizedY)) gray = (int)(gray * 0.08);
            }
            if (neutralizeD80)
            {
                int pixel = i / 4;
                double normalizedX = pixel % width / (double)width;
                double normalizedY = pixel / width / (double)height;
                if (IsD80Diffuser(normalizedX, normalizedY)) gray = (int)(gray * 0.08);
            }
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)gray;
        }

        BitmapSource result = BitmapSource.Create(
            width, height, original.DpiX, original.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Mascara de un tramo horizontal de la iluminacion original de una RAM.</summary>
    private static ImageSource CreateProductLightMask(BitmapSource original, double from, double to,
                                                      bool d41)
    {
        int x = Math.Clamp((int)(from * original.PixelWidth), 0, original.PixelWidth - 1);
        int right = Math.Clamp((int)(to * original.PixelWidth), x + 1, original.PixelWidth);
        int width = right - x;
        int height = original.PixelHeight;
        int stride = width * 4;
        var mask = new byte[stride * height];

        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                double normalizedX = (x + px) / (double)original.PixelWidth;
                double normalizedY = py / (double)height;
                bool insideDiffuser = d41
                    ? IsD41Diffuser(normalizedX, normalizedY)
                    : IsD80Diffuser(normalizedX, normalizedY);
                if (!insideDiffuser) continue;

                int i = py * stride + px * 4;
                mask[i] = mask[i + 1] = mask[i + 2] = 255;
                mask[i + 3] = 235;
            }
        }

        BitmapSource result = BitmapSource.Create(
            width, height, original.DpiX, original.DpiY,
            PixelFormats.Bgra32, null, mask, stride);
        result.Freeze();
        return result;
    }

    private static bool IsColorful(byte r, byte g, byte b)
    {
        int high = Math.Max(r, Math.Max(g, b));
        int low = Math.Min(r, Math.Min(g, b));
        return high > 75 && high - low > 22;
    }

    private static bool IsD41Diffuser(double x, double y)
    {
        if (y < 0.205 || y > 0.405) return false;
        double t = (y - 0.205) / 0.20;
        double margin = 0.075 - t * 0.035;
        return x >= margin && x <= 1 - margin;
    }

    private static bool IsD80Diffuser(double x, double y)
    {
        // Barra superior: trapecio suave que sigue el borde del acrilico.
        if (y >= 0.07 && y <= 0.195)
        {
            double t = (y - 0.07) / 0.125;
            double margin = 0.075 - t * 0.015;
            if (x >= margin && x <= 1 - margin) return true;
        }

        // Luces triangulares laterales, sin invadir las diagonales metalicas.
        if (y < 0.215 || y > 0.350) return false;
        double sideT = (y - 0.215) / 0.135;
        double innerEdge = 0.150 - sideT * 0.065;
        return (x >= 0.060 && x <= innerEdge)
            || (x <= 0.940 && x >= 1 - innerEdge);
    }

    private static bool[] FindWhiteBackground(byte[] pixels, int width, int height)
    {
        var result = new bool[width * height];
        var pending = new Queue<int>();

        void Add(int x, int y)
        {
            int p = y * width + x;
            if (result[p]) return;
            int i = p * 4;
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            int high = Math.Max(r, Math.Max(g, b));
            int low = Math.Min(r, Math.Min(g, b));
            if (low < 232 || high - low > 18) return;
            result[p] = true;
            pending.Enqueue(p);
        }

        for (int x = 0; x < width; x++) { Add(x, 0); Add(x, height - 1); }
        for (int y = 1; y < height - 1; y++) { Add(0, y); Add(width - 1, y); }

        while (pending.Count > 0)
        {
            int p = pending.Dequeue();
            int x = p % width;
            int y = p / width;
            if (x > 0) Add(x - 1, y);
            if (x + 1 < width) Add(x + 1, y);
            if (y > 0) Add(x, y - 1);
            if (y + 1 < height) Add(x, y + 1);
        }

        return result;
    }

    // ------------------------------------------------------------------ RAM

    private static void DrawDram(Canvas canvas, Device device, string displayName,
                                 Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick)
    {
        Zone zone = device.Zones[0];
        int start = RgbService.ZoneOffsets(device)[zone.Index];
        int count = (int)zone.LedCount;
        bool d41 = displayName.Contains("D41", StringComparison.OrdinalIgnoreCase);
        BitmapSource original = d41 ? D41Original : D80Original;
        ImageSource photo = d41 ? D41Photo : D80Photo;

        const double imageLeft = 60;
        const double imageTop = 170;
        const double imageWidth = 880;
        const double imageHeight = 348;
        var dramPhoto = new Image
        {
            Source = photo,
            Width = imageWidth,
            Height = imageHeight,
            Stretch = Stretch.Fill,
        };
        Canvas.SetLeft(dramPhoto, imageLeft);
        Canvas.SetTop(dramPhoto, imageTop);
        canvas.Children.Add(dramPhoto);

        double cell = imageWidth / count;
        for (int i = 0; i < count; i++)
        {
            int led = start + i;
            if (led >= device.Leds.Length) break;

            Border dot = MakePhotoCell(cell, imageHeight, 0);
            dot.ToolTip = device.Leds[led].Name;
            dot.OpacityMask = new ImageBrush(CreateProductLightMask(
                original, i / (double)count, (i + 1) / (double)count, d41))
            {
                Stretch = Stretch.Fill,
            };
            Attach(dot, new Pick(zone.Index, led), shapes, onClick);

            Canvas.SetLeft(dot, imageLeft + i * cell);
            Canvas.SetTop(dot, imageTop);
            canvas.Children.Add(dot);
        }

        canvas.Children.Add(Caption(
            $"{displayName} — pulsa un segmento de la barra RGB para seleccionarlo",
            60, 545));
    }

    // -------------------------------------------------------------- placa

    private static void DrawMotherboard(Canvas canvas, Device device,
                                        Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick,
                                        Action<int, int>? onResize)
    {
        var catalog = ZoneCatalog.For(device.Name);

        // Las cabeceras de tira no pueden estar en el catalogo: cuantos LED
        // tienen lo decide quien conecta la tira. Van en una franja debajo de
        // la placa, y esa franja le quita altura al dibujo.
        //
        // El filtro es "redimensionable", no "no catalogada": si no, se colaria
        // aqui cualquier zona que el catalogo omite a proposito, como la que en
        // esta placa no ilumina nada.
        Zone[] headers = [.. device.Zones.Where(z => RgbService.IsResizable(z)
                                                  && !catalog.ContainsKey(z.Index))];

        double boardHeight = headers.Length == 0
            ? CanvasHeight
            : CanvasHeight - headers.Length * HeaderRow - 12;

        canvas.Children.Add(new Border
        {
            Width = CanvasWidth,
            Height = boardHeight,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)),
            BorderBrush = Edge,
            BorderThickness = new Thickness(2),
        });

        // La foto conserva la geometria real pero se muestra sin color. Las
        // zonas superpuestas reciben despues el color logico de cada LED, de
        // modo que el naranja de la foto original no compite con el elegido.
        double imageSize = boardHeight - 12;
        double imageLeft = (CanvasWidth - imageSize) / 2;
        const double imageTop = 6;
        var image = new Image
        {
            Source = MotherboardPhoto,
            Width = imageSize,
            Height = imageSize,
            Stretch = Stretch.Uniform,
        };
        Canvas.SetLeft(image, imageLeft);
        Canvas.SetTop(image, imageTop);
        canvas.Children.Add(image);

        foreach (Zone zone in device.Zones.Where(z => z.LedCount > 0))
        {
            if (!catalog.TryGetValue(zone.Index, out ZoneCatalog.ZoneInfo? info)) continue;

            var shape = new Border
            {
                Width = info.Width * imageSize,
                Height = info.Height * imageSize,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = $"{info.Name} — {zone.LedCount} LED\n{info.Description}",
                OpacityMask = new ImageBrush(MotherboardMask(zone.Index, info))
                {
                    Stretch = Stretch.Fill,
                },
            };
            Attach(shape, new Pick(zone.Index, -1), shapes, onClick);

            Canvas.SetLeft(shape, imageLeft + info.X * imageSize);
            Canvas.SetTop(shape, imageTop + info.Y * imageSize);
            canvas.Children.Add(shape);
        }

        DrawHeaders(canvas, device, headers, boardHeight + 12, shapes, onClick, onResize);
    }

    /// <summary>
    /// Convierte la foto a gris conservando su transparencia. Los pixeles que
    /// eran iluminacion naranja se oscurecen mas para que el overlay dinamico
    /// sea el unico color visible.
    /// </summary>
    private static BitmapSource LoadMotherboardOriginal()
    {
        var source = new BitmapImage(new Uri(
            "pack://application:,,,/Assets/Z390AorusProWifi.png", UriKind.Absolute));
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    private static ImageSource LoadMotherboardPhoto()
    {
        int stride = MotherboardOriginal.PixelWidth * 4;
        var pixels = new byte[stride * MotherboardOriginal.PixelHeight];
        MotherboardOriginal.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            if (pixels[i + 3] == 0) continue;

            int gray = (54 * r + 183 * g + 19 * b) >> 8;
            bool wasOrange = r > 110 && r > g * 1.25 && g > b * 1.45;
            if (wasOrange) gray = (int)(gray * 0.28);

            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)gray;
        }

        BitmapSource result = BitmapSource.Create(
            MotherboardOriginal.PixelWidth, MotherboardOriginal.PixelHeight,
            MotherboardOriginal.DpiX, MotherboardOriginal.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Mascara de los pixeles iluminados originalmente dentro de una zona. El
    /// overlay adopta esta silueta: se colorean las letras, tiras y logotipos,
    /// no el rectangulo usado para detectar el clic.
    /// </summary>
    private static ImageSource MotherboardMask(int zoneIndex, ZoneCatalog.ZoneInfo info)
    {
        if (MotherboardMasks.TryGetValue(zoneIndex, out ImageSource? cached)) return cached;

        int x = Math.Clamp((int)(info.X * MotherboardOriginal.PixelWidth),
                           0, MotherboardOriginal.PixelWidth - 1);
        int y = Math.Clamp((int)(info.Y * MotherboardOriginal.PixelHeight),
                           0, MotherboardOriginal.PixelHeight - 1);
        int width = Math.Clamp((int)(info.Width * MotherboardOriginal.PixelWidth),
                               1, MotherboardOriginal.PixelWidth - x);
        int height = Math.Clamp((int)(info.Height * MotherboardOriginal.PixelHeight),
                                1, MotherboardOriginal.PixelHeight - y);

        var crop = new CroppedBitmap(MotherboardOriginal, new Int32Rect(x, y, width, height));
        int stride = width * 4;
        var source = new byte[stride * height];
        var mask = new byte[source.Length];
        crop.CopyPixels(source, stride, 0);

        int lit = 0;
        for (int i = 0; i < source.Length; i += 4)
        {
            byte b = source[i];
            byte g = source[i + 1];
            byte r = source[i + 2];
            byte sourceAlpha = source[i + 3];

            bool orangeLight = sourceAlpha > 0 && r > 115 && r > g * 1.18 && g > b * 1.35;
            byte alpha = orangeLight
                ? (byte)Math.Clamp((r - b) * 2, 45, 255)
                : (byte)0;

            mask[i] = mask[i + 1] = mask[i + 2] = 255;
            mask[i + 3] = alpha;
            if (alpha > 0) lit++;
        }

        // La cabecera de ventiladores es externa y no aparece encendida en la
        // foto. Se representa con un pequeno aro en el centro de su zona.
        if (lit < 20)
        {
            double cx = width / 2.0, cy = height / 2.0;
            double radius = Math.Min(width, height) * 0.14;
            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    double distance = Math.Sqrt(Math.Pow(px - cx, 2) + Math.Pow(py - cy, 2));
                    if (Math.Abs(distance - radius) > 3) continue;
                    int i = py * stride + px * 4;
                    mask[i] = mask[i + 1] = mask[i + 2] = mask[i + 3] = 255;
                }
            }
        }

        BitmapSource result = BitmapSource.Create(
            width, height, MotherboardOriginal.DpiX, MotherboardOriginal.DpiY,
            PixelFormats.Bgra32, null, mask, stride);
        result.Freeze();
        MotherboardMasks[zoneIndex] = result;
        return result;
    }

    /// <summary>Alto reservado a cada cabecera de tira, etiqueta incluida.</summary>
    private const double HeaderRow = 64;

    /// <summary>
    /// Cabeceras de tira direccionable, en la franja de abajo.
    ///
    /// Cada una lleva su casilla de longitud al lado. Va aqui y no en el panel
    /// de la derecha porque no es una preferencia de pintado: describe que hay
    /// conectado a la placa, igual que el resto del mapa.
    ///
    /// Con tira se dibujan sus LEDs uno a uno, como los del teclado. Sin ella
    /// queda solo la etiqueta: la cabecera existe aunque no tenga nada
    /// enchufado, y asi no parece que falte en la aplicacion.
    /// </summary>
    private static void DrawHeaders(Canvas canvas, Device device, Zone[] headers, double top,
                                    Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick,
                                    Action<int, int>? onResize)
    {
        if (headers.Length == 0) return;

        var offsets = RgbService.ZoneOffsets(device);

        for (int i = 0; i < headers.Length; i++)
        {
            Zone zone = headers[i];
            double y = top + i * HeaderRow;

            canvas.Children.Add(Caption(zone.LedCount > 0
                ? $"{zone.Name} — {zone.LedCount} LED"
                : $"{zone.Name} — sin tira conectada", 40, y));

            if (onResize is not null) AddLengthBox(canvas, zone, x: 330, y: y - 5, onResize);

            if (zone.LedCount == 0) continue;

            // Las tiras largas encogen para caber; las cortas no se estiran mas
            // alla del tamano de una tecla.
            double cell = Math.Min(30, (CanvasWidth - 80) / zone.LedCount);
            DrawStrip(canvas, device, zone, offsets[zone.Index], shapes, onClick,
                      x: 40, y: y + 26, cell: cell, height: 30, label: string.Empty);
        }
    }

    /// <summary>Casilla y boton para declarar cuantos LED tiene la tira.</summary>
    private static void AddLengthBox(Canvas canvas, Zone zone, double x, double y,
                                     Action<int, int> onResize)
    {
        var box = new TextBox
        {
            Width = 78,
            Height = 30,
            Text = zone.LedCount.ToString(),
            FontSize = 14,
            Padding = new Thickness(9, 4, 9, 4),
            ToolTip = $"Cuantos LED tiene la tira (entre {zone.LedsMin} y {zone.LedsMax})",
            Style = canvas.TryFindResource("NumberBox") as Style,
        };

        var apply = new Button
        {
            Content = "Fijar",
            Height = 30,
            FontSize = 13,
            Padding = new Thickness(14, 0, 14, 0),
        };

        // Copia local: el objeto Zone se reemplaza al releer el dispositivo.
        int index = zone.Index;
        apply.Click += (_, _) =>
        {
            if (int.TryParse(box.Text.Trim(), out int count) && count >= 0) onResize(index, count);
        };

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        canvas.Children.Add(box);

        Canvas.SetLeft(apply, x + 86);
        Canvas.SetTop(apply, y);
        canvas.Children.Add(apply);
    }

    // ------------------------------------------------- generico y utilidades

    private static void DrawZoneGrid(Canvas canvas, Device device,
                                     Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick)
    {
        int slot = 0;
        foreach (Zone zone in device.Zones.Where(z => z.LedCount > 0))
        {
            Border shape = MakeCell($"{zone.Name}\n{zone.LedCount} LED", 210, 110, 18);
            Attach(shape, new Pick(zone.Index, -1), shapes, onClick);

            Canvas.SetLeft(shape, 40 + slot % 4 * 235);
            Canvas.SetTop(shape, 40 + slot / 4 * 135);
            canvas.Children.Add(shape);
            slot++;
        }
    }

    /// <summary>Fila de LEDs contiguos. Devuelve la Y de abajo.</summary>
    private static double DrawStrip(Canvas canvas, Device device, Zone zone, int start,
                                    Dictionary<Pick, Border> shapes, MouseButtonEventHandler onClick,
                                    double x, double y, double cell, double height, string label)
    {
        if (label.Length > 0) canvas.Children.Add(Caption(label, x, y - 20));

        for (int i = 0; i < zone.LedCount; i++)
        {
            int led = start + i;
            if (led >= device.Leds.Length) break;

            Border dot = MakeCell(string.Empty, cell - 4, height, 10);
            dot.ToolTip = device.Leds[led].Name;
            Attach(dot, new Pick(zone.Index, led), shapes, onClick);

            Canvas.SetLeft(dot, x + i * cell);
            Canvas.SetTop(dot, y);
            canvas.Children.Add(dot);
        }

        return y + height;
    }

    private static Border MakeCell(string text, double w, double h, double fontSize) => new()
    {
        Width = Math.Max(8, w),
        Height = Math.Max(8, h),
        CornerRadius = new CornerRadius(5),
        Background = Face,
        BorderBrush = Edge,
        BorderThickness = new Thickness(1.5),
        Cursor = Cursors.Hand,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Ink,
            FontSize = fontSize,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private static TextBlock Caption(string text, double x, double y)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x88)),
            FontSize = 15,
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        return block;
    }

    private static void Attach(Border shape, Pick pick, Dictionary<Pick, Border> shapes,
                               MouseButtonEventHandler onClick)
    {
        shape.Tag = pick;
        shape.MouseLeftButtonUp += onClick;
        shapes[pick] = shape;
    }
}
