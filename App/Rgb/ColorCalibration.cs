using OpenRGB.NET;
using System.IO;
using System.Text.Json;

namespace RgbStudio.Rgb;

/// <summary>
/// Curvas de color por dispositivo.
///
/// Los valores que guarda la aplicacion siguen siendo los colores logicos. La
/// calibracion solo se aplica a una copia justo antes de enviarla a OpenRGB, de
/// modo que no se acumula al repintar una zona o ejecutar un efecto.
/// </summary>
internal static class ColorCalibration
{
    internal sealed record Profile
    {
        public double RedGain { get; init; } = 1;
        public double GreenGain { get; init; } = 1;
        public double BlueGain { get; init; } = 1;
        public double RedGamma { get; init; } = 1;
        public double GreenGamma { get; init; } = 1;
        public double BlueGamma { get; init; } = 1;

        internal bool IsIdentity =>
            RedGain == 1 && GreenGain == 1 && BlueGain == 1 &&
            RedGamma == 1 && GreenGamma == 1 && BlueGamma == 1;

        internal Profile Clamped() => this with
        {
            RedGain = Math.Clamp(RedGain, 0, 3),
            GreenGain = Math.Clamp(GreenGain, 0, 3),
            BlueGain = Math.Clamp(BlueGain, 0, 3),
            RedGamma = Math.Clamp(RedGamma, 0.1, 4),
            GreenGamma = Math.Clamp(GreenGamma, 0.1, 4),
            BlueGamma = Math.Clamp(BlueGamma, 0.1, 4),
        };
    }

    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RgbStudio", "calibracion.json");

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static Dictionary<string, Profile>? _saved;

    private static string ZoneKey(string deviceName, int zoneIndex)
        => $"{deviceName}::zona:{zoneIndex}";

    private static Dictionary<string, Profile> Saved
    {
        get
        {
            if (_saved is not null) return _saved;

            try
            {
                _saved = File.Exists(Store)
                    ? JsonSerializer.Deserialize<Dictionary<string, Profile>>(File.ReadAllText(Store)) ?? []
                    : [];
            }
            catch
            {
                _saved = [];
            }

            return _saved;
        }
    }

    /// <summary>
    /// Punto inicial medido para la Z390: convierte #E65222 aproximadamente en
    /// #FD6B02 y mantiene correctos los primarios, porque 0 y 255 no cambian
    /// con gamma.
    /// </summary>
    private static Profile DefaultFor(string deviceName)
        => deviceName.Contains("Z390", StringComparison.OrdinalIgnoreCase)
            ? new Profile { RedGain = 1.10, GreenGain = 1.30, BlueGamma = 2.50 }
            : new Profile();

    internal static Profile Initial(string deviceName) => DefaultFor(deviceName);

    internal static Profile Initial(string deviceName, int zoneIndex)
        => DefaultFor(deviceName);

    internal static Profile Get(string deviceName)
    {
        lock (Gate)
            return Saved.TryGetValue(deviceName, out Profile? profile)
                ? profile.Clamped()
                : DefaultFor(deviceName);
    }

    internal static Profile Get(string deviceName, int zoneIndex)
    {
        lock (Gate)
            return Saved.TryGetValue(ZoneKey(deviceName, zoneIndex), out Profile? profile)
                ? profile.Clamped()
                : Get(deviceName);
    }

    private static bool HasZoneProfile(string deviceName, int zoneIndex)
    {
        lock (Gate)
            return Saved.ContainsKey(ZoneKey(deviceName, zoneIndex));
    }

    internal static void Remember(string deviceName, Profile profile)
    {
        lock (Gate)
        {
            Saved[deviceName] = profile.Clamped();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
                File.WriteAllText(Store, JsonSerializer.Serialize(Saved, Format));
            }
            catch
            {
                // La calibracion de esta sesion sigue activa aunque no se pueda guardar.
            }
        }
    }

    internal static void Remember(string deviceName, int zoneIndex, Profile profile)
    {
        lock (Gate)
        {
            Saved[ZoneKey(deviceName, zoneIndex)] = profile.Clamped();
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllText(Store, JsonSerializer.Serialize(Saved, Format));
        }
        catch
        {
            // La calibracion de esta sesion sigue activa aunque no se pueda guardar.
        }
    }

    internal static void Reset(string deviceName)
    {
        lock (Gate)
        {
            Saved.Remove(deviceName);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
                File.WriteAllText(Store, JsonSerializer.Serialize(Saved, Format));
            }
            catch
            {
                // No poder persistir no debe impedir usar el perfil inicial.
            }
        }
    }

    internal static Color Apply(string deviceName, Color color)
    {
        Profile profile = Get(deviceName);
        if (profile.IsIdentity) return color;

        return new Color(
            Curve(color.R, profile.RedGain, profile.RedGamma),
            Curve(color.G, profile.GreenGain, profile.GreenGamma),
            Curve(color.B, profile.BlueGain, profile.BlueGamma));
    }

    internal static Color[] Apply(string deviceName, Color[] colors)
    {
        Profile profile = Get(deviceName);
        if (profile.IsIdentity) return colors;

        var calibrated = new Color[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            Color color = colors[i];
            calibrated[i] = new Color(
                Curve(color.R, profile.RedGain, profile.RedGamma),
                Curve(color.G, profile.GreenGain, profile.GreenGamma),
                Curve(color.B, profile.BlueGain, profile.BlueGamma));
        }
        return calibrated;
    }

    /// <summary>
    /// Aplica el perfil general y despues sustituye las zonas que tengan un
    /// perfil propio. El perfil de zona es absoluto, no se acumula sobre el
    /// general, para que "Neutro" signifique realmente RGB sin correccion.
    /// </summary>
    internal static Color[] Apply(Device device, Color[] colors)
    {
        Color[] output = Apply(device.Name, colors);
        int offset = 0;

        foreach (Zone zone in device.Zones)
        {
            int zoneLedCount = (int)Math.Min(zone.LedCount, (uint)int.MaxValue);
            int count = Math.Min(zoneLedCount, Math.Max(0, colors.Length - offset));
            if (count > 0 && HasZoneProfile(device.Name, zone.Index))
            {
                if (ReferenceEquals(output, colors)) output = [.. colors];

                Profile profile = Get(device.Name, zone.Index);
                for (int i = offset; i < offset + count; i++)
                    output[i] = Apply(profile, colors[i]);
            }

            offset += zoneLedCount;
            if (offset >= colors.Length) break;
        }

        return output;
    }

    private static Color Apply(Profile profile, Color color)
    {
        if (profile.IsIdentity) return color;

        return new Color(
            Curve(color.R, profile.RedGain, profile.RedGamma),
            Curve(color.G, profile.GreenGain, profile.GreenGamma),
            Curve(color.B, profile.BlueGain, profile.BlueGamma));
    }

    private static byte Curve(byte value, double gain, double gamma)
    {
        double corrected = Math.Pow(value / 255.0, gamma) * gain * 255;
        return (byte)Math.Clamp(Math.Round(corrected), 0, 255);
    }
}
