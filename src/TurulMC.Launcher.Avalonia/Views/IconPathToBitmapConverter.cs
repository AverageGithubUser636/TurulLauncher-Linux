using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Helyi ikonfájl-útvonal → <see cref="Bitmap"/> a Modrinth-ikonokhoz.
/// Memóriában gyorsítótáraz (legfeljebb 500 kép); hibás/hiányzó fájlra
/// <c>null</c>-t ad, ilyenkor a sor mögöttes glyph-je látszik.
/// </summary>
public sealed class IconPathToBitmapConverter : IValueConverter
{
    public static IconPathToBitmapConverter Instance { get; } = new();

    private readonly Dictionary<string, Bitmap?> _cache = new();
    private readonly object _lock = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached))
                return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
                bitmap = new Bitmap(path);
        }
        catch
        {
            bitmap = null;
        }

        lock (_lock)
        {
            if (_cache.Count > 500) _cache.Clear();
            _cache[path] = bitmap;
        }
        return bitmap;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
