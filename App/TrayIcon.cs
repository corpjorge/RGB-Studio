using System.Drawing;
using System.Windows.Forms;

namespace RgbStudio;

/// <summary>
/// Icono del area de notificacion. Mantiene accesible la ventana mientras los
/// efectos de software y el seguimiento de la aplicacion siguen ejecutandose.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    internal event Action? OpenRequested;
    internal event Action? ExitRequested;

    internal TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir RGB Studio", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "RGB Studio",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    internal void ShowBackgroundHint()
    {
        _icon.BalloonTipTitle = "RGB Studio sigue activo";
        _icon.BalloonTipText = "Los colores y efectos continuan en segundo plano.";
        _icon.ShowBalloonTip(3000);
    }

    private static Icon LoadIcon()
    {
        Uri uri = new("pack://application:,,,/Assets/RgbStudio.ico");
        using Stream stream = System.Windows.Application.GetResourceStream(uri).Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
