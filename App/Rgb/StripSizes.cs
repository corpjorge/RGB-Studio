using OpenRGB.NET;
using System.IO;
using System.Text.Json;

namespace RgbStudio.Rgb;

/// <summary>
/// Recuerda cuantos LED tiene cada tira conectada.
///
/// La longitud de una tira direccionable no se puede averiguar: la escribe
/// quien la conecta. Y OpenRGB arranca con las cabeceras a cero, asi que sin
/// guardarla habria que volver a teclearla en cada arranque.
/// </summary>
internal static class StripSizes
{
    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RgbStudio", "tiras.json");

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private static Dictionary<string, int>? _saved;

    private static Dictionary<string, int> Saved
    {
        get
        {
            if (_saved is not null) return _saved;

            try
            {
                _saved = File.Exists(Store)
                    ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Store)) ?? []
                    : [];
            }
            catch
            {
                // Un fichero corrupto o sin permisos no debe impedir arrancar:
                // como mucho se pierde la comodidad de no volver a teclearlo.
                _saved = [];
            }

            return _saved;
        }
    }

    // El nombre del dispositivo, y no su indice, porque el orden en que OpenRGB
    // los enumera cambia si se conecta o desconecta algo.
    private static string Key(string deviceName, int zoneIndex) => $"{deviceName}|{zoneIndex}";

    internal static void Remember(string deviceName, int zoneIndex, int ledCount)
    {
        Saved[Key(deviceName, zoneIndex)] = ledCount;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllText(Store, JsonSerializer.Serialize(Saved, Format));
        }
        catch
        {
            // Igual que arriba: no poder guardar no rompe nada de esta sesion.
        }
    }

    /// <summary>Vuelve a declarar las longitudes guardadas. Se llama al conectar.</summary>
    internal static void Restore(RgbService rgb)
    {
        if (Saved.Count == 0) return;

        // Se planifica antes de tocar nada: cada cambio de tamano hace que el
        // servicio relea los dispositivos, y las referencias de este bucle
        // quedarian obsoletas a mitad de recorrido.
        var plan = new List<(int Device, int Zone, int Count)>();

        for (int d = 0; d < rgb.Devices.Count; d++)
        {
            Device device = rgb.Devices[d];

            foreach (Zone zone in device.Zones.Where(RgbService.IsResizable))
            {
                if (!Saved.TryGetValue(Key(device.Name, zone.Index), out int count)) continue;
                if (count == zone.LedCount) continue;   // ya esta como toca

                plan.Add((d, zone.Index, count));
            }
        }

        foreach ((int device, int zone, int count) in plan)
            rgb.ResizeZone(device, zone, count);
    }
}
