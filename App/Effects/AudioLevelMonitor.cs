using NAudio.Wave;

namespace RgbStudio.Effects;

/// <summary>
/// Mide el audio que sale por el dispositivo predeterminado de Windows.
/// Solo conserva un nivel numerico suavizado; no graba ni almacena audio.
/// </summary>
internal static class AudioLevelMonitor
{
    private static readonly object Gate = new();
    private static WasapiLoopbackCapture? _capture;
    private static float _level;
    private static float _beat;
    private static float _average;

    internal static double Level => Math.Clamp(Volatile.Read(ref _level), 0, 1);
    internal static double Beat => Math.Clamp(Volatile.Read(ref _beat), 0, 1);

    internal static void Start()
    {
        lock (Gate)
        {
            if (_capture is not null) return;

            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            try
            {
                capture.StartRecording();
                _capture = capture;
            }
            catch
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                throw;
            }
        }
    }

    internal static void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (Gate)
        {
            capture = _capture;
            _capture = null;
            Volatile.Write(ref _level, 0);
            Volatile.Write(ref _beat, 0);
            Volatile.Write(ref _average, 0);
        }

        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        try { capture.StopRecording(); }
        catch { /* el dispositivo de audio puede haber desaparecido */ }
        capture.Dispose();
    }

    private static void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded == 0) return;

        double sum = 0;
        int count = 0;
        WaveFormat format = capture.WaveFormat;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int offset = 0; offset + 4 <= e.BytesRecorded; offset += 4)
            {
                float sample = BitConverter.ToSingle(e.Buffer, offset);
                if (!float.IsFinite(sample)) continue;
                sum += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int offset = 0; offset + 2 <= e.BytesRecorded; offset += 2)
            {
                double sample = BitConverter.ToInt16(e.Buffer, offset) / 32768.0;
                sum += sample * sample;
                count++;
            }
        }

        if (count == 0) return;

        // La musica normal suele tener un RMS bajo. Esta curva la hace visible
        // sin llevar cualquier sonido pequeno inmediatamente al maximo.
        double rms = Math.Sqrt(sum / count);
        float measured = (float)Math.Pow(Math.Clamp(rms * 4.2, 0, 1), 0.72);
        float previous = Volatile.Read(ref _level);
        float smoothing = measured > previous ? 0.72f : 0.16f;
        Volatile.Write(ref _level, previous + (measured - previous) * smoothing);

        // El promedio lento representa el volumen habitual de la cancion. Un
        // golpe se obtiene de la energia que aparece de repente por encima de
        // ese fondo; asi una parte fuerte sostenida no mantiene todo encendido.
        float average = Volatile.Read(ref _average);
        average += (measured - average) * 0.055f;
        Volatile.Write(ref _average, average);

        float onset = Math.Clamp((measured - average * 1.10f - 0.025f) * 4.8f, 0, 1);
        float previousBeat = Volatile.Read(ref _beat);
        float beatSmoothing = onset > previousBeat ? 0.88f : 0.30f;
        float beat = previousBeat + (onset - previousBeat) * beatSmoothing;
        if (rms < 0.006) beat = 0;
        Volatile.Write(ref _beat, beat);
    }

    private static void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_capture, sender)) _capture = null;
            Volatile.Write(ref _level, 0);
            Volatile.Write(ref _beat, 0);
            Volatile.Write(ref _average, 0);
        }
    }
}
