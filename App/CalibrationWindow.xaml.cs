using RgbStudio.Rgb;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace RgbStudio;

public partial class CalibrationWindow : Window
{
    private readonly ColorCalibration.Profile _initial;

    internal ColorCalibration.Profile Profile { get; private set; }

    internal CalibrationWindow(string deviceName,
                               ColorCalibration.Profile current,
                               ColorCalibration.Profile initial)
    {
        InitializeComponent();
        DeviceName.Text = deviceName;
        _initial = initial;
        Profile = current;
        Fill(current);
    }

    private void Fill(ColorCalibration.Profile profile)
    {
        RedGainBox.Text = Percent(profile.RedGain);
        GreenGainBox.Text = Percent(profile.GreenGain);
        BlueGainBox.Text = Percent(profile.BlueGain);
        RedGammaBox.Text = Decimal(profile.RedGamma);
        GreenGammaBox.Text = Decimal(profile.GreenGamma);
        BlueGammaBox.Text = Decimal(profile.BlueGamma);
        Validation.Text = string.Empty;
    }

    private static string Percent(double value)
        => Math.Round(value * 100).ToString(CultureInfo.InvariantCulture);

    private static string Decimal(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryNumber(TextBox box, out double value)
        => double.TryParse(box.Text.Trim().Replace(',', '.'),
                           NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private bool TryRead(out ColorCalibration.Profile profile)
    {
        profile = new ColorCalibration.Profile();

        if (!TryNumber(RedGainBox, out double redGain) ||
            !TryNumber(GreenGainBox, out double greenGain) ||
            !TryNumber(BlueGainBox, out double blueGain) ||
            !TryNumber(RedGammaBox, out double redGamma) ||
            !TryNumber(GreenGammaBox, out double greenGamma) ||
            !TryNumber(BlueGammaBox, out double blueGamma))
            return false;

        if (redGain is < 0 or > 300 || greenGain is < 0 or > 300 || blueGain is < 0 or > 300 ||
            redGamma is < 0.1 or > 4 || greenGamma is < 0.1 or > 4 || blueGamma is < 0.1 or > 4)
            return false;

        profile = new ColorCalibration.Profile
        {
            RedGain = redGain / 100,
            GreenGain = greenGain / 100,
            BlueGain = blueGain / 100,
            RedGamma = redGamma,
            GreenGamma = greenGamma,
            BlueGamma = blueGamma,
        };
        return true;
    }

    private void OnNeutral(object sender, RoutedEventArgs e) => Fill(new ColorCalibration.Profile());

    private void OnInitial(object sender, RoutedEventArgs e) => Fill(_initial);

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out ColorCalibration.Profile profile))
        {
            Validation.Text = "Usa ganancias entre 0 y 300 %, y gamma entre 0.1 y 4.";
            return;
        }

        Profile = profile;
        DialogResult = true;
    }
}
