using OpenRGB.NET;

// Herramienta de mapeo de zonas.
//
//   list   lista los dispositivos y sus zonas
//   map    cada zona con un color distinto, todas a la vez
//   walk   una zona cada vez, esperando Enter
//
// Requiere OpenRGB con el servidor SDK activo. Conviene tenerlo minimizado o
// arrancado sin interfaz (OpenRGB.exe --server --startminimized): con la
// ventana visible puede reimponer sus propios colores y pisar los nuestros.

const string Host = "127.0.0.1";
const int Port = 6742;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

OpenRgbClient client;
try
{
    client = new OpenRgbClient(Host, Port, name: "RgbStudio", autoConnect: true);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"No se pudo conectar a {Host}:{Port} — {ex.Message}");
    Console.ResetColor();
    return 1;
}

Device[] devices = client.GetAllControllerData();

if (command == "modes")
{
    Probe.ModeProbe.Run(devices);
    return 0;
}

if (command == "leds")
{
    Probe.LedProbe.Run(devices);
    return 0;
}

if (command == "list")
{
    for (int i = 0; i < devices.Length; i++)
    {
        Device d = devices[i];
        Console.WriteLine($"[{i}] {d.Name}  ({d.Type}, {d.Leds.Length} LEDs)");
        Console.WriteLine($"     modos : {string.Join(", ", d.Modes.Select(m => m.Name))}");
        foreach (Zone z in d.Zones)
            Console.WriteLine($"     zona [{z.Index}] {z.Name} — {z.LedCount} LEDs, {z.Type}");
        Console.WriteLine();
    }
    return 0;
}

int target = Array.FindIndex(devices, d => d.Name.Contains("Z390", StringComparison.OrdinalIgnoreCase));
if (target < 0)
{
    Console.WriteLine("No se encontro la placa base.");
    return 1;
}

Device mb = devices[target];

// El dispositivo debe estar en "Direct" ANTES de enviarle colores: en cualquier
// otro modo el firmware ejecuta su efecto e ignora lo que mandemos.
int direct = Array.FindIndex(mb.Modes, m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase));
if (direct < 0)
{
    Console.WriteLine("Este dispositivo no tiene modo Direct.");
    return 1;
}

client.UpdateMode(target, direct, null, null, null);
Thread.Sleep(300);
Console.WriteLine($"Modo Direct activado (indice {direct}).\n");

// Desplazamiento de cada zona dentro del array de LEDs del dispositivo. Las
// zonas son consecutivas, asi que basta con ir acumulando sus tamanos.
var offsets = new Dictionary<int, int>();
int running = 0;
foreach (Zone z in mb.Zones)
{
    offsets[z.Index] = running;
    running += (int)z.LedCount;
}

Zone[] usable = mb.Zones.Where(z => z.LedCount > 0).ToArray();
if (usable.Length == 0)
{
    Console.WriteLine("Ninguna zona tiene LEDs.");
    return 0;
}

var off = new Color(0, 0, 0);

if (command == "map")
{
    (string Name, Color Value)[] palette =
    {
        ("ROJO",     new Color(255, 0, 0)),
        ("VERDE",    new Color(0, 255, 0)),
        ("AZUL",     new Color(0, 0, 255)),
        ("AMARILLO", new Color(255, 255, 0)),
        ("CIAN",     new Color(0, 255, 255)),
        ("MAGENTA",  new Color(255, 0, 255)),
        ("BLANCO",   new Color(255, 255, 255)),
        ("NARANJA",  new Color(255, 90, 0)),
    };

    Console.WriteLine("Cada zona con su color. Anota donde ves cada uno.\n");

    Color[] frame = Frame(mb, off);
    for (int i = 0; i < usable.Length; i++)
    {
        (string name, Color color) = palette[i % palette.Length];
        Paint(frame, usable[i], offsets, color);
        Console.WriteLine($"  [{usable[i].Index}] {usable[i].Name,-18} → {name}");
    }

    Send(client, target, frame);
    Console.WriteLine("\nPuestos. Enter para apagar.");
    Console.ReadLine();
    Send(client, target, Frame(mb, off));
    return 0;
}

if (command == "walk")
{
    var on = new Color(0, 255, 0);
    Console.WriteLine("Una zona cada vez, el resto apagadas. Escribe que ves y pulsa Enter.\n");

    foreach (Zone zone in usable)
    {
        Color[] frame = Frame(mb, off);
        Paint(frame, zone, offsets, on);
        Send(client, target, frame);

        Console.Write($"  [{zone.Index}] {zone.Name,-18} → ");
        Console.ReadLine();
    }

    Send(client, target, Frame(mb, off));
    return 0;
}

Console.WriteLine($"Comando desconocido: '{command}'. Usa: list | map | walk");
return 1;

// ---------------------------------------------------------------------------

static Color[] Frame(Device device, Color fill)
    => Enumerable.Repeat(fill, device.Leds.Length).ToArray();

static void Paint(Color[] frame, Zone zone, Dictionary<int, int> offsets, Color color)
{
    int start = offsets[zone.Index];
    for (int i = 0; i < zone.LedCount && start + i < frame.Length; i++)
        frame[start + i] = color;
}

/// <summary>
/// Envia el estado completo del dispositivo en UNA sola llamada.
///
/// Mandar una actualizacion por zona (varios paquetes seguidos) hace que el
/// controlador del IT8297 se pierda: aplica la primera y descarta el resto, o
/// no aplica ninguna. Un unico UpdateLeds con todos los colores es fiable.
/// Se repite una vez tras una pausa porque el primer envio tras cambiar de modo
/// a veces se pierde.
/// </summary>
static void Send(OpenRgbClient client, int device, Color[] frame)
{
    client.UpdateLeds(device, frame);
    Thread.Sleep(120);
    client.UpdateLeds(device, frame);
}
