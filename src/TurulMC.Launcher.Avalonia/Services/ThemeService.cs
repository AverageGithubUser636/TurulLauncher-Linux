using Avalonia.Media;
using Avalonia.Controls;
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

    /// <summary>Téma alkalmazása: erőforrás-frissítés + esemény a logóknak.</summary>
    public void Apply(string? name)
    {
        Active = Resolve(name);
        try
        {
            if (global::Avalonia.Application.Current?.Resources is { } resources)
            {
                SetBrush(resources, "TurulGoldBrush", Active.Accent);
                SetBrush(resources, "TurulGoldDimBrush", Active.Dark);
                SetBrush(resources, "TurulSelectBrush", Active.Soft);
            }
        }
        catch (Exception ex)
        {
            LauncherLogger.Warning("Téma erőforrás-frissítés hiba: " + ex.Message);
        }
        Changed?.Invoke();
        LauncherLogger.Info($"Téma alkalmazva: {Active.Name} ({Active.Accent})");
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
