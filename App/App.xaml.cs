using System.Windows;

namespace RgbStudio;

public partial class App : Application
{
    private const string MutexName = @"Local\RgbStudio.SingleInstance";
    private const string ActivationEventName = @"Local\RgbStudio.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--verify-installation")
        {
            // Diagnóstico sin ventana, red ni hardware para verificar el
            // instalador en un entorno limpio de GitHub Actions.
            var result = new
            {
                Version = RgbStudio.MainWindow.ApplicationVersion.ToString(),
                SelfContained = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")),
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                RuntimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "rgbstudio-installed.marker")),
            };
            File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(result));
            Shutdown();
            return;
        }

        // El evento existe en ambos procesos. La segunda instancia solo lo
        // activa y termina; la primera lo recibe y trae su ventana al frente.
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _instanceMutex = new Mutex(true, MutexName, out _ownsMutex);

        if (!_ownsMutex)
        {
            _activationEvent.Set();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    internal void ShowMainWindow()
    {
        if (MainWindow is MainWindow window)
            window.RestoreFromTray();
    }

    internal void ExitApplication(bool installUpdate = true)
    {
        if (MainWindow is MainWindow window)
        {
            if (installUpdate) window.QueuePreparedAppUpdate();
            window.PrepareForExit();
        }

        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window)
            window.PrepareForExit();

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();

        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
