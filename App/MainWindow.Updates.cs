using RgbStudio.Updates;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace RgbStudio;

public partial class MainWindow
{
    private readonly OpenRgbUpdateStore _updateStore = new();
    private readonly CancellationTokenSource _updatesLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = OpenRgbUpdater.CheckInterval };
    private OpenRgbUpdater? _openRgbUpdater;
    private CancellationTokenSource? _activeUpdateCancellation;
    private bool _checkingOpenRgb;
    private readonly SemaphoreSlim _openRgbStartGate = new(1, 1);

    private void InitializeOpenRgbUpdates()
    {
        _openRgbUpdater = new OpenRgbUpdater(_updateStore);
        AutoUpdateOpenRgb.IsChecked = _updateStore.Preferences.Automatic;
        Loaded += async (_, _) =>
        {
            _updateTimer.Start();
            await CheckOpenRgbUpdatesAsync(force: false);
        };
        _updateTimer.Tick += async (_, _) => await CheckOpenRgbUpdatesAsync(force: false);
        Closed += (_, _) =>
        {
            _updateTimer.Stop();
            _updatesLifetime.Cancel();
            _openRgbUpdater.Dispose();
        };
    }

    private async void OnCheckOpenRgbUpdate(object sender, RoutedEventArgs e)
        => await CheckOpenRgbUpdatesAsync(force: true);

    private async void OnAutoUpdateOpenRgbChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            bool enabled = AutoUpdateOpenRgb.IsChecked == true;
            _updateStore.SetAutomatic(enabled);
            if (enabled) await CheckOpenRgbUpdatesAsync(force: false);
            else
            {
                _activeUpdateCancellation?.Cancel();
                if (!_checkingOpenRgb) OpenRgbUpdateStatus.Text = "Actualizaciones automáticas desactivadas.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OpenRgbUpdateStatus.Text = $"No se pudo guardar la preferencia: {ex.Message}";
        }
    }

    private async Task CheckOpenRgbUpdatesAsync(bool force)
    {
        if (_checkingOpenRgb || _openRgbUpdater is null || _updatesLifetime.IsCancellationRequested) return;
        using var updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(_updatesLifetime.Token);
        _activeUpdateCancellation = updateCancellation;
        CancellationToken updateToken = updateCancellation.Token;
        _checkingOpenRgb = true;
        CheckOpenRgbUpdateButton.IsEnabled = false;
        OpenRgbUpdateProgress.Visibility = Visibility.Visible;
        OpenRgbUpdateProgress.IsIndeterminate = true;
        var progress = new Progress<OpenRgbUpdateProgress>(update =>
        {
            if (_updatesLifetime.IsCancellationRequested || updateToken.IsCancellationRequested) return;
            OpenRgbUpdateStatus.Text = update.Message;
            OpenRgbUpdateProgress.IsIndeterminate = update.Percent is null;
            if (update.Percent is { } percent) OpenRgbUpdateProgress.Value = percent;
        });
        try
        {
            OpenRgbUpdateResult result = await _openRgbUpdater.CheckAsync(force, progress, updateToken);
            if (result.Installed && !_rgb.Connected && !_updatesLifetime.IsCancellationRequested)
                await EnsureOpenRgbConnectionAsync();
        }
        catch (OperationCanceledException) when (_updatesLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) when (updateToken.IsCancellationRequested)
        {
            OpenRgbUpdateStatus.Text = AutoUpdateOpenRgb.IsChecked == true
                ? "Comprobación cancelada. Puedes volver a usar Buscar ahora."
                : "Actualizaciones automáticas desactivadas.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidOperationException
                                   or OperationCanceledException or ArgumentException or OverflowException or BadImageFormatException)
        {
            OpenRgbUpdateStatus.Text = ex is OperationCanceledException
                ? "OpenRGB: la comprobación tardó demasiado. Se volverá a intentar."
                : $"No se pudo actualizar OpenRGB: {ex.Message}";
        }
        finally
        {
            _checkingOpenRgb = false;
            _activeUpdateCancellation = null;
            CheckOpenRgbUpdateButton.IsEnabled = true;
            OpenRgbUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task EnsureOpenRgbConnectionAsync()
    {
        if (_updatesLifetime.IsCancellationRequested) return;
        try
        {
            await _openRgbStartGate.WaitAsync(_updatesLifetime.Token);
            try
            {
                Status.Text = "Preparando el servidor de OpenRGB…";
                string? message = await new OpenRgbHost().EnsureStartedAsync(_updateStore, _updatesLifetime.Token);
                if (message is not null) { Status.Text = message; return; }
                if (!_updatesLifetime.IsCancellationRequested) Connect();
            }
            finally { _openRgbStartGate.Release(); }
        }
        catch (OperationCanceledException) when (_updatesLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                   or InvalidDataException or InvalidOperationException)
        {
            Status.Text = $"No se pudo iniciar OpenRGB: {ex.Message}";
        }
    }
}
