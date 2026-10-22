using OpenRGB.NET;
using RgbStudio.Effects;
using RgbStudio.Rendering;
using RgbStudio.Rgb;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;
using RgbColor = OpenRGB.NET.Color;

namespace RgbStudio;

public partial class MainWindow : Window
{
    private readonly RgbService _rgb = new();

    private int _deviceIndex = -1;
    private int _zoneIndex = -1;               // -1 = todo el dispositivo
    private int _ledIndex = -1;                // -1 = la zona entera
    private Dictionary<Pick, Border> _shapes = [];

    // La rueda de color dispara cambios continuamente al arrastrar. Enviar cada
    // uno saturaria al IT8297, que ya demostro llevarse mal con rafagas de
    // paquetes. Este temporizador los agrupa: como mucho un envio cada 100 ms.
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _pendingLive;
    private bool _loadingDevice;

    // Inicializador de campo, NO en el constructor: los eventos del XAML
    // (IsChecked="True" en una casilla, por ejemplo) se disparan durante
    // InitializeComponent(), antes de que corra el cuerpo del constructor. Si
    // el motor se creara alli, esos manejadores lo encontrarian nulo y la
    // ventana reventaria antes de mostrarse. Los inicializadores de campo si
    // corren antes, en orden de declaracion, y _rgb esta declarado arriba.
    private readonly EffectEngine _engine;

    private readonly AppWatcher _apps = new();
    private readonly TrayIcon _trayIcon;
    private bool _allowClose;
    private bool _trayHintShown;

    private static readonly SoftwareEffect[] SoftwareEffects =
    [
        new WaveEffect(),
        new BreatheEffect(),
        new RainbowEffect(),
        new GradientEffect(),
        new CometEffect(),
        new HeartbeatEffect(),
        new FireEffect(),
        new ColorCycleEffect(),
        new AuroraEffect(),
        new StarsEffect(),
        new ScannerEffect(),
        new BandsEffect(),
        new PlasmaEffect(),
        new ColombiaEffect(),
        new PrincessEffect(),
        new PoliceEffect(),
        new DoubleBreatheEffect(),
        new ContinuousRippleEffect(),
        new ReactiveKeysEffect(),
        new AudioReactiveEffect(),
        new RainEffect(),
        new RippleEffect(),
        new DiagonalEffect(),
    ];

    public MainWindow()
    {
        _engine = new EffectEngine(_rgb);
        _trayIcon = new TrayIcon();

        InitializeComponent();
        InitializeOpenRgbUpdates();

        // Ocupa todo el alto util si hace falta, pero nunca queda escondida
        // detras de la barra de tareas en pantallas mas pequenas.
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(1120, MaxHeight);

        _trayIcon.OpenRequested += RestoreFromTray;
        _trayIcon.ExitRequested += () => ((App)System.Windows.Application.Current).ExitApplication();

        _engine.Failed += (_, msg) => Dispatcher.Invoke(
            () => Status.Text = $"Efecto detenido — {msg}");

        Picker.ColorChanged += (_, _) => RequestLiveApply();
        _liveTimer.Tick += (_, _) =>
        {
            if (_pendingLive)
            {
                _pendingLive = false;
                ApplyColor(live: true);
            }

            if (_engine.Running) RefreshShapeColors();
        };
        _liveTimer.Start();

        _apps.Changed += OnForegroundAppChanged;

        Loaded += (_, _) => Connect();
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            _liveTimer.Stop();
            _apps.Stop();
            _engine.Stop();
            KeyboardPulseTracker.Stop();
            AudioLevelMonitor.Stop();
            _rgb.Dispose();
            _trayIcon.Dispose();
        };
    }

    // ------------------------------------------------------- barra de titulo

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";

        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();

        if (_trayHintShown) return;
        _trayHintShown = true;
        _trayIcon.ShowBackgroundHint();
    }

    internal void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Activate no siempre puede tomar el foco desde otro proceso. Este
        // pulso de Topmost hace visible la ventana sin dejarla fijada encima.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void PrepareForExit() => _allowClose = true;

    // --------------------------------------------------- seguir la app activa

    private void OnFollowAppToggled(object sender, RoutedEventArgs e)
    {
        // El XAML dispara Checked/Unchecked durante InitializeComponent, antes
        // de que exista media ventana. Ya nos costo dos cuelgues.
        if (!IsLoaded) return;

        if (FollowApp.IsChecked == true)
        {
            _apps.Start();
            Status.Text = "Siguiendo la aplicacion en primer plano.";
        }
        else
        {
            _apps.Stop();
            FollowAppInfo.Visibility = Visibility.Collapsed;
            Status.Text = "Color manual.";
        }
    }

    private void OnForegroundAppChanged(object? sender, AppWatcher.AppColor app)
    {
        FollowAppInfo.Visibility = Visibility.Visible;
        FollowAppName.Text = app.Name;
        FollowAppSwatch.Background = new SolidColorBrush(app.Color);

        // notify:false a proposito. El camino normal del selector hace un envio
        // unico sin repetir, que es lo correcto mientras se arrastra el raton
        // porque enseguida viene otro cuadro. Aqui no: cambiar de aplicacion es
        // un evento suelto, y el IT8297 descarta envios sueltos de vez en
        // cuando. Por eso el color fijo no llegaba a la placa mientras que los
        // efectos si, que reenvian quince veces por segundo.
        Picker.SetColor(app.Color, notify: false);

        var color = new RgbColor(app.Color.R, app.Color.G, app.Color.B);

        if (_engine.Running) _engine.UpdateColor(color);
        else PaintEverything(color);
    }

    // Ultimo color pendiente y si hay un pintado en marcha. Cambiar de ventana
    // puede ser mas rapido que pintar cuatro dispositivos con sus pausas, y sin
    // esto se encolarian colores ya caducados.
    private readonly object _followGate = new();
    private RgbColor? _followPending;
    private bool _followBusy;

    /// <summary>
    /// Pinta un color en todos los dispositivos, con el envio fiable.
    ///
    /// Todos y no solo el seleccionado: seguir a la aplicacion es algo del
    /// equipo entero, igual que los efectos, que tambien van a todos.
    /// </summary>
    private void PaintEverything(RgbColor color)
    {
        if (!_rgb.Connected) return;

        lock (_followGate)
        {
            _followPending = color;
            if (_followBusy) return;      // el pintado en curso lo recogera
            _followBusy = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                RgbColor next;
                lock (_followGate)
                {
                    if (_followPending is null) { _followBusy = false; return; }
                    next = _followPending.Value;
                    _followPending = null;
                }

                for (int d = 0; d < _rgb.Devices.Count; d++)
                {
                    if (!_rgb.EnterDirectMode(d)) continue;
                    _rgb.PaintRange(d, 0, _rgb.Devices[d].Leds.Length, next, live: false);
                }

                Dispatcher.Invoke(() =>
                {
                    Status.Text = $"#{next.R:X2}{next.G:X2}{next.B:X2} — color de {FollowAppName.Text}";
                    RefreshShapeColors();
                });
            }
        });
    }

    private void RequestLiveApply()
    {
        if (_loadingDevice) return;

        // El color solo va al motor si un efecto de software afecta realmente
        // a esta seleccion. Puede haber efectos en otros dispositivos mientras
        // el teclado esta en Direct; en ese caso hay que aplicar el color fijo.
        if (_deviceIndex >= 0
            && Selected?.IsSoftware == true
            && _engine.Affects(_deviceIndex, _zoneIndex))
        {
            WpfColor c = Picker.SelectedColor;
            _engine.UpdateColor(new RgbColor(c.R, c.G, c.B));
            return;
        }

        if (LiveApply.IsChecked != true) return;
        _pendingLive = true;
    }

    // ------------------------------------------------------- efectos propios

    private void StartSoftwareEffect()
    {
        EffectItem? item = Selected;
        if (item is null || !item.IsSoftware) return;
        if (!_rgb.Connected) { Status.Text = "Sin conexion con OpenRGB."; return; }

        // 0 = solo la parte seleccionada, 1 = el dispositivo, 2 = todos.
        int scope = EffectScope.SelectedIndex;
        (int Device, int Zone)[] targets = scope switch
        {
            0 when _deviceIndex >= 0 => [(_deviceIndex, _zoneIndex)],
            1 when _deviceIndex >= 0 => [(_deviceIndex, -1)],
            2 => Enumerable.Range(0, _rgb.Devices.Count).Select(d => (d, -1)).ToArray(),
            _ => [],
        };

        if (targets.Length == 0) { Status.Text = "Selecciona un dispositivo."; return; }

        WpfColor color = Picker.SelectedColor;
        var rgbColor = new RgbColor(color.R, color.G, color.B);

        string where = scope switch
        {
            0 => SelectionLabel.Text,
            1 => _rgb.Devices[_deviceIndex].Name,
            _ => $"{targets.Length} dispositivos",
        };

        if (item.IsSolid)
        {
            ApplySolidColor(targets, rgbColor, scope, where);
            return;
        }

        SoftwareEffect fx = SoftwareEffects[item.Index];
        if (fx is ReactiveKeysEffect) KeyboardPulseTracker.Start();
        if (fx is AudioReactiveEffect)
        {
            try
            {
                AudioLevelMonitor.Start();
            }
            catch (Exception ex)
            {
                Status.Text = $"No se pudo capturar el audio — {ex.Message}";
                return;
            }
        }
        double speed = EffectSpeed.Value;

        // Assign entra en modo directo en cada dispositivo, y eso lleva pausas:
        // fuera del hilo de la ventana.
        Task.Run(() =>
        {
            _engine.Assign(fx, targets, rgbColor, speed);
            Dispatcher.Invoke(() => Status.Text += $"  ({_engine.Count} efectos activos)");
        });

        Status.Text = $"'{fx.Name}' en {where}.";
    }

    /// <summary>
    /// Aplica un color fijo una sola vez. No se registra en EffectEngine
    /// porque un color constante no necesita un bucle de quince cuadros por
    /// segundo: el propio hardware conserva el ultimo estado recibido.
    /// </summary>
    private void ApplySolidColor((int Device, int Zone)[] targets, RgbColor color,
                                 int scope, string where)
    {
        int selectedDevice = _deviceIndex;
        int selectedLed = _ledIndex;
        Status.Text = $"Aplicando color solido en {where}...";

        Task.Run(() =>
        {
            foreach ((int device, int zone) in targets)
            {
                _engine.Clear(device, zone);
                if (!_rgb.EnterDirectMode(device)) continue;

                (int start, int end) = scope == 0
                    && device == selectedDevice
                    && selectedLed >= 0
                        ? (selectedLed, selectedLed + 1)
                        : _rgb.ZoneRange(device, zone);

                _rgb.PaintRange(device, start, end, color, live: false);
            }

            Dispatcher.Invoke(() =>
            {
                Status.Text = $"Color solido #{color.R:X2}{color.G:X2}{color.B:X2} en {where}.";
                RefreshShapeColors();
            });
        });
    }

    private void OnEffectSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => _engine?.UpdateSpeed(EffectSpeed.Value);

    // ------------------------------------------------------------- conexion

    private void Connect()
    {
        string? error = _rgb.Connect();
        if (error is not null)
        {
            Status.Text = $"Sin conexion — {error}. Abre OpenRGB y arranca su servidor SDK.";
            return;
        }

        // Antes de listar nada: OpenRGB olvida la longitud de las tiras entre
        // arranques, y si no se redeclara aqui el mapa saldria sin ellas.
        StripSizes.Restore(_rgb);

        Status.Text = $"Conectado — {_rgb.Devices.Count} dispositivos";
        FillDeviceList(0);
    }

    /// <summary>Rellena la lista de dispositivos y selecciona uno, lo que redibuja todo.</summary>
    private void FillDeviceList(int select)
    {
        DeviceList.Items.Clear();
        for (int i = 0; i < _rgb.Devices.Count; i++)
        {
            Device d = _rgb.Devices[i];
            DeviceList.Items.Add($"{DeviceDisplayName(i)}   ({d.Leds.Length} LEDs)");
        }

        if (DeviceList.Items.Count > 0)
            DeviceList.SelectedIndex = Math.Clamp(select, 0, DeviceList.Items.Count - 1);
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => Connect();

    // ------------------------------------------------- tiras direccionables

    /// <summary>
    /// Declara cuantos LED tiene la tira de una cabecera.
    ///
    /// Lo llama el propio mapa: la casilla vive junto a la cabecera dibujada,
    /// no en el panel de la derecha, porque describe que hay conectado a la
    /// placa y no como pintarlo.
    /// </summary>
    private void SetStripLength(int zoneIndex, int count)
    {
        if (_deviceIndex < 0) return;

        int deviceIndex = _deviceIndex;
        string deviceName = _rgb.Devices[deviceIndex].Name;
        Status.Text = "Ajustando la tira...";

        Task.Run(() =>
        {
            // El dispositivo cambia de tamano: un efecto en marcha estaria
            // pintando sobre un mapa de LEDs que ya no vale.
            _engine.Clear(deviceIndex, -1);

            string? error = _rgb.ResizeZone(deviceIndex, zoneIndex, count);

            Dispatcher.Invoke(() =>
            {
                if (error is not null) { Status.Text = error; return; }

                StripSizes.Remember(deviceName, zoneIndex, count);

                // Rehacer la lista: cambia el numero de LED del dispositivo, y
                // volver a seleccionarlo redibuja el mapa y este mismo panel.
                FillDeviceList(deviceIndex);

                Status.Text = count > 0
                    ? $"Tira de {count} LED lista. Ya aparece en el mapa."
                    : "Cabecera marcada como vacia.";
            });
        });
    }

    // -------------------------------------------------------------- seleccion

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        _deviceIndex = DeviceList.SelectedIndex;
        if (_deviceIndex < 0 || _deviceIndex >= _rgb.Devices.Count) return;

        Device device = _rgb.Devices[_deviceIndex];
        string displayName = DeviceDisplayName(_deviceIndex);
        MapTitle.Text = displayName.ToUpperInvariant();

        // Bandera para que rellenar la lista no dispare un envio.
        _loadingDevice = true;
        ModeBox.Items.Clear();

        // Los del firmware primero, los propios despues y marcados: van a
        // sitios distintos (el chip / nuestro motor) pero para quien usa la
        // aplicacion son lo mismo, asi que comparten selector.
        foreach (Mode m in device.Modes)
            ModeBox.Items.Add(new EffectItem(m.Name, m.Name, IsSoftware: false, -1));

        // Los espaciales necesitan matriz; se marcan para que se vea que en un
        // dispositivo sin rejilla se veran como su version lineal.
        bool hasMatrix = device.Zones.Any(z => z.MatrixMap is not null);
        var customItems = new List<EffectItem>
        {
            new("◆ Color sólido", "Color sólido", IsSoftware: true, -1, IsSolid: true),
        };

        for (int i = 0; i < SoftwareEffects.Length; i++)
        {
            SoftwareEffect fx = SoftwareEffects[i];
            string suffix = fx.NeedsMatrix && !hasMatrix ? "  (mejor en teclado)" : string.Empty;
            customItems.Add(new EffectItem($"◆ {fx.Name}{suffix}", fx.Name, IsSoftware: true, i));
        }

        foreach (EffectItem custom in customItems.OrderBy(
                     item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            ModeBox.Items.Add(custom);

        ModeBox.SelectedIndex = Array.FindIndex(device.Modes,
            m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase));
        _loadingDevice = false;

        UpdateEffectPanels();
        SelectAllZones();
        DrawDevice(device, displayName);
        LoadCalibration(device.Name);
    }

    /// <summary>Entrada del selector de efectos: del firmware o calculado por nosotros.</summary>
    private sealed record EffectItem(
        string Label, string Name, bool IsSoftware, int Index, bool IsSolid = false)
    {
        public override string ToString() => Label;
    }

    private EffectItem? Selected => ModeBox.SelectedItem as EffectItem;

    private void UpdateEffectPanels()
    {
        bool software = Selected?.IsSoftware == true;
        bool solid = Selected?.IsSolid == true;
        bool usesSpeed = software && !solid
                         && Selected is { Index: >= 0 } selected
                         && SoftwareEffects[selected.Index].UsesSpeed;
        SoftwarePanel.Visibility = software ? Visibility.Visible : Visibility.Collapsed;
        SpeedLabel.Visibility = usesSpeed ? Visibility.Visible : Visibility.Collapsed;
        EffectSpeed.Visibility = usesSpeed ? Visibility.Visible : Visibility.Collapsed;
        EffectHelp.ToolTip = solid
            ? "Aplica el color una sola vez al alcance elegido."
            : software
            ? "Calculado por la aplicacion. Usa el color y la velocidad de abajo."
            : "Lo ejecuta el propio dispositivo.";
    }

    /// <summary>Cambiar de efecto se aplica solo, sin pasar por el boton.</summary>
    private void OnModeSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateEffectPanels();
        if (_loadingDevice || _deviceIndex < 0) return;
        if (LiveApply.IsChecked != true) return;

        ApplyColor(live: false);
    }

    private void OnEffectScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevice || !IsLoaded) return;
        if (_engine.Running) StartSoftwareEffect();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        SelectAllZones();
        HighlightSelection();
    }

    private void SelectAllZones()
    {
        _zoneIndex = -1;
        _ledIndex = -1;
        SelectionLabel.Text = "Todo el dispositivo";
        SelectionHelp.ToolTip = "El color se aplicara a todas las zonas.";

        if (_deviceIndex >= 0 && _deviceIndex < _rgb.Devices.Count)
            LoadCalibration(_rgb.Devices[_deviceIndex].Name);
    }

    // ------------------------------------------------------------------ mapa

    private void DrawDevice(Device device, string displayName)
    {
        _shapes = DeviceRenderer.Draw(
            BoardCanvas, device, displayName, OnShapeClicked, SetStripLength);
        RefreshShapeColors();
    }

    /// <summary>
    /// OpenRGB publica ambos modulos como "ENE DRAM". En este equipo el orden
    /// es estable: primero D80 y despues D41, por lo que la interfaz los separa
    /// sin cambiar el identificador usado para hablar con el hardware.
    /// </summary>
    private string DeviceDisplayName(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= _rgb.Devices.Count) return string.Empty;

        Device device = _rgb.Devices[deviceIndex];
        if (device.Type != DeviceType.Dram
            || !device.Name.Equals("ENE DRAM", StringComparison.OrdinalIgnoreCase))
            return device.Name;

        int dramNumber = 0;
        for (int i = 0; i <= deviceIndex; i++)
        {
            Device candidate = _rgb.Devices[i];
            if (candidate.Type == DeviceType.Dram
                && candidate.Name.Equals("ENE DRAM", StringComparison.OrdinalIgnoreCase))
                dramNumber++;
        }

        return dramNumber switch
        {
            1 => "XPG SPECTRIX D80",
            2 => "XPG SPECTRIX D41",
            _ => $"XPG SPECTRIX RAM {dramNumber}",
        };
    }

    private void OnShapeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Pick pick }) return;
        if (_deviceIndex < 0) return;

        Device device = _rgb.Devices[_deviceIndex];
        Zone? zone = device.Zones.FirstOrDefault(z => z.Index == pick.ZoneIndex);
        if (zone is null) return;

        _zoneIndex = pick.ZoneIndex;
        _ledIndex = pick.LedIndex;

        if (_ledIndex >= 0 && _ledIndex < device.Leds.Length)
        {
            SelectionLabel.Text = device.Leds[_ledIndex].Name;
            SelectionHelp.ToolTip = $"LED individual dentro de '{zone.Name}'.";
        }
        else
        {
            var catalog = ZoneCatalog.For(device.Name);
            SelectionLabel.Text = catalog.TryGetValue(_zoneIndex, out ZoneCatalog.ZoneInfo? info)
                ? info.Name
                : zone.Name;
            SelectionHelp.ToolTip = info?.Description ?? $"{zone.LedCount} LED, tipo {zone.Type}";
        }

        HighlightSelection();
        LoadCalibration(device.Name);

        // Pulsar algo lo pinta al momento con el color actual.
        if (LiveApply.IsChecked == true) ApplyColor(live: false);
    }

    private void HighlightSelection()
        => RefreshShapeColors();

    /// <summary>
    /// Pinta el mapa con el estado logico, antes de la calibracion fisica de
    /// cada dispositivo. La placa y las RAM usan mascaras sobre sus fotografias;
    /// el teclado superpone cada LED sobre la tecla o barra correspondiente.
    /// </summary>
    private void RefreshShapeColors()
    {
        if (_deviceIndex < 0 || _deviceIndex >= _rgb.Devices.Count) return;

        Device device = _rgb.Devices[_deviceIndex];
        RgbColor[] colors = _rgb.Snapshot(_deviceIndex);
        Dictionary<int, int> offsets = RgbService.ZoneOffsets(device);
        bool motherboard = device.Type == DeviceType.Motherboard && ZoneCatalog.For(device.Name).Count > 0;
        bool maskedPhoto = motherboard || device.Type == DeviceType.Dram;
        bool keyboardPhoto = device.Type == DeviceType.Keyboard;
        bool selectingEverything = _zoneIndex == -1;

        foreach ((Pick pick, Border shape) in _shapes)
        {
            bool selected = _zoneIndex == -1
                         || (pick.ZoneIndex == _zoneIndex && (_ledIndex < 0 || pick.LedIndex == _ledIndex));

            RgbColor logical = ShapeColor(pick, device, colors, offsets);
            WpfColor color = WpfColor.FromRgb(logical.R, logical.G, logical.B);
            bool dark = logical.R + logical.G + logical.B < 30;
            WpfColor outline = dark ? WpfColor.FromRgb(0x8A, 0x8A, 0x96) : color;

            byte fillAlpha = maskedPhoto
                ? (byte)255
                : keyboardPhoto
                    ? (byte)(selected ? 112 : 72)
                    : (byte)(selected ? 210 : 135);
            shape.Background = new SolidColorBrush(WpfColor.FromArgb(
                dark && maskedPhoto ? (byte)0 : fillAlpha, color.R, color.G, color.B));
            shape.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(
                keyboardPhoto && selectingEverything
                    ? (byte)125
                    : selected ? (byte)235 : (byte)80,
                outline.R, outline.G, outline.B));
            shape.BorderThickness = new Thickness(
                keyboardPhoto && selectingEverything ? 1 : selected ? 3 : 1.25);
            shape.Opacity = maskedPhoto ? (selected ? 1.0 : 0.78) : (selected ? 1.0 : 0.68);
            shape.Effect = selected && !dark && !(keyboardPhoto && selectingEverything)
                ? new DropShadowEffect
                {
                    Color = color,
                    BlurRadius = maskedPhoto ? 22 : 10,
                    ShadowDepth = 0,
                    Opacity = 0.9,
                }
                : null;
        }
    }

    private static RgbColor ShapeColor(Pick pick, Device device, RgbColor[] colors,
                                       Dictionary<int, int> offsets)
    {
        if (pick.LedIndex >= 0 && pick.LedIndex < colors.Length)
            return colors[pick.LedIndex];

        Zone? zone = device.Zones.FirstOrDefault(z => z.Index == pick.ZoneIndex);
        if (zone is null || !offsets.TryGetValue(zone.Index, out int start))
            return new RgbColor(0, 0, 0);

        int end = Math.Min(start + (int)zone.LedCount, colors.Length);
        if (start >= end) return new RgbColor(0, 0, 0);

        int red = 0, green = 0, blue = 0;
        for (int i = start; i < end; i++)
        {
            red += colors[i].R;
            green += colors[i].G;
            blue += colors[i].B;
        }

        int count = end - start;
        return new RgbColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    // --------------------------------------------------------------- aplicar

    private WpfColor CurrentColor() => Picker.SelectedColor;

    // ---------------------------------------------------------- calibracion

    private void LoadCalibration(string deviceName)
    {
        int? zoneIndex = CalibrationZoneIndex(deviceName);
        ColorCalibration.Profile profile = zoneIndex is int zone
            ? ColorCalibration.Get(deviceName, zone)
            : ColorCalibration.Get(deviceName);

        string target = zoneIndex is int selectedZone
            ? ZoneCatalog.Describe(deviceName, selectedZone, $"Zona {selectedZone}")
            : "el dispositivo seleccionado";

        CalibrationButton.Content = zoneIndex is null
            ? "Calibrar color del dispositivo..."
            : "Calibrar color de esta zona...";
        CalibrationHelp.ToolTip = profile.IsIdentity
            ? $"Perfil neutro para {target}."
            : $"{target}: R {profile.RedGain:P0} · G {profile.GreenGain:P0} · B {profile.BlueGain:P0} · " +
              $"γ {profile.RedGamma:0.##}/{profile.GreenGamma:0.##}/{profile.BlueGamma:0.##}";
    }

    private int? CalibrationZoneIndex(string deviceName)
        => _zoneIndex >= 0 && ZoneCatalog.For(deviceName).ContainsKey(_zoneIndex)
            ? _zoneIndex
            : null;

    private void OnOpenCalibration(object sender, RoutedEventArgs e)
    {
        if (_deviceIndex < 0) return;

        string deviceName = _rgb.Devices[_deviceIndex].Name;
        int? zoneIndex = CalibrationZoneIndex(deviceName);
        string targetName = zoneIndex is int selectedZone
            ? $"{deviceName} · {ZoneCatalog.Describe(deviceName, selectedZone, $"Zona {selectedZone}")}"
            : deviceName;
        var dialog = new CalibrationWindow(
            targetName,
            zoneIndex is int zone
                ? ColorCalibration.Get(deviceName, zone)
                : ColorCalibration.Get(deviceName),
            zoneIndex is int initialZone
                ? ColorCalibration.Initial(deviceName, initialZone)
                : ColorCalibration.Initial(deviceName))
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        if (zoneIndex is int calibratedZone)
            ColorCalibration.Remember(deviceName, calibratedZone, dialog.Profile);
        else
            ColorCalibration.Remember(deviceName, dialog.Profile);

        LoadCalibration(deviceName);
        PreviewCalibration(targetName);
    }

    private void PreviewCalibration(string deviceName)
    {
        WpfColor color = Picker.SelectedColor;
        var rgbColor = new RgbColor(color.R, color.G, color.B);

        if (FollowApp.IsChecked == true && !_engine.Running)
            PaintEverything(rgbColor);
        else if (LiveApply.IsChecked == true)
            ApplyColor(live: false);

        Status.Text = $"Calibracion activa para {deviceName}.";
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyColor(live: false);

    /// <summary>
    /// Todo el trabajo con el hardware se hace en segundo plano.
    ///
    /// Las llamadas al SDK tardan lo que tarde el USB, y algunas llevan pausas
    /// deliberadas. En el hilo de la ventana eso se traduce en bloqueos, que es
    /// justo lo que pasaba al cambiar de efecto en el teclado.
    /// </summary>
    private void ApplyColor(bool live)
    {
        if (_deviceIndex < 0) { Status.Text = "Selecciona un dispositivo."; return; }

        int deviceIndex = _deviceIndex;
        int zoneIndex = _zoneIndex;
        int ledIndex = _ledIndex;
        Device device = _rgb.Devices[deviceIndex];
        EffectItem? item = Selected;
        string mode = item?.Name ?? "Direct";

        // Efecto propio: lo lleva el motor, que ya corre en su propio hilo.
        if (item?.IsSoftware == true)
        {
            StartSoftwareEffect();
            return;
        }

        WpfColor c = CurrentColor();
        var target = new RgbColor(c.R, c.G, c.B);
        string what = zoneIndex == -1 ? device.Name : SelectionLabel.Text;

        Status.Text = "Aplicando...";

        Task.Run(() =>
        {
            // Solo se retira el efecto de ESTA zona: los de otros dispositivos
            // siguen corriendo. Si no, pintar la placa apagaria el teclado.
            _engine.Clear(deviceIndex, zoneIndex);

            string message;

            if (!mode.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            {
                RgbService.ModeResult result = _rgb.SetMode(deviceIndex, mode, target);
                message = result switch
                {
                    RgbService.ModeResult.ColorApplied
                        => $"'{mode}' con #{c.R:X2}{c.G:X2}{c.B:X2} en {device.Name}.",
                    RgbService.ModeResult.ColorIgnored
                        => $"'{mode}' en {device.Name}. Este efecto no admite color: usa el suyo.",
                    _ => $"No se pudo aplicar '{mode}'.",
                };
            }
            else if (!_rgb.EnterDirectMode(deviceIndex))
            {
                message = $"{device.Name} no admite control directo.";
            }
            else
            {
                // Se parte del estado guardado, no de un lienzo en negro: asi
                // pintar una zona no borra las que ya tenian color.
                if (ledIndex >= 0)
                {
                    _rgb.PaintRange(deviceIndex, ledIndex, ledIndex + 1, target, live);
                }
                else
                {
                    (int start, int end) = _rgb.ZoneRange(deviceIndex, zoneIndex);
                    _rgb.PaintRange(deviceIndex, start, end, target, live);
                }

                message = $"#{c.R:X2}{c.G:X2}{c.B:X2} en {what}";
            }

            Dispatcher.Invoke(() =>
            {
                Status.Text = message;
                RefreshShapeColors();
            });
        });
    }

    private void OnTurnOff(object sender, RoutedEventArgs e)
    {
        if (_deviceIndex < 0) return;

        int deviceIndex = _deviceIndex;
        Device device = _rgb.Devices[deviceIndex];
        Status.Text = "Apagando...";

        Task.Run(() =>
        {
            _engine.Clear(deviceIndex, -1);
            if (!_rgb.EnterDirectMode(deviceIndex)) return;

            _rgb.PaintRange(deviceIndex, 0, device.Leds.Length, new RgbColor(0, 0, 0), live: false);
            Dispatcher.Invoke(() =>
            {
                Status.Text = $"{device.Name} apagado.";
                RefreshShapeColors();
            });
        });
    }
}
