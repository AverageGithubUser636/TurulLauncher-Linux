using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Indítási splash: logó, verzió, folyamatjelző és fázis-jelvények
/// (Beállítások → Instance-ok → Felület → Kész), a Windows-verzió mintájára.
/// </summary>
public partial class SplashWindow : Window
{
    private double _lastProgress;

    public SplashWindow()
    {
        InitializeComponent();
        VersionBadge.Text = "v" + AppInfo.Version;
        RefreshLogo();
        Services.ThemeService.Current.Changed += OnThemeChanged;
        UpdatePhaseChips(0);
        Closed += (_, _) => Services.ThemeService.Current.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshLogo();
            UpdatePhaseChips(_lastProgress);
        });
    }

    private void RefreshLogo()
    {
        try
        {
            using var stream = global::Avalonia.Platform.AssetLoader.Open(
                new Uri(Services.ThemeService.Current.LogoUri));
            LogoImage.Source = new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch { }
    }

    /// <summary>Folyamat frissítése a háttérszálról is hívható.</summary>
    public void ReportProgress(double value, string? status = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            value = Math.Clamp(value, 0, 100);
            value = Math.Max(_lastProgress, value);
            _lastProgress = value;
            StartupProgress.Value = value;
            if (status is not null) ProgressText.Text = status;
            else ProgressText.Text = $"{Math.Round(value):0}%";
            UpdatePhaseChips(value);
        });
    }

    private void UpdatePhaseChips(double progress)
    {
        var active = progress switch
        {
            >= 99 => 3,
            >= 70 => 2,
            >= 22 => 1,
            _ => 0
        };

        var accent = new SolidColorBrush(Services.ThemeService.Current.AccentColor());
        var soft = new SolidColorBrush(Services.ThemeService.Current.SoftColor());
        var done = new SolidColorBrush(Color.Parse("#1A2129"));
        var idle = new SolidColorBrush(Color.Parse("#0D1218"));
        var idleText = new SolidColorBrush(Color.Parse("#6B737E"));
        var doneText = new SolidColorBrush(Color.Parse("#8C95A1"));

        var chips = new[] { (Chip1, Chip1Text), (Chip2, Chip2Text), (Chip3, Chip3Text), (Chip4, Chip4Text) };
        var labels = new[] { "Beállítások", "Instance-ok", "Felület", "Kész" };
        for (var i = 0; i < chips.Length; i++)
        {
            var (border, label) = chips[i];
            if (i < active)
            {
                border.Background = done;
                border.BorderBrush = done;
                label.Foreground = doneText;
                label.Text = labels[i] + " ✓";
            }
            else if (i == active)
            {
                border.Background = soft;
                border.BorderBrush = accent;
                label.Foreground = accent;
                label.Text = labels[i];
            }
            else
            {
                border.Background = idle;
                border.BorderBrush = idle;
                label.Foreground = idleText;
                label.Text = labels[i];
            }
        }
    }
}
