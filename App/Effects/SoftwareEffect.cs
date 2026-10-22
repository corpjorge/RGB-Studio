using OpenRGB.NET;

namespace RgbStudio.Effects;

/// <summary>
/// Lo que un efecto sabe del LED que esta pintando.
///
/// Position sirve para todo (una tira, una fila de zonas). X e Y solo tienen
/// sentido cuando el dispositivo declara una matriz — el teclado la trae, con
/// sus 6 filas por 23 columnas — y permiten efectos que entienden el espacio:
/// lluvia que cae, ondas que se expanden, barridos en diagonal.
/// </summary>
internal readonly record struct LedContext(
    double Position, double X, double Y, int Index, bool HasMatrix);

internal abstract class SoftwareEffect
{
    internal abstract string Name { get; }

    /// <summary>true si el efecto necesita la matriz para lucirse.</summary>
    internal virtual bool NeedsMatrix => false;

    /// <summary>false cuando la animacion sigue eventos y no un reloj.</summary>
    internal virtual bool UsesSpeed => true;

    internal abstract Color Sample(double seconds, LedContext led, Color baseColor);

    public override string ToString() => Name;

    // ---------------------------------------------------------- utilidades

    protected static Color FromHsv(double h, double s, double v)
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

        return new Color((byte)Math.Round((r + m) * 255),
                         (byte)Math.Round((g + m) * 255),
                         (byte)Math.Round((b + m) * 255));
    }

    protected static Color Scale(Color c, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new Color((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    protected static Color Mix(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Color(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    protected static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta <= 0) return 0;

        if (max == r) return 60 * (((g - b) / delta % 6 + 6) % 6);
        if (max == g) return 60 * ((b - r) / delta + 2);
        return 60 * ((r - g) / delta + 4);
    }

    /// <summary>
    /// Ruido estable por LED: el mismo indice da siempre el mismo valor.
    ///
    /// Hace falta para el fuego y la lluvia, donde cada LED debe tener su
    /// propio ritmo. Con Random de verdad titilarian todos a la vez de forma
    /// distinta en cada cuadro, y se veria como estatica de television.
    /// </summary>
    protected static double Noise(int index, int salt = 0)
    {
        unchecked
        {
            uint h = (uint)(index * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
        }
    }
}

// ---------------------------------------------------------------- lineales

internal sealed class WaveEffect : SoftwareEffect
{
    internal override string Name => "Onda";
    private const double Width = 0.28;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double d = Math.Abs(led.Position - seconds % 1.0);
        d = Math.Min(d, 1 - d);                       // la onda da la vuelta
        double intensity = Math.Max(0, 1 - d / Width);
        return Scale(baseColor, intensity * intensity);
    }
}

internal sealed class BreatheEffect : SoftwareEffect
{
    internal override string Name => "Respiracion suave";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
        => Scale(baseColor, 0.12 + (Math.Sin(seconds * 2 * Math.PI) + 1) / 2 * 0.88);
}

internal sealed class RainbowEffect : SoftwareEffect
{
    internal override string Name => "Arcoiris";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
        => FromHsv(led.Position * 360 - seconds * 360, 1, 1);
}

internal sealed class GradientEffect : SoftwareEffect
{
    internal override string Name => "Degradado en movimiento";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double t = (Math.Sin((led.Position - seconds) * 2 * Math.PI) + 1) / 2;
        return FromHsv(HueOf(baseColor) + t * 80, 1, 1);
    }
}

/// <summary>Cabeza brillante con estela que se apaga detras.</summary>
internal sealed class CometEffect : SoftwareEffect
{
    internal override string Name => "Cometa";
    private const double Tail = 0.45;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double head = seconds % 1.0;
        double behind = head - led.Position;
        if (behind < 0) behind += 1;                  // la estela cruza el final

        if (behind > Tail) return new Color(0, 0, 0);

        double fade = 1 - behind / Tail;
        return Scale(baseColor, fade * fade * fade);
    }
}

/// <summary>Dos pulsos seguidos y una pausa, como un latido.</summary>
internal sealed class HeartbeatEffect : SoftwareEffect
{
    internal override string Name => "Latido";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double t = seconds % 1.0;
        double beat = Pulse(t, 0.00) + Pulse(t, 0.22);
        return Scale(baseColor, 0.08 + Math.Min(1, beat) * 0.92);
    }

    private static double Pulse(double t, double at)
    {
        double d = t - at;
        return d is >= 0 and < 0.14 ? Math.Sin(d / 0.14 * Math.PI) : 0;
    }
}

/// <summary>Llamas: tonos calidos con parpadeo propio en cada LED.</summary>
internal sealed class FireEffect : SoftwareEffect
{
    internal override string Name => "Fuego";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        // Tres ondas de distinta frecuencia y desfase por LED: da un titileo
        // irregular que no se repite a simple vista.
        double n = Noise(led.Index);
        double flicker =
            0.55 +
            0.25 * Math.Sin((seconds * 6.1 + n * 12) * Math.PI) +
            0.20 * Math.Sin((seconds * 9.7 + n * 31) * Math.PI);

        flicker = Math.Clamp(flicker, 0.15, 1);

        // Del rojo profundo al amarillo segun la intensidad.
        return FromHsv(8 + flicker * 42, 1, flicker);
    }
}

/// <summary>Ciclo de color, todo el conjunto del mismo tono a la vez.</summary>
internal sealed class ColorCycleEffect : SoftwareEffect
{
    internal override string Name => "Ciclo de color";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
        => FromHsv(seconds * 120, 1, 1);
}

/// <summary>Velos de color que se desplazan lentamente en varias direcciones.</summary>
internal sealed class AuroraEffect : SoftwareEffect
{
    internal override string Name => "Aurora";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double first = Math.Sin((led.X * 1.8 + led.Y * 0.7 - seconds * 0.32) * 2 * Math.PI);
        double second = Math.Sin((led.X * -0.8 + led.Y * 1.6 + seconds * 0.21) * 2 * Math.PI);
        double blend = (first + second + 2) / 4;
        double hue = HueOf(baseColor) - 45 + blend * 125;
        double brightness = 0.18 + Math.Pow(blend, 1.35) * 0.82;
        return FromHsv(hue, 0.88, brightness);
    }
}

/// <summary>Destellos independientes que aparecen y desaparecen suavemente.</summary>
internal sealed class StarsEffect : SoftwareEffect
{
    internal override string Name => "Estrellas";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double speed = 0.28 + Noise(led.Index, 11) * 0.65;
        double phase = (seconds * speed + Noise(led.Index, 12)) % 1.0;
        double distance = Math.Abs(phase - 0.16);
        double sparkle = Math.Exp(-distance * distance / 0.008);

        // Algunos LED son estrellas tenues y otros alcanzan todo el brillo.
        double strength = 0.35 + Noise(led.Index, 13) * 0.65;
        return Scale(baseColor, 0.025 + sparkle * strength);
    }
}

/// <summary>Una barra luminosa rebota entre ambos extremos.</summary>
internal sealed class ScannerEffect : SoftwareEffect
{
    internal override string Name => "Escáner";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double cycle = seconds % 2.0;
        double head = cycle <= 1 ? cycle : 2 - cycle;
        double distance = Math.Abs(led.Position - head);
        double glow = Math.Max(0, 1 - distance / 0.24);
        return Scale(baseColor, 0.025 + glow * glow * glow * 0.975);
    }
}

/// <summary>Franjas alternas de color que avanzan por el dispositivo.</summary>
internal sealed class BandsEffect : SoftwareEffect
{
    internal override string Name => "Bandas luminosas";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double wave = (Math.Sin((led.Position * 5 - seconds * 0.7) * 2 * Math.PI) + 1) / 2;
        double hue = HueOf(baseColor) + wave * 80;
        double brightness = 0.28 + wave * 0.72;
        return FromHsv(hue, 0.95, brightness);
    }
}

/// <summary>Interferencia de ondas que forma manchas fluidas de color.</summary>
internal sealed class PlasmaEffect : SoftwareEffect
{
    internal override string Name => "Plasma";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double a = Math.Sin((led.X * 2.2 + seconds * 0.43) * 2 * Math.PI);
        double b = Math.Sin((led.Y * 2.7 - seconds * 0.37) * 2 * Math.PI);
        double c = Math.Sin(((led.X + led.Y) * 1.45 + seconds * 0.24) * 2 * Math.PI);
        double plasma = (a + b + c + 3) / 6;
        return FromHsv(HueOf(baseColor) + plasma * 180, 0.92, 0.38 + plasma * 0.62);
    }
}

/// <summary>
/// Bandera colombiana: amarillo ocupa la mitad, azul y rojo un cuarto cada uno.
/// En el teclado usa filas; en dispositivos lineales distribuye las franjas a
/// lo largo de sus LED.
/// </summary>
internal sealed class ColombiaEffect : SoftwareEffect
{
    internal override string Name => "Bandera de Colombia";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double axis = led.HasMatrix ? led.Y : led.Position;
        Color stripe = axis <= 0.50
            ? new Color(252, 209, 22)
            : axis <= 0.75
                ? new Color(0, 56, 147)
                : new Color(206, 17, 38);

        // Una variacion leve da vida a la bandera sin alterar sus colores.
        double wave = 0.84 + 0.16 * (Math.Sin(
            (led.X * 1.5 + led.Y * 0.6 - seconds * 0.35) * 2 * Math.PI) + 1) / 2;
        return Scale(stripe, wave);
    }
}

/// <summary>Rosas, lilas y destellos blancos con acabado perlado.</summary>
internal sealed class PrincessEffect : SoftwareEffect
{
    internal override string Name => "Princesa";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double ribbon = (Math.Sin(
            (led.X * 1.7 + led.Y * 1.2 - seconds * 0.38) * 2 * Math.PI) + 1) / 2;
        Color pink = FromHsv(305 + ribbon * 42, 0.58 + ribbon * 0.25, 0.72 + ribbon * 0.28);

        double sparklePhase = (seconds * (0.38 + Noise(led.Index, 21) * 0.5)
                               + Noise(led.Index, 22)) % 1.0;
        double distance = Math.Abs(sparklePhase - 0.12);
        double sparkle = Math.Exp(-distance * distance / 0.004)
                         * (0.35 + Noise(led.Index, 23) * 0.65);

        return Mix(pink, new Color(255, 242, 255), sparkle);
    }
}

/// <summary>Destellos alternos rojos y azules, como luces de emergencia.</summary>
internal sealed class PoliceEffect : SoftwareEffect
{
    internal override string Name => "Policía";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        int beat = (int)(seconds * 6) % 6;
        bool left = led.Position < 0.5;
        bool redBeat = beat is 0 or 1;
        bool blueBeat = beat is 3 or 4;

        if ((left && redBeat) || (!left && blueBeat))
            return new Color(255, 0, 24);
        if ((!left && redBeat) || (left && blueBeat))
            return new Color(0, 70, 255);

        return new Color(8, 0, 12);
    }
}

/// <summary>Dos respiraciones sucesivas que alternan el color base y uno complementario.</summary>
internal sealed class DoubleBreatheEffect : SoftwareEffect
{
    internal override string Name => "Respiración doble";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double cycle = seconds % 2.0;
        bool secondColor = cycle >= 1;
        double local = cycle % 1.0;
        double breath = 0.07 + Math.Pow(Math.Sin(local * Math.PI), 2) * 0.93;
        Color color = secondColor
            ? FromHsv(HueOf(baseColor) + 125, 0.88, 1)
            : baseColor;
        return Scale(color, breath);
    }
}

/// <summary>Frentes de onda repetidos que nacen en el centro sin pausas.</summary>
internal sealed class ContinuousRippleEffect : SoftwareEffect
{
    internal override string Name => "Onda expansiva continua";

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double dx = led.X - 0.5;
        double dy = led.HasMatrix ? led.Y - 0.5 : 0;
        double radius = Math.Sqrt(dx * dx + dy * dy);
        double phase = (radius * 3.2 - seconds * 0.9) % 1.0;
        if (phase < 0) phase += 1;

        double ring = Math.Exp(-Math.Pow((phase - 0.12) / 0.11, 2));
        return Scale(baseColor, 0.025 + ring * 0.975);
    }
}

/// <summary>Ilumina la tecla pulsada y propaga un halo corto a sus vecinas.</summary>
internal sealed class ReactiveKeysEffect : SoftwareEffect
{
    internal override string Name => "Teclas reactivas";
    internal override bool NeedsMatrix => true;
    internal override bool UsesSpeed => false;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        if (!led.HasMatrix) return Scale(baseColor, 0.02);
        return Scale(baseColor, 0.02 + KeyboardPulseTracker.Intensity(led.X, led.Y) * 0.98);
    }
}

/// <summary>Medidor de volumen que reacciona al audio reproducido por Windows.</summary>
internal sealed class AudioReactiveEffect : SoftwareEffect
{
    internal override string Name => "Audio reactivo";
    internal override bool UsesSpeed => false;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double level = AudioLevelMonitor.Level;

        // Los dispositivos sin matriz suelen tener muy pocos LED: una barra
        // espacial casi nunca alcanza sus posiciones. En ellos se usa solo el
        // golpe detectado: placa y RAM se apagan entre beats y en silencio.
        if (!led.HasMatrix)
        {
            double beat = AudioLevelMonitor.Beat;
            double devicePulse = beat < 0.035
                ? 0
                : Math.Pow((beat - 0.035) / 0.965, 0.78);
            return Scale(baseColor, devicePulse);
        }

        double position = 1 - led.Y;

        // Una transicion corta evita que los extremos de la barra parpadeen.
        double bar = Math.Clamp((level - position) * 9 + 0.5, 0, 1);
        double pulse = 0.015 + bar * 0.985;
        return Scale(baseColor, pulse);
    }
}

// ------------------------------------------------------------- espaciales

/// <summary>Gotas que caen por las columnas del teclado.</summary>
internal sealed class RainEffect : SoftwareEffect
{
    internal override string Name => "Lluvia";
    internal override bool NeedsMatrix => true;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        // Cada columna cae a su ritmo y con su desfase.
        int column = (int)Math.Round(led.X * 22);
        double speed = 0.6 + Noise(column, 1) * 0.9;
        double offset = Noise(column, 2);

        double drop = (seconds * speed + offset) % 1.0;
        double distance = led.Y - drop;
        if (distance < 0) distance += 1;

        double fade = Math.Max(0, 1 - distance * 4);
        return Scale(baseColor, fade * fade);
    }
}

/// <summary>Anillos que se expanden desde el centro.</summary>
internal sealed class RippleEffect : SoftwareEffect
{
    internal override string Name => "Ondas concentricas";
    internal override bool NeedsMatrix => true;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double dx = led.X - 0.5;
        double dy = led.Y - 0.5;
        double radius = Math.Sqrt(dx * dx + dy * dy) * 1.6;

        // Un seno del radio menos el tiempo produce anillos que se alejan.
        double wave = Math.Sin((radius - seconds) * 2 * Math.PI * 2);
        return Scale(baseColor, Math.Max(0, wave));
    }
}

/// <summary>Franja de luz que cruza en diagonal.</summary>
internal sealed class DiagonalEffect : SoftwareEffect
{
    internal override string Name => "Barrido diagonal";
    internal override bool NeedsMatrix => true;

    internal override Color Sample(double seconds, LedContext led, Color baseColor)
    {
        double d = (led.X * 0.7 + led.Y * 0.3 - seconds) % 1.0;
        if (d < 0) d += 1;

        double fade = Math.Max(0, 1 - d / 0.3);
        return Scale(baseColor, fade * fade);
    }
}
