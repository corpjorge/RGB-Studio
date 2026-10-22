using OpenRGB.NET;
using System.IO;

namespace RgbStudio.Rgb;

/// <summary>
/// Toda la comunicacion con el hardware pasa por aqui.
///
/// Se apoya en el servidor SDK de OpenRGB, que a su vez habla con la placa por
/// USB HID, con la RAM por SMBus y con los perifericos por sus protocolos. La
/// aplicacion solo ve "dispositivos con zonas y colores".
/// </summary>
internal sealed class RgbService : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 6742;

    private OpenRgbClient? _client;
    private Device[] _devices = [];

    /// <summary>
    /// Color actual de cada LED, por dispositivo.
    ///
    /// El protocolo obliga a enviar SIEMPRE el estado completo del dispositivo:
    /// no existe "cambia solo este LED". Sin recordar lo anterior, pintar una
    /// zona apagaria todas las demas. Aqui se conserva, se modifica lo que toca
    /// y se manda entero.
    /// </summary>
    private readonly Dictionary<int, Color[]> _state = [];

    /// <summary>
    /// Serializa el acceso al cliente y al estado.
    ///
    /// El motor de efectos escribe desde un hilo de fondo mientras la interfaz
    /// puede estar aplicando un color. Ni OpenRgbClient ni los arrays de estado
    /// admiten uso concurrente.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Dispositivos que ya estan en modo directo.
    ///
    /// Volver a pedirlo cuesta una pausa de 200 ms cada vez, y hay caminos que
    /// lo piden para todos los dispositivos seguidos — seguir a la aplicacion
    /// en primer plano, sin ir mas lejos.
    /// </summary>
    private readonly HashSet<int> _direct = [];

    internal IReadOnlyList<Device> Devices => _devices;
    internal bool Connected => _client is not null;

    /// <summary>Conecta y lee el inventario. Devuelve null si fue bien, o el motivo del fallo.</summary>
    internal string? Connect()
    {
        try
        {
            _client = new OpenRgbClient(Host, Port, name: "RGB Studio", autoConnect: true);
            _devices = _client.GetAllControllerData();
            _direct.Clear();
            return null;
        }
        catch (Exception ex)
        {
            _client = null;
            _devices = [];
            return ex.Message;
        }
    }

    internal void Refresh()
    {
        if (_client is null) return;
        _devices = _client.GetAllControllerData();
    }

    /// <summary>
    /// Pone el dispositivo bajo control del software.
    ///
    /// Imprescindible antes de enviar colores: en cualquier otro modo el
    /// firmware ejecuta su propio efecto e ignora lo que le mandemos.
    /// </summary>
    internal bool EnterDirectMode(int deviceIndex)
    {
        if (_client is null || deviceIndex >= _devices.Length) return false;

        Device device = _devices[deviceIndex];
        int direct = Array.FindIndex(device.Modes,
            m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase));

        if (direct < 0) return false;

        lock (_gate)
        {
            if (_direct.Contains(deviceIndex)) return true;

            _client.UpdateMode(deviceIndex, direct, null, null, null);
            Thread.Sleep(200);
            _direct.Add(deviceIndex);
        }
        return true;
    }

    /// <summary>Que ha pasado con el color al aplicar un efecto de firmware.</summary>
    internal enum ModeResult { NotFound, ColorApplied, ColorIgnored }

    /// <summary>
    /// Aplica un efecto de firmware con su color y velocidad.
    ///
    /// La clave esta en ColorMode, que dice DONDE espera el color cada modo:
    ///
    ///   ModeSpecific — viaja dentro del paquete del modo (Z390: Static,
    ///                  Breathing, Double Flash...).
    ///   PerLed       — el paquete del modo NO lleva color; hay que mandarlo
    ///                  aparte con UpdateLeds (la RAM y el teclado usan esto).
    ///   None         — el modo no admite color (Rainbow, Spectrum Cycle, Wave).
    ///
    /// Tratar todo como ModeSpecific hacia que los PerLed se quedaran con el
    /// color que tuviera el aparato: rojo en la RAM, blanco en el teclado.
    /// </summary>
    internal ModeResult SetMode(int deviceIndex, string modeName, Color color, int speedPercent = 50)
    {
        if (_client is null || deviceIndex >= _devices.Length) return ModeResult.NotFound;

        Device device = _devices[deviceIndex];
        int index = Array.FindIndex(device.Modes,
            m => m.Name.Equals(modeName, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return ModeResult.NotFound;

        Mode mode = device.Modes[index];

        uint? speed = null;
        if (mode.Flags.HasFlag(ModeFlags.HasSpeed))
        {
            double t = Math.Clamp(speedPercent, 0, 100) / 100.0;
            speed = (uint)(mode.SpeedMin + (mode.SpeedMax - mode.SpeedMin) * t);
        }

        Color[]? modeColors = null;
        if (mode.ColorMode == ColorMode.ModeSpecific)
        {
            // Algunos piden varios (el Breathing del teclado usa 2).
            int count = Math.Max(1, mode.Colors?.Length ?? 1);
            Color calibrated = ColorCalibration.Apply(device.Name, color);
            modeColors = Enumerable.Repeat(calibrated, count).ToArray();
        }

        lock (_gate)
        {
            _client.UpdateMode(deviceIndex, index, speed, null, modeColors);
            _direct.Remove(deviceIndex);   // ya no esta en directo

            if (mode.ColorMode == ColorMode.ModeSpecific)
                Array.Fill(StateUnlocked(deviceIndex), color);

            if (mode.ColorMode == ColorMode.PerLed)
            {
                // El modo ya esta puesto; ahora se le da el color por el otro canal.
                Thread.Sleep(60);
                Color[] frame = StateUnlocked(deviceIndex);
                Array.Fill(frame, color);
                Apply(deviceIndex, frame);
                return ModeResult.ColorApplied;
            }
        }

        return mode.ColorMode == ColorMode.None
            ? ModeResult.ColorIgnored
            : ModeResult.ColorApplied;
    }

    /// <summary>
    /// Envia el estado completo de un dispositivo.
    ///
    /// SIEMPRE el dispositivo entero en una sola llamada, nunca zona por zona:
    /// mandar varios paquetes seguidos hace que el controlador del IT8297
    /// aplique el primero y descarte el resto. Se repite tras una pausa porque
    /// el primer envio despues de cambiar de modo a veces se pierde.
    /// </summary>
    internal void Apply(int deviceIndex, Color[] frame)
    {
        if (_client is null) return;

        Color[] output = ColorCalibration.Apply(_devices[deviceIndex], frame);
        _client.UpdateLeds(deviceIndex, output);
        Thread.Sleep(80);
        _client.UpdateLeds(deviceIndex, output);
    }

    /// <summary>
    /// Envio unico, sin repeticion ni pausa.
    ///
    /// Para actualizaciones continuas (arrastrar por la rueda de color): la
    /// version con pausa bloquearia el hilo de la interfaz y la dejaria a
    /// tirones. Aqui no importa perder algun cuadro, porque el siguiente llega
    /// enseguida.
    /// </summary>
    internal void ApplyLive(int deviceIndex, Color[] frame)
    {
        if (_client is null) return;
        Color[] output = ColorCalibration.Apply(_devices[deviceIndex], frame);
        _client.UpdateLeds(deviceIndex, output);
    }

    /// <summary>Lienzo con todos los LEDs de un dispositivo apagados.</summary>
    internal static Color[] BlankFrame(Device device)
        => Enumerable.Repeat(new Color(0, 0, 0), device.Leds.Length).ToArray();

    /// <summary>Estado actual de un dispositivo. Llamar siempre dentro del cerrojo.</summary>
    private Color[] StateUnlocked(int deviceIndex)
    {
        Device device = _devices[deviceIndex];

        if (!_state.TryGetValue(deviceIndex, out Color[]? frame) || frame.Length != device.Leds.Length)
        {
            frame = device.Colors.Length == device.Leds.Length
                ? [.. device.Colors]
                : BlankFrame(device);
            _state[deviceIndex] = frame;
        }

        return frame;
    }

    /// <summary>Copia estable del color logico actual de cada LED.</summary>
    internal Color[] Snapshot(int deviceIndex)
    {
        lock (_gate)
            return deviceIndex >= 0 && deviceIndex < _devices.Length
                ? [.. StateUnlocked(deviceIndex)]
                : [];
    }

    /// <summary>Pinta un rango de LEDs y envia el dispositivo entero.</summary>
    internal void PaintRange(int deviceIndex, int start, int end, Color color, bool live)
    {
        lock (_gate)
        {
            Color[] frame = StateUnlocked(deviceIndex);
            for (int i = Math.Max(0, start); i < Math.Min(end, frame.Length); i++)
                frame[i] = color;

            Send(deviceIndex, frame, live);
        }
    }

    /// <summary>
    /// Deja que el motor de efectos rellene el estado y lo envia.
    ///
    /// El pintado ocurre dentro del cerrojo para que no se solape con un cambio
    /// de color desde la interfaz.
    /// </summary>
    internal void PaintAndSend(int deviceIndex, Action<Color[]> paint)
    {
        lock (_gate)
        {
            Color[] frame = StateUnlocked(deviceIndex);
            paint(frame);
            Send(deviceIndex, frame, live: true);
        }
    }

    private void Send(int deviceIndex, Color[] frame, bool live)
    {
        if (live) ApplyLive(deviceIndex, frame);
        else Apply(deviceIndex, frame);
    }

    /// <summary>Rango de LEDs que ocupa una zona: [inicio, fin).</summary>
    internal (int Start, int End) ZoneRange(int deviceIndex, int zoneIndex)
    {
        Device device = _devices[deviceIndex];
        if (zoneIndex < 0) return (0, device.Leds.Length);

        Zone? zone = device.Zones.FirstOrDefault(z => z.Index == zoneIndex);
        if (zone is null) return (0, device.Leds.Length);

        int start = ZoneOffsets(device)[zoneIndex];
        return (start, Math.Min(start + (int)zone.LedCount, device.Leds.Length));
    }

    /// <summary>
    /// true si la zona admite cambiar de tamano.
    ///
    /// Solo lo hacen las cabeceras de tira direccionable. Las zonas soldadas a
    /// la placa tienen un numero fijo de LED, y ahi minimo y maximo coinciden.
    /// </summary>
    internal static bool IsResizable(Zone zone) => zone.LedsMax > zone.LedsMin;

    /// <summary>
    /// Declara cuantos LED tiene una tira conectada a una cabecera.
    ///
    /// Una tira direccionable no dice su longitud y el bus no permite
    /// averiguarla: pueden ser 10 LED o 60 y se ve igual. Hasta que no se
    /// declara, la cabecera figura con cero y no hay nada que encender.
    ///
    /// Devuelve null si fue bien, o el motivo del fallo.
    /// </summary>
    internal string? ResizeZone(int deviceIndex, int zoneIndex, int ledCount)
    {
        if (_client is null) return "Sin conexion con OpenRGB.";
        if (deviceIndex < 0 || deviceIndex >= _devices.Length) return "Dispositivo desconocido.";

        Zone? zone = _devices[deviceIndex].Zones.FirstOrDefault(z => z.Index == zoneIndex);
        if (zone is null) return "Zona desconocida.";
        if (!IsResizable(zone)) return $"'{zone.Name}' tiene un numero fijo de LED.";

        if (ledCount < zone.LedsMin || ledCount > zone.LedsMax)
            return $"'{zone.Name}' admite entre {zone.LedsMin} y {zone.LedsMax} LED.";

        try
        {
            lock (_gate)
            {
                _client.ResizeZone(deviceIndex, zoneIndex, ledCount);

                // El dispositivo cambia de tamano: hay que releer su
                // descripcion y tirar el estado guardado, porque el array de
                // colores anterior ya no cuadra con el numero de LED.
                Thread.Sleep(150);
                _devices = _client.GetAllControllerData();
                _state.Remove(deviceIndex);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Indice del primer LED de cada zona. Las zonas son consecutivas dentro
    /// del array del dispositivo, asi que basta con acumular sus tamanos.
    /// </summary>
    internal static Dictionary<int, int> ZoneOffsets(Device device)
    {
        var offsets = new Dictionary<int, int>();
        int running = 0;
        foreach (Zone z in device.Zones)
        {
            offsets[z.Index] = running;
            running += (int)z.LedCount;
        }
        return offsets;
    }

    /// <summary>Pinta una zona completa dentro de un lienzo.</summary>
    internal static void PaintZone(Color[] frame, Device device, int zoneIndex, Color color)
    {
        Zone? zone = device.Zones.FirstOrDefault(z => z.Index == zoneIndex);
        if (zone is null) return;

        int start = ZoneOffsets(device)[zoneIndex];
        for (int i = 0; i < zone.LedCount && start + i < frame.Length; i++)
            frame[start + i] = color;
    }

    public void Dispose() => _client?.Dispose();
}
