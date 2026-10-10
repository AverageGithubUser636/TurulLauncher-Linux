using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Instance létrehozó/szerkesztő dialógus. <c>ShowDialog</c>-gal használandó;
/// mentéskor a VM visszaírja a módosított példányt.
/// </summary>
public partial class InstanceEditWindow : Window
{
    private readonly InstanceEditViewModel _vm;

    public InstanceEditWindow(LauncherInstance source, bool isNew)
    {
        InitializeComponent();
        _vm = new InstanceEditViewModel(source, isNew);
        DataContext = _vm;
        Title = isNew ? "Új Instance" : "Instance szerkesztése";
    }

    /// <summary>
    /// A szerkesztett (vagy új) példány. Csak akkor érvényes, ha a dialógus
    /// <c>true</c>-val zárt.
    /// </summary>
    public LauncherInstance Result => _vm.BuildResult();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!_vm.Validate())
            return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(false);

    private async void OnPickJava(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (StorageProvider is null) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Java futtatható választása",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Java") { Patterns = new[] { "java", "java.exe", "*" } }
                }
            });
            var picked = files.FirstOrDefault()?.Path.LocalPath;
            if (!string.IsNullOrWhiteSpace(picked))
                _vm.JavaPath = picked;
        }
        catch (Exception ex)
        {
            _vm.ErrorText = "Fájlválasztási hiba: " + ex.Message;
        }
    }
}

public sealed class InstanceEditViewModel : PropertyChangedBase
{
    private readonly LauncherInstance _source;
    private readonly bool _isNew;

    private string _name = "";
    private string _minecraftVersion = "";
    private string _loader = "none";
    private string _loaderVersion = "";
    private double _ramMb = 4096;
    private string _javaPath = "";
    private string _jvmArgs = "";
    private double _windowWidth;
    private double _windowHeight;
    private string _notes = "";
    private string _errorText = "";
    private bool _showSnapshots;

    public List<string> LoaderOptions { get; } = new() { "none", "fabric" };

    /// <summary>Mojang-manifestből töltött verziólista (gyorsítótárazva).</summary>
    public List<string> VersionOptions { get; } = new();

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (!Raise(ref _showSnapshots, value)) return;
            _ = LoadVersionsAsync();
        }
    }

    public string Name { get => _name; set => Raise(ref _name, value); }
    public string MinecraftVersion { get => _minecraftVersion; set => Raise(ref _minecraftVersion, value); }
    public string Loader { get => _loader; set => Raise(ref _loader, value); }
    public string LoaderVersion { get => _loaderVersion; set => Raise(ref _loaderVersion, value); }
    public double RamMb { get => _ramMb; set => Raise(ref _ramMb, value); }
    public string JavaPath { get => _javaPath; set => Raise(ref _javaPath, value); }
    public string JvmArgs { get => _jvmArgs; set => Raise(ref _jvmArgs, value); }
    public double WindowWidth { get => _windowWidth; set => Raise(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Raise(ref _windowHeight, value); }
    public string Notes { get => _notes; set => Raise(ref _notes, value); }
    public string ErrorText { get => _errorText; set => Raise(ref _errorText, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public InstanceEditViewModel(LauncherInstance source, bool isNew)
    {
        _source = source;
        _isNew = isNew;
        _name = source.Name;
        _minecraftVersion = source.MinecraftVersion;
        _loader = string.IsNullOrWhiteSpace(source.Loader) ? "none" : source.Loader.ToLowerInvariant();
        if (!LoaderOptions.Contains(_loader)) _loader = "none";
        _loaderVersion = source.LoaderVersion;
        _ramMb = source.RamMb > 0 ? source.RamMb : 4096;
        _javaPath = source.JavaPath;
        _jvmArgs = source.JvmArgs;
        _windowWidth = source.WindowWidth;
        _windowHeight = source.WindowHeight;
        _notes = source.Notes;
        _ = LoadVersionsAsync();
    }

    /// <summary>Verziólista betöltése (hálózat nélkül csendben üres marad —
    /// a szabad szöveges beírás akkor is működik).</summary>
    public async Task LoadVersionsAsync()
    {
        try
        {
            var cache = Path.Combine(
                Core.Storage.LauncherPaths.DataRoot, "cache", "version_manifest.json");
            var catalog = new Core.Minecraft.MinecraftVersionCatalog(null, cache);
            var versions = await catalog.ListAsync(_showSnapshots);
            VersionOptions.Clear();
            foreach (var v in versions)
                VersionOptions.Add(v.Id);
            // Az aktuális érték akkor is választható, ha nincs a listában.
            if (!string.IsNullOrWhiteSpace(MinecraftVersion) &&
                !VersionOptions.Contains(MinecraftVersion))
                VersionOptions.Insert(0, MinecraftVersion);
            OnPropertyChanged(nameof(VersionOptions));
        }
        catch (Exception ex)
        {
            Core.Logging.LauncherLogger.Debug("Verziólista hiba: " + ex.Message);
        }
    }

    public bool Validate()
    {
        ErrorText = "";
        OnPropertyChanged(nameof(HasError));

        if (string.IsNullOrWhiteSpace(Name))
            return Fail("Adj meg egy nevet.");
        if (string.IsNullOrWhiteSpace(MinecraftVersion))
            return Fail("Adj meg egy Minecraft verziót (pl. 1.21.1).");
        if (RamMb is < 1024 or > 65536)
            return Fail("A RAM 1024 és 65536 MB között lehet.");

        var java = JavaPath.Trim();
        if (!string.IsNullOrWhiteSpace(java))
        {
            try
            {
                Core.Validation.LauncherSettingsValidator.ValidateJavaPath(java);
            }
            catch (Exception ex)
            {
                return Fail("Java útvonal: " + ex.Message);
            }
        }

        return true;
    }

    public LauncherInstance BuildResult()
    {
        if (_isNew)
        {
            return new LauncherInstance
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = Name.Trim(),
                MinecraftVersion = MinecraftVersion.Trim(),
                Loader = Loader,
                LoaderVersion = LoaderVersion.Trim(),
                RamMb = (int)RamMb,
                JavaPath = JavaPath.Trim(),
                JvmArgs = JvmArgs.Trim(),
                WindowWidth = (int)WindowWidth,
                WindowHeight = (int)WindowHeight,
                Notes = Notes.Trim(),
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow
            };
        }

        _source.Name = Name.Trim();
        _source.MinecraftVersion = MinecraftVersion.Trim();
        _source.Loader = Loader;
        _source.LoaderVersion = LoaderVersion.Trim();
        _source.RamMb = (int)RamMb;
        _source.JavaPath = JavaPath.Trim();
        _source.JvmArgs = JvmArgs.Trim();
        _source.WindowWidth = (int)WindowWidth;
        _source.WindowHeight = (int)WindowHeight;
        _source.Notes = Notes.Trim();
        return _source;
    }

    private bool Fail(string message)
    {
        ErrorText = message;
        OnPropertyChanged(nameof(HasError));
        LauncherLogger.Warning("Instance validációs hiba: " + message);
        return false;
    }
}
