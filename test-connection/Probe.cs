using OpenRGB.NET;

namespace Probe;

/// <summary>
/// Vuelca los nombres de LED y la matriz fisica de cada dispositivo.
///
/// Es lo que decide si un dispositivo se puede dibujar automaticamente o hay
/// que mapearlo a mano como hicimos con la placa.
/// </summary>
internal static class LedProbe
{
    internal static void Run(Device[] devices)
    {
        foreach (Device d in devices)
        {
            Console.WriteLine($"=== {d.Name} ({d.Type}) ===");

            foreach (Zone z in d.Zones)
            {
                Console.WriteLine($"  Zona '{z.Name}' — {z.LedCount} LEDs, tipo {z.Type}");

                if (z.MatrixMap is not null)
                {
                    Console.WriteLine($"     MATRIZ {z.MatrixMap.Height} filas x {z.MatrixMap.Width} columnas");
                }
            }

            // Nombres de los primeros LEDs: si son descriptivos, se pueden
            // colocar sin adivinar nada.
            Console.WriteLine($"  Nombres de LED (primeros 14 de {d.Leds.Length}):");
            foreach (Led led in d.Leds.Take(14))
                Console.Write($"    '{led.Name}'");
            Console.WriteLine();
            Console.WriteLine();
        }
    }
}
