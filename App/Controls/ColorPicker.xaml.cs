using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RgbStudio.Controls;

/// <summary>
/// Selector de color con rueda de tono y cuadro de saturacion/brillo, al estilo
/// del de OpenRGB.
///
/// El anillo se genera como mapa de bits pixel a pixel porque WPF no tiene
/// degradados conicos. El cuadro interior, en cambio, se resuelve con tres
/// capas de degradados lineales: color puro, blanco horizontal y negro
/// vertical.
/// </summary>
public partial class ColorPicker : UserControl
{
    private const int Size = 216;
    private const double OuterRadius = 108;
    private const double InnerRadius = 84;

    private double _hue;          // 0..360
    private double _saturation = 1;
    private double _value = 1;

    private bool _ready;
    private bool _updatingFields;

    /// <summary>Se dispara con cada cambio, tambien mientras se arrastra.</summary>
    public event EventHandler<Color>? ColorChanged;

    public Color SelectedColor => FromHsv(_hue, _saturation, _value);

    private static readonly Color[] PresetColors =
    [
        Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xFF, 0x00, 0x00),
        Color.FromRgb(0xFF, 0x7A, 0x18), Color.FromRgb(0xFF, 0xE0, 0x00),
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x00, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x50, 0xFF), Color.FromRgb(0xFF, 0x00, 0xFF),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    public ColorPicker()
    {
        InitializeComponent();
        HueRing.Source = BuildHueRing();
        BuildPresets();
        _ready = true;
        SetColor(Color.FromRgb(0xFF, 0x7A, 0x18), notify: false);
    }

    // ------------------------------------------------------------ apariencia

    /// <summary>Genera el anillo de tonos. Fuera del anillo queda transparente.</summary>
    private static BitmapSource BuildHueRing()
    {
        var pixels = new byte[Size * Size * 4];
        const double center = Size / 2.0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x - center + 0.5;
                double dy = y - center + 0.5;
                double radius = Math.Sqrt(dx * dx + dy * dy);
                int i = (y * Size + x) * 4;

                if (radius > OuterRadius || radius < InnerRadius) continue;

                double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (angle < 0) angle += 360;

                Color c = FromHsv(angle, 1, 1);

                // Suavizado de los bordes del anillo.
                double edge = Math.Min(OuterRadius - radius, radius - InnerRadius);
                byte alpha = (byte)(Math.Clamp(edge, 0, 1) * 255);

                pixels[i + 0] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = alpha;
            }
        }

        var bmp = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private void BuildPresets()
    {
        foreach (Color color in PresetColors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(0, 0, 5, 5),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            swatch.MouseLeftButtonUp += (_, _) => SetColor(color, notify: true);
            Presets.Items.Add(swatch);
        }
    }

    // --------------------------------------------------------------- entrada

    private void OnRingDown(object sender, MouseButtonEventArgs e)
    {
        HueRing.CaptureMouse();
        PickHue(e.GetPosition(HueRing));
    }

    private void OnRingMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(HueRing));
    }

    private void OnSquareDown(object sender, MouseButtonEventArgs e)
    {
        SvSquare.CaptureMouse();
        PickSv(e.GetPosition(SvSquare));
    }

    private void OnSquareMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PickSv(e.GetPosition(SvSquare));
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        HueRing.ReleaseMouseCapture();
        SvSquare.ReleaseMouseCapture();
    }

    private void PickHue(Point p)
    {
        double dx = p.X - Size / 2.0;
        double dy = p.Y - Size / 2.0;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (angle < 0) angle += 360;

        _hue = angle;
        Refresh(notify: true);
    }

    private void PickSv(Point p)
    {
        _saturation = Math.Clamp(p.X / SvSquare.Width, 0, 1);
        _value = Math.Clamp(1 - p.Y / SvSquare.Height, 0, 1);
        Refresh(notify: true);
    }

    private void OnRgbTyped(object sender, KeyEventArgs e)
    {
        if (_updatingFields) return;
        if (!byte.TryParse(RBox.Text, out byte r)) return;
        if (!byte.TryParse(GBox.Text, out byte g)) return;
        if (!byte.TryParse(BBox.Text, out byte b)) return;

        SetColor(Color.FromRgb(r, g, b), notify: true, skipRgbBoxes: true);
    }

    private void OnHexTyped(object sender, KeyEventArgs e)
    {
        if (_updatingFields) return;

        string text = HexBox.Text.TrimStart('#');
        if (text.Length != 6) return;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return;

        SetColor(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v),
                 notify: true, skipHexBox: true);
    }

    // ---------------------------------------------------------------- estado

    public void SetColor(Color color, bool notify = true,
                         bool skipRgbBoxes = false, bool skipHexBox = false)
    {
        (_hue, _saturation, _value) = ToHsv(color);
        Refresh(notify, skipRgbBoxes, skipHexBox);
    }

    private void Refresh(bool notify, bool skipRgbBoxes = false, bool skipHexBox = false)
    {
        if (!_ready) return;

        Color color = SelectedColor;

        // Fondo del cuadro: el tono puro, con los degradados encima.
        SvHue.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

        // Marcador del anillo, sobre la linea media entre radios.
        const double ringRadius = (OuterRadius + InnerRadius) / 2;
        double rad = _hue * Math.PI / 180;
        Canvas.SetLeft(HueThumb, Size / 2.0 + Math.Cos(rad) * ringRadius - HueThumb.Width / 2);
        Canvas.SetTop(HueThumb, Size / 2.0 + Math.Sin(rad) * ringRadius - HueThumb.Height / 2);
        HueThumb.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

        // Marcador del cuadro.
        double squareLeft = Canvas.GetLeft(SvSquare);
        double squareTop = Canvas.GetTop(SvSquare);
        Canvas.SetLeft(SvThumb, squareLeft + _saturation * SvSquare.Width - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, squareTop + (1 - _value) * SvSquare.Height - SvThumb.Height / 2);
        SvThumb.Fill = new SolidColorBrush(color);

        _updatingFields = true;
        if (!skipRgbBoxes)
        {
            RBox.Text = color.R.ToString();
            GBox.Text = color.G.ToString();
            BBox.Text = color.B.ToString();
        }
        if (!skipHexBox) HexBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";

        HBox.Text = ((int)_hue).ToString();
        SBox.Text = ((int)(_saturation * 100)).ToString();
        VBox.Text = ((int)(_value * 100)).ToString();
        _updatingFields = false;

        if (notify) ColorChanged?.Invoke(this, color);
    }

    // ------------------------------------------------------------ conversion

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

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60 * (((g - b) / delta % 6 + 6) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);
        }

        return (h, max == 0 ? 0 : delta / max, max);
    }
}
