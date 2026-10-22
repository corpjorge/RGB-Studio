using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RgbStudio.Effects;

/// <summary>
/// Seguimiento minimo de pulsaciones para el efecto reactivo.
///
/// No guarda caracteres ni secuencias: cada pulsacion se convierte de
/// inmediato en una coordenada aproximada del teclado y desaparece en 1.4 s.
/// El hook se instala en el hilo de WPF, que ya dispone de bucle de mensajes.
/// </summary>
internal static class KeyboardPulseTracker
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const double Lifetime = 1.4;

    private sealed record Pulse(double X, double Y, long Timestamp);

    private static readonly object Gate = new();
    private static readonly List<Pulse> Pulses = [];
    private static readonly HookProc Callback = OnKeyboard;
    private static readonly Dictionary<int, (double X, double Y)> Coordinates = BuildCoordinates();
    private static IntPtr _hook;

    internal static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = GetModuleHandle(module?.ModuleName);
        _hook = SetWindowsHookEx(WhKeyboardLl, Callback, moduleHandle, 0);
    }

    internal static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        lock (Gate) Pulses.Clear();
    }

    internal static double Intensity(double x, double y)
    {
        long now = Stopwatch.GetTimestamp();
        double strongest = 0;

        lock (Gate)
        {
            Pulses.RemoveAll(p => Stopwatch.GetElapsedTime(p.Timestamp, now).TotalSeconds > Lifetime);

            foreach (Pulse pulse in Pulses)
            {
                double age = Stopwatch.GetElapsedTime(pulse.Timestamp, now).TotalSeconds;
                double dx = x - pulse.X;
                double dy = y - pulse.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                double keyGlow = Math.Exp(-distance * distance / 0.0028) * Math.Exp(-age * 3.1);
                double radius = age * 0.42;
                double halo = Math.Exp(-Math.Pow((distance - radius) / 0.055, 2))
                              * Math.Exp(-age * 2.4) * 0.55;
                strongest = Math.Max(strongest, keyGlow + halo);
            }
        }

        return Math.Clamp(strongest, 0, 1);
    }

    private static IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == (IntPtr)WmKeyDown || message == (IntPtr)WmSysKeyDown))
        {
            int virtualKey = Marshal.ReadInt32(data);
            if (Coordinates.TryGetValue(virtualKey, out var point))
            {
                lock (Gate)
                {
                    Pulses.Add(new Pulse(point.X, point.Y, Stopwatch.GetTimestamp()));
                    if (Pulses.Count > 24) Pulses.RemoveAt(0);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static Dictionary<int, (double X, double Y)> BuildCoordinates()
    {
        var result = new Dictionary<int, (double, double)>();

        static void Row(Dictionary<int, (double, double)> map, double y, params int[] keys)
        {
            double last = Math.Max(1, keys.Length - 1);
            for (int i = 0; i < keys.Length; i++) map[keys[i]] = (i / last, y);
        }

        Row(result, 0.00, 0x1B, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
            0x78, 0x79, 0x7A, 0x7B, 0x2C, 0x91, 0x13);
        Row(result, 0.20, 0xC0, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
            0x39, 0x30, 0xBD, 0xBB, 0x08, 0x2D, 0x24, 0x21, 0x90, 0x6F, 0x6A, 0x6D);
        Row(result, 0.40, 0x09, 0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49,
            0x4F, 0x50, 0xDB, 0xDD, 0xDC, 0x2E, 0x23, 0x22, 0x67, 0x68, 0x69, 0x6B);
        Row(result, 0.60, 0x14, 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B,
            0x4C, 0xBA, 0xDE, 0x0D, 0x64, 0x65, 0x66);
        Row(result, 0.80, 0xA0, 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC,
            0xBE, 0xBF, 0xA1, 0x26, 0x61, 0x62, 0x63);
        Row(result, 1.00, 0xA2, 0x5B, 0xA4, 0x20, 0xA5, 0x5C, 0xA3, 0x25, 0x28,
            0x27, 0x60, 0x6E);

        return result;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
