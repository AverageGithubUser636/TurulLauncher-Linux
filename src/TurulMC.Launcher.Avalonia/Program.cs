using Avalonia;
using Avalonia.Media;

namespace TurulMC.Launcher.Avalonia;

public static class Program
{
    // Avalonia configuration and startup. Anything before Avalonia is initialized
    // must not touch UI types, so keep this minimal and exception-safe.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            StartupLog.WriteCrash("Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A beágyazott Inter mellett a rendszer betűit is felajánljuk tartalékban,
            // hogy Linuxon (ami gyakran más betűkészlet-készlettel indul) se legyen
            // "glyphTypeface" hiba, ha egy glyph hiányzik.
            .WithInterFont()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Noto Sans") },
                    new FontFallback { FontFamily = new FontFamily("DejaVu Sans") },
                    new FontFallback { FontFamily = new FontFamily("sans-serif") },
                }
            })
            .LogToTrace();
}
