namespace RgbStudio.Rgb;

/// <summary>
/// Nombres reales de las zonas, y donde estan fisicamente.
///
/// El catalogo conserva las posiciones fisicas y las descripciones que usa la
/// interfaz. Comprobado a ojo, zona por zona, el 2026-08-06 sobre una Z390
/// AORUS PRO WIFI.
/// </summary>
internal static class ZoneCatalog
{
    /// <summary>Descripcion de una zona para la interfaz.</summary>
    internal sealed record ZoneInfo(
        string Name,
        string Description,
        double X,
        double Y,
        double Width,
        double Height);

    /// <summary>
    /// Mapa para la Z390 AORUS PRO WIFI. Las coordenadas son relativas al
    /// esquema de la placa que dibuja la interfaz (0..1 en ambos ejes), no
    /// medidas reales.
    ///
    /// Los indices corresponden al diseño especifico de OpenRGB. La cabecera
    /// D_LED1/D_LED2 es la zona 0 y no se dibuja porque no tiene tira conectada.
    /// </summary>
    private static readonly Dictionary<int, ZoneInfo> Z390 = new()
    {
        [1] = new("Logo AORUS", "Logotipo sobre el respaldo de conectores",
                  0.12, 0.10, 0.22, 0.24),

        [2] = new("Contorno RAM", "Tiras a los lados de los zocalos de memoria",
                  0.65, 0.05, 0.14, 0.50),

        [3] = new("Aguila AORUS", "Logotipo del aguila, parte inferior",
                  0.59, 0.59, 0.22, 0.31),

        [4] = new("Linea inferior", "Franja del borde inferior izquierdo",
                  0.13, 0.55, 0.18, 0.40),

        [5] = new("Ventiladores y disipador", "Cabecera RGB: ventiladores de caja y disipador",
                  0.34, 0.17, 0.28, 0.30),
    };

    /// <summary>Zonas conocidas de un dispositivo, o vacio si no esta catalogado.</summary>
    internal static IReadOnlyDictionary<int, ZoneInfo> For(string deviceName)
        => deviceName.Contains("Z390", StringComparison.OrdinalIgnoreCase)
            ? Z390
            : new Dictionary<int, ZoneInfo>();

    /// <summary>
    /// Nombre legible de una zona. Cae en el nombre original de OpenRGB si el
    /// dispositivo no esta catalogado.
    /// </summary>
    internal static string Describe(string deviceName, int zoneIndex, string fallback)
        => For(deviceName).TryGetValue(zoneIndex, out ZoneInfo? info) ? info.Name : fallback;
}
