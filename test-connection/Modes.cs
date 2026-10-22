using OpenRGB.NET;

namespace Probe;

/// <summary>
/// Vuelca como declara cada modo sus colores.
///
/// Decide donde hay que mandar el color: dentro del paquete del modo
/// (ModeSpecific) o aparte con UpdateLeds (PerLed). Elegir mal explica que un
/// efecto salga siempre rojo o siempre blanco, ignorando lo que se pida.
/// </summary>
internal static class ModeProbe
{
    internal static void Run(Device[] devices)
    {
        foreach (Device d in devices)
        {
            Console.WriteLine($"=== {d.Name} ===");
            foreach (Mode m in d.Modes)
            {
                int colorCount = m.Colors?.Length ?? -1;
                Console.WriteLine($"  {m.Name,-16} ColorMode={m.ColorMode,-13} "
                                + $"colores={colorCount,-3} banderas={m.Flags}");
            }
            Console.WriteLine();
        }
    }
}
