using Avalonia.Media;
using Avalonia.Controls;
using global::Avalonia.Styling;
using TurulMC.Core.Logging;

namespace TurulMC.Launcher.Avalonia.Services;

/// <summary>
/// Témakezelés: a Windows-launcher 5 színsémája (sárga/zöld/piros/lila/világoskék),
/// mindegyikhez saját Turul-logóval. Az akcentus az App erőforrásaiba íródik,
/// így minden <c>DynamicResource</c>-kötésű stílus azonnal frissül.
/// </summary>
public sealed class ThemeService
{
    public sealed record ThemeInfo(
        string Name,
        string LabelHu,
        string Accent,
        string Dark,
        string Soft,
        string LogoFile);

    public static readonly IReadOnlyList<ThemeInfo> Themes = new List<ThemeInfo>
    {
        new("yellow", "Arany",   "#D4AF37", "#B8961F", "#443613", "turul-logo-yellow.png"),
        new("green",  "Zöld",    "#38D996", "#20B879", "#174435", "turul-logo-green.png"),
        new("red",    "Piros",   "#FF5D6C", "#D94453", "#4B2028", "turul-logo-red.png"),
        new("purple", "Lila",    "#A970FF", "#8651DB", "#35234F", "turul-logo-purple.png"),
        new("sky",    "Égszín",  "#56C7FF", "#2CA4E1", "#183C4E", "turul-logo-sky.png"),
    };

    private static ThemeService? _current;
    public static ThemeService Current => _current ??= new ThemeService();

    private ThemeService() { }

    public ThemeInfo Active { get; private set; } = Themes[0];

    /// <summary>Az aktuális logó avares-útvonala (Image Source-hoz).</summary>
    public string LogoUri => $"avares://TurulLauncher/Assets/Logos/{Active.LogoFile}";

    public global::Avalonia.Media.Color AccentColor()
    {
        try { return global::Avalonia.Media.Color.Parse(Active.Accent); }
        catch { return global::Avalonia.Media.Color.Parse("#D4AF37"); }
    }

    public global::Avalonia.Media.Color SoftColor()
    {
        try { return global::Avalonia.Media.Color.Parse(Active.Soft); }
        catch { return global::Avalonia.Media.Color.Parse("#443613"); }
    }

    public event Action? Changed;

    public static ThemeInfo Resolve(string? name)
        => Themes.FirstOrDefault(t =>
                t.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase))
            ?? Themes[0];

    /// <summary>
    /// Egyedi akcentusszín (#RRGGBB): a sötét és puha árnyalatot a rendszer
    /// számolja (sötétítés + háttérbe keverés), a logó marad a témáé.
    /// Érvénytelen értékre nem nyúl semmihez.
    /// </summary>
    /// <returns>Igaz, ha alkalmazva lett.</returns>
    public bool ApplyCustomAccent(string? hex)
    {
        if (!TryParseAccent(hex, out var accent)) return false;
        var dark = Darken(accent, 0.68);
        var soft = Mix(accent, global::Avalonia.Media.Color.Parse("#14171C"), 0.30);
        ApplyColors(ToHex(accent), ToHex(dark), ToHex(soft));
        Changed?.Invoke();
        LauncherLogger.Info($"Egyedi akcentus alkalmazva: {ToHex(accent)}");
        return true;
    }

    /// <summary>Felületi animációk (hover-átmenetek) ki/bekapcsolása.</summary>
    public void SetAnimationsEnabled(bool enabled)
    {
        try
        {
            var app = global::Avalonia.Application.Current;
            if (app is null) return;
            if (_transitionsStyle is not null)
            {
                app.Styles.Remove(_transitionsStyle);
                _transitionsStyle = null;
            }
            if (!enabled) return;

            var fast = TimeSpan.FromMilliseconds(120);
            var style = new global::Avalonia.Styling.Style();
            AddTransitions(style,
                s => s.OfType<global::Avalonia.Controls.Button>(),
                new global::Avalonia.Animation.BrushTransition
                {
                    Property = global::Avalonia.Controls.Button.BackgroundProperty,
                    Duration = fast
                },
                new global::Avalonia.Animation.DoubleTransition
                {
                    Property = global::Avalonia.Controls.Button.OpacityProperty,
                    Duration = fast
                });
            AddTransitions(style,
                s => s.OfType<global::Avalonia.Controls.ListBoxItem>(),
                new global::Avalonia.Animation.BrushTransition
                {
                    Property = global::Avalonia.Controls.ListBoxItem.BackgroundProperty,
                    Duration = TimeSpan.FromMilliseconds(100)
                });
            AddTransitions(style,
                s => s.OfType<global::Avalonia.Controls.TextBox>(),
                new global::Avalonia.Animation.BrushTransition
                {
                    Property = global::Avalonia.Controls.TextBox.BorderBrushProperty,
                    Duration = fast
                });
            _transitionsStyle = style;
            app.Styles.Add(style);
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Animáció-kapcsoló hiba: " + ex.Message);
        }
    }

    private global::Avalonia.Styling.Style? _transitionsStyle;

    private static void AddTransitions(
        global::Avalonia.Styling.Style style,
        Func<global::Avalonia.Styling.Selector?, global::Avalonia.Styling.Selector> selector,
        params global::Avalonia.Animation.ITransition[] transitions)
    {
        var inner = new global::Avalonia.Styling.Style(selector);
        var list = new global::Avalonia.Animation.Transitions();
        foreach (var t in transitions) list.Add(t);
        inner.Setters.Add(new global::Avalonia.Styling.Setter(
            global::Avalonia.Animation.Animatable.TransitionsProperty,
            list));
        style.Children.Add(inner);
    }

    public static bool TryParseAccent(string? hex, out global::Avalonia.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6 || !t.All(c => Uri.IsHexDigit(c))) return false;
        try
        {
            color = global::Avalonia.Media.Color.Parse("#" + t);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static global::Avalonia.Media.Color Darken(global::Avalonia.Media.Color c, double factor)
        => global::Avalonia.Media.Color.FromRgb(
            (byte)Math.Clamp(c.R * factor, 0, 255),
            (byte)Math.Clamp(c.G * factor, 0, 255),
            (byte)Math.Clamp(c.B * factor, 0, 255));

    private static global::Avalonia.Media.Color Mix(
        global::Avalonia.Media.Color fg, global::Avalonia.Media.Color bg, double amount)
    {
        var a = Math.Clamp(amount, 0, 1);
        return global::Avalonia.Media.Color.FromRgb(
            (byte)(fg.R * a + bg.R * (1 - a)),
            (byte)(fg.G * a + bg.G * (1 - a)),
            (byte)(fg.B * a + bg.B * (1 - a)));
    }

    private static string ToHex(global::Avalonia.Media.Color c)
        => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Téma alkalmazása: erőforrás-frissítés + esemény a logóknak.</summary>
    public void Apply(string? name)
    {
        Active = Resolve(name);
        ApplyColors(Active.Accent, Active.Dark, Active.Soft);
        Changed?.Invoke();
        LauncherLogger.Info($"Téma alkalmazva: {Active.Name} ({Active.Accent})");
    }

    private static void ApplyColors(string accent, string dark, string soft)
    {
        try
        {
            if (global::Avalonia.Application.Current?.Resources is { } resources)
            {
                SetBrush(resources, "TurulGoldBrush", accent);
                SetBrush(resources, "TurulGoldDimBrush", dark);
                SetBrush(resources, "TurulSelectBrush", soft);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Téma erőforrás-frissítés hiba: " + ex.Message);
        }
    }

    private static void SetBrush(IResourceDictionary resources, string key, string hex)
    {
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = Color.Parse(hex);
            return;
        }
        resources[key] = new SolidColorBrush(Color.Parse(hex));
    }
}
