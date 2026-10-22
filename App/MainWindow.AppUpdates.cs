using RgbStudio.Updates;
using System.Net.Http;
using System.Windows;

namespace RgbStudio;

public partial class MainWindow
{
    private RgbStudioUpdater? _appUpdater;
    private RgbStudioUpdate? _preparedAppUpdate;
    private CancellationTokenSource? _appCheckCancellation;
    private bool _checkingAppUpdate;

    internal static Version ApplicationVersion
    {
        get
        {
            Version version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private void InitializeRgbStudioUpdates()
    {
        _appUpdater = new RgbStudioUpdater(ApplicationVersion);
        RgbStudioVersionLabel.Text = "RGB STUDIO " + ApplicationVersion;
        AutoUpdateRgbStudio.IsChecked = _appUpdater.Automatic;
        Loaded += async (_, _) =>
        {
            if (_appUpdater.Automatic) await CheckRgbStudioUpdatesAsync();
            else RgbStudioUpdateStatus.Text = "Actualizaciones automáticas desactivadas.";
        };
        _updateTimer.Tick += async (_, _) => { if (_appUpdater.Automatic) await CheckRgbStudioUpdatesAsync(); };
        Closed += (_, _) => _appUpdater.Dispose();
    }

    private async void OnCheckRgbStudioUpdate(object sender, RoutedEventArgs e) => await CheckRgbStudioUpdatesAsync();

    private async void OnAutoUpdateRgbStudioChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _appUpdater is null) return;
        try
        {
            _appUpdater.Automatic = AutoUpdateRgbStudio.IsChecked == true;
            if (_appUpdater.Automatic) await CheckRgbStudioUpdatesAsync();
            else
            {
                _appCheckCancellation?.Cancel();
                RgbStudioUpdateStatus.Text = "Actualizaciones automáticas desactivadas.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RgbStudioUpdateStatus.Text = "No se pudo guardar la preferencia: " + ex.Message;
        }
    }

    private async Task CheckRgbStudioUpdatesAsync()
    {
        if (_checkingAppUpdate || _appUpdater is null || _updatesLifetime.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updatesLifetime.Token);
        CancellationToken token = cancellation.Token;
        _appCheckCancellation = cancellation;
        _checkingAppUpdate = true;
        CheckRgbStudioUpdateButton.IsEnabled = false;
        RgbStudioUpdateProgress.Visibility = Visibility.Visible;
        RgbStudioUpdateProgress.IsIndeterminate = true;
        var progress = new Progress<RgbStudioUpdateProgress>(value =>
        {
            if (token.IsCancellationRequested) return;
            RgbStudioUpdateStatus.Text = value.Message;
            RgbStudioUpdateProgress.IsIndeterminate = value.Percent is null;
            if (value.Percent is { } percent) RgbStudioUpdateProgress.Value = percent;
        });
        try
        {
            RgbStudioUpdate? update = await _appUpdater.CheckAndDownloadAsync(progress, token);
            if (update is not null)
            {
                bool announce = _preparedAppUpdate?.Release.Version != update.Release.Version;
                _preparedAppUpdate = update;
                InstallRgbStudioUpdateButton.Visibility = Visibility.Visible;
                if (announce) _trayIcon.ShowUpdateReady(update.Release.Version.ToString());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                   or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException
                                   or ArgumentException or OverflowException)
        {
            RgbStudioUpdateStatus.Text = "No se pudo comprobar RGB Studio: " + ex.Message;
        }
        finally
        {
            _checkingAppUpdate = false;
            _appCheckCancellation = null;
            CheckRgbStudioUpdateButton.IsEnabled = true;
            RgbStudioUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnInstallRgbStudioUpdate(object sender, RoutedEventArgs e)
    {
        if (QueuePreparedAppUpdate(force: true)) ((App)Application.Current).ExitApplication(installUpdate: false);
    }

    internal bool QueuePreparedAppUpdate(bool force = false)
    {
        if (_preparedAppUpdate is null || _appUpdater is null || (!force && !_appUpdater.Automatic)) return false;
        try
        {
            if (_appUpdater.StartInstallerAfterExit(_preparedAppUpdate, AppContext.BaseDirectory)) return true;
            RgbStudioUpdateStatus.Text = "Para actualizar esta copia de desarrollo, instala el release desde GitHub.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            RgbStudioUpdateStatus.Text = "No se pudo iniciar el instalador: " + ex.Message;
        }
        return false;
    }
}
