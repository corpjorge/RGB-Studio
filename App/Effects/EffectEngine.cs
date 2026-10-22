using OpenRGB.NET;
using RgbStudio.Rgb;
using System.Diagnostics;

namespace RgbStudio.Effects;

/// <summary>
/// Ejecuta efectos por software sobre zonas concretas.
///
/// Admite VARIOS a la vez, cada uno con su color y velocidad: una onda en la
/// placa mientras el teclado hace un arcoiris y la tira lateral late. Un unico
/// efecto global obligaria a elegir, que es justo lo que no queremos.
///
/// Corre en un hilo de fondo, no sobre la interfaz. Enviar 128 LEDs quince
/// veces por segundo desde el hilo de la ventana la dejaba congelada.
/// </summary>
internal sealed class EffectEngine
{
    // 15 cuadros por segundo: fluido y prudente con el IT8297, que ya demostro
    // atragantarse con rafagas de paquetes.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(66);

    /// <summary>Un efecto asignado a una zona. Zona -1 = el dispositivo entero.</summary>
    private sealed record Assignment(SoftwareEffect Effect, Color Color, double Speed);

    private readonly RgbService _rgb;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Dictionary<(int Device, int Zone), Assignment> _assignments = [];

    private CancellationTokenSource? _cancel;
    private Task? _loop;

    internal bool Running
    {
        get { lock (_gate) return _assignments.Count > 0; }
    }

    internal event EventHandler<string>? Failed;

    internal EffectEngine(RgbService rgb) => _rgb = rgb;

    /// <summary>Pone (o reemplaza) el efecto de una zona sin tocar los demas.</summary>
    internal void Assign(SoftwareEffect effect, IEnumerable<(int Device, int Zone)> targets,
                         Color color, double speed)
    {
        foreach ((int device, int zone) in targets)
        {
            _rgb.EnterDirectMode(device);
            lock (_gate) _assignments[(device, zone)] = new Assignment(effect, color, speed);
        }

        StopAudioCaptureIfUnused();
        EnsureRunning();
    }

    /// <summary>
    /// Quita los efectos que afectan a una zona.
    ///
    /// Tambien los del dispositivo entero cuando se pide una zona suelta: si
    /// hay un arcoiris en todo el teclado y pintas una tecla a mano, el efecto
    /// la sobrescribiria en el cuadro siguiente.
    /// </summary>
    internal void Clear(int device, int zone)
    {
        lock (_gate)
        {
            var doomed = _assignments.Keys
                .Where(k => k.Device == device && (zone < 0 || k.Zone < 0 || k.Zone == zone))
                .ToList();

            foreach (var key in doomed) _assignments.Remove(key);
        }

        StopAudioCaptureIfUnused();
        StopIfIdle();
    }

    internal void ClearAll()
    {
        lock (_gate) _assignments.Clear();
        StopAudioCaptureIfUnused();
        StopIfIdle();
    }

    /// <summary>
    /// Indica si un efecto de software afecta a la seleccion actual. No basta
    /// con saber que el motor esta activo: puede estar animando la placa o la
    /// RAM mientras el teclado se controla manualmente en modo Direct.
    /// </summary>
    internal bool Affects(int device, int zone)
    {
        lock (_gate)
            return _assignments.Keys.Any(
                key => key.Device == device
                    && (zone < 0 || key.Zone < 0 || key.Zone == zone));
    }

    /// <summary>Color y velocidad en caliente, sin reiniciar los efectos.</summary>
    internal void UpdateColor(Color color)
    {
        lock (_gate)
            foreach (var key in _assignments.Keys.ToList())
                _assignments[key] = _assignments[key] with { Color = color };
    }

    internal void UpdateSpeed(double speed)
    {
        lock (_gate)
            foreach (var key in _assignments.Keys.ToList())
                _assignments[key] = _assignments[key] with { Speed = speed };
    }

    /// <summary>Cuantos efectos hay corriendo.</summary>
    internal int Count { get { lock (_gate) return _assignments.Count; } }

    // --------------------------------------------------------------- bucle

    private void EnsureRunning()
    {
        if (_cancel is not null) return;

        _cancel = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cancel.Token));
    }

    private void StopIfIdle()
    {
        bool idle;
        lock (_gate) idle = _assignments.Count == 0;
        if (!idle) return;

        CancellationTokenSource? cancel = _cancel;
        _cancel = null;
        if (cancel is null) return;

        cancel.Cancel();

        // Se espera a que suelte el cerrojo del servicio, para que un color
        // aplicado justo despues no se cruce con el ultimo cuadro.
        try { _loop?.Wait(TimeSpan.FromMilliseconds(300)); } catch { /* cancelado */ }
        cancel.Dispose();
    }

    internal void Stop() => ClearAll();

    private void StopAudioCaptureIfUnused()
    {
        bool audioActive;
        lock (_gate)
            audioActive = _assignments.Values.Any(
                assignment => assignment.Effect is AudioReactiveEffect);

        if (!audioActive) AudioLevelMonitor.Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
                RenderFrame();
        }
        catch (OperationCanceledException)
        {
            // Parada normal.
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
        }
    }

    private void RenderFrame()
    {
        KeyValuePair<(int Device, int Zone), Assignment>[] work;
        lock (_gate) work = [.. _assignments];
        if (work.Length == 0) return;

        double elapsed = _clock.Elapsed.TotalSeconds;

        foreach (var group in work.GroupBy(a => a.Key.Device))
        {
            int device = group.Key;

            // El del dispositivo entero primero: asi un efecto de zona puesto
            // encima gana, en vez de quedar tapado.
            var ordered = group.OrderByDescending(a => a.Key.Zone < 0).ToArray();

            _rgb.PaintAndSend(device, frame =>
            {
                foreach (var (key, item) in ordered)
                {
                    (int start, int end) = _rgb.ZoneRange(device, key.Zone);
                    int last = Math.Max(1, end - start - 1);
                    var grid = MatrixCoords(device, key.Zone);
                    double seconds = elapsed * item.Speed;

                    for (int i = start; i < end && i < frame.Length; i++)
                    {
                        // Posicion dentro de la zona, no del dispositivo: asi el
                        // efecto se ve entero aunque la zona sea pequena.
                        double position = (double)(i - start) / last;

                        // Sin matriz el LED se coloca sobre una linea, y los
                        // efectos espaciales degeneran a su version lineal.
                        bool hasMatrix = grid.TryGetValue(i, out var c);
                        (double x, double y) = hasMatrix ? c : (position, 0.5);

                        frame[i] = item.Effect.Sample(
                            seconds,
                            new LedContext(position, x, y, i, hasMatrix),
                            item.Color);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Posicion normalizada de cada LED en la rejilla del dispositivo. Vacio si
    /// la zona no es de tipo Matrix, que es lo habitual fuera de los teclados.
    /// </summary>
    private Dictionary<int, (double X, double Y)> MatrixCoords(int deviceIndex, int zoneIndex)
    {
        var coords = new Dictionary<int, (double, double)>();
        Device device = _rgb.Devices[deviceIndex];

        IEnumerable<Zone> zones = zoneIndex < 0
            ? device.Zones
            : device.Zones.Where(z => z.Index == zoneIndex);

        foreach (Zone zone in zones)
        {
            if (zone.MatrixMap is not { } map) continue;

            int start = RgbService.ZoneOffsets(device)[zone.Index];
            double lastCol = Math.Max(1, map.Width - 1);
            double lastRow = Math.Max(1, map.Height - 1);

            for (uint row = 0; row < map.Height; row++)
            {
                for (uint col = 0; col < map.Width; col++)
                {
                    uint value = map.Matrix[row, col];
                    if (value == uint.MaxValue) continue;

                    int led = start + (int)value;
                    if (led < device.Leds.Length)
                        coords[led] = (col / lastCol, row / lastRow);
                }
            }
        }

        return coords;
    }
}
