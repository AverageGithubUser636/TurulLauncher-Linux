using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TurulMC.Launcher;

public sealed partial class SplashWindow : Window
{
    private sealed record ThemeInfo(string Accent, string Dark, string Soft, string LogoFile);

    private static readonly IReadOnlyDictionary<string, ThemeInfo> Themes =
        new Dictionary<string, ThemeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["yellow"] = new("#D4AF37", "#B8961F", "#443613", "turul-logo-yellow.png"),
            ["green"] = new("#38D996", "#20B879", "#174435", "turul-logo-green.png"),
            ["red"] = new("#FF5D6C", "#D94453", "#4B2028", "turul-logo-red.png"),
            ["purple"] = new("#A970FF", "#8651DB", "#35234F", "turul-logo-purple.png"),
            ["sky"] = new("#56C7FF", "#2CA4E1", "#183C4E", "turul-logo-sky.png")
        };

    private readonly string _language;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private bool _closing;
    private double _lastProgress;
    private int _activeChip;
    private SolidColorBrush? _accentBrush;
    private SolidColorBrush? _softBrush;

    public SplashWindow(string? themeName = null, string? language = null)
    {
        _language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "hu";
        InitializeComponent();
        VersionBadge.Text = App.AppVersion;
        ApplyTheme(themeName ?? "yellow");
        ApplyLanguage();
        try { StatusPulse.Begin(); } catch { }
        try { GlowPulse.Begin(); } catch { }
        UpdatePhaseChips(8);
    }

    public void InitializeAfterActivation()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        if (_appWindow is null)
            return;

        _appWindow.Title = "TurulLauncher";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "turullauncher.ico");
        if (File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        const int width = 620;
        const int height = 430;
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + Math.Max(0, (work.Height - height) / 2);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(_presenter);
    }

    public void SetStatus(string text, double progress)
    {
        if (_closing)
            return;

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetStatus(text, progress));
            return;
        }

        var value = Math.Clamp(progress, 0d, 100d);
        // Startup progress should never visually jump backwards if two async
        // preload tasks report close to the same time.
        value = Math.Max(_lastProgress, value);
        _lastProgress = value;

        StatusText.Text = LocalizeStatus(text);
        StartupProgress.Value = value;
        ProgressText.Text = $"{Math.Round(value):0}%";
        UpdatePhaseChips(value);
    }

    /// <summary>
    /// A fázis-jelzők állapota a haladás alapján: Beállítások → Instance-ok → Felület → Kész.
    /// A kész fázisok halványan kipipálva, az aktuális témaszínnel kiemelve.
    /// </summary>
    private void UpdatePhaseChips(double progress)
    {
        var active = progress switch
        {
            >= 99 => 3,
            >= 70 => 2,
            >= 22 => 1,
            _ => 0
        };
        if (active == _activeChip && progress < 100) return;
        _activeChip = active;

        var accent = _accentBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD4, 0xAF, 0x37));
        var soft = _softBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x44, 0x36, 0x13));
        var done = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1A, 0x21, 0x29));
        var idle = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0D, 0x12, 0x18));
        var idleText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6B, 0x73, 0x7E));
        var doneText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0x95, 0xA1));

        var chips = new[] { (Chip1, Chip1Text), (Chip2, Chip2Text), (Chip3, Chip3Text), (Chip4, Chip4Text) };
        for (var i = 0; i < chips.Length; i++)
        {
            var (border, label) = chips[i];
            if (i < active)
            {
                border.Background = done;
                border.BorderBrush = done;
                label.Foreground = doneText;
                label.Text = ChipLabel(i) + " ✓";
            }
            else if (i == active)
            {
                border.Background = soft;
                border.BorderBrush = accent;
                label.Foreground = accent;
                label.Text = ChipLabel(i);
            }
            else
            {
                border.Background = idle;
                border.BorderBrush = idle;
                label.Foreground = idleText;
                label.Text = ChipLabel(i);
            }
        }
    }

    private string ChipLabel(int index) => _language == "en"
        ? index switch { 0 => "Settings", 1 => "Instances", 2 => "Interface", _ => "Ready" }
        : index switch { 0 => "Beállítások", 1 => "Instance-ok", 2 => "Felület", _ => "Kész" };

    public async Task CloseWithFadeAsync()
    {
        if (_closing)
            return;

        _closing = true;
        if (_presenter is not null)
            _presenter.IsAlwaysOnTop = false;

        for (var i = 0; i <= 12; i++)
        {
            Root.Opacity = 1d - (i / 12d);
            await Task.Delay(14);
        }

        Close();
    }

    private void ApplyTheme(string themeName)
    {
        if (!Themes.TryGetValue(themeName, out var theme))
            theme = Themes["yellow"];

        var accent = Brush(theme.Accent);
        var dark = Brush(theme.Dark);
        var soft = Brush(theme.Soft);
        _accentBrush = accent;
        _softBrush = soft;

        BrandDot.Background = accent;
        ShellBorder.BorderBrush = soft;
        OuterHalo.Stroke = soft;
        InnerHalo.Stroke = dark;
        ProgressText.Foreground = accent;
        StatusDot.Fill = accent;
        HaloRing.Foreground = accent;
        GlowStop.Color = Tinted(theme.Accent, 0x33);
        DividerStop.Color = Tinted(theme.Accent, 0x66);
        BarStop0.Color = ColorFromHex(theme.Dark);
        BarStop1.Color = ColorFromHex(theme.Accent);
        BarGlowStop0.Color = Tinted(theme.Accent, 0x00);
        BarGlowStop1.Color = Tinted(theme.Accent, 0x55);

        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Themes", theme.LogoFile);
        if (File.Exists(logoPath))
        {
            var logoUri = new Uri("file:///" + logoPath.Replace('\\', '/'));
            LogoImage.Source = new BitmapImage(logoUri);
        }

        // A fázis-jelzőket az új téma színével rajzoljuk újra.
        _activeChip = -1;
        UpdatePhaseChips(_lastProgress);
    }

    private void ApplyLanguage()
    {
        if (_language == "en")
        {
            SubtitleText.Text = "Preparing your launcher…";
            FooterText.Text = "The main window opens only when startup is ready.";
            StatusText.Text = "Starting TurulLauncher…";
        }
        else
        {
            SubtitleText.Text = "A launcher előkészítése…";
            FooterText.Text = "A főablak akkor jelenik meg, amikor minden készen áll.";
            StatusText.Text = "TurulLauncher indítása…";
        }
    }

    private string LocalizeStatus(string text)
    {
        if (_language != "en")
            return text;

        return text switch
        {
            "TurulLauncher indítása…" => "Starting TurulLauncher…",
            "Instance-ok és beállítások betöltése…" => "Loading instances and settings…",
            "Ablak előkészítése…" => "Preparing window…",
            "WebView2 előkészítése…" => "Preparing WebView2…",
            "Launcher felület előtöltése…" => "Preloading launcher interface…",
            "Beállítások és profilok betöltve…" => "Settings and profiles loaded…",
            "A felület hibajelzéssel töltött be…" => "Interface loaded with a warning…",
            "A launcher tovább tölt a háttérben…" => "Launcher is still loading in the background…",
            "Kész — launcher megnyitása…" => "Ready — opening launcher…",
            "Launcher megnyitása…" => "Opening launcher…",
            _ => text
        };
    }

    private static SolidColorBrush Brush(string hex) => new(ColorFromHex(hex));

    private static Windows.UI.Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6)
            return Microsoft.UI.Colors.White;

        var r = Convert.ToByte(hex.Substring(0, 2), 16);
        var g = Convert.ToByte(hex.Substring(2, 2), 16);
        var b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private static Windows.UI.Color Tinted(string hex, byte alpha)
    {
        var c = ColorFromHex(hex);
        c.A = alpha;
        return c;
    }
}
