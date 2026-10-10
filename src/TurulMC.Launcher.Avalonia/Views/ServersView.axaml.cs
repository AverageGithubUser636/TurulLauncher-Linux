using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TurulMC.Core.Logging;
using TurulMC.Core.Models;
using TurulMC.Launcher.Avalonia.Services;

namespace TurulMC.Launcher.Avalonia.Views;

public partial class ServersView : UserControl
{
    private readonly ServersViewModel _vm;

    public ServersView()
    {
        InitializeComponent();
        _vm = new ServersViewModel();
        DataContext = _vm;
        _ = _vm.InitializeAsync();
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
        => await _vm.AddAsync();

    private async void OnDelete(object? sender, RoutedEventArgs e)
        => await _vm.DeleteSelectedAsync();

    private async void OnPingAll(object? sender, RoutedEventArgs e)
        => await _vm.PingAllAsync();

    private async void OnConnect(object? sender, RoutedEventArgs e)
        => await _vm.ConnectAsync(TopLevel.GetTopLevel(this) as Window);
}

public sealed class ServerRow : PropertyChangedBase
{
    private string _detailText = "Még nincs pingelve.";
    private string _pingText = "";
    private IBrush _dotBrush = new SolidColorBrush(Color.Parse("#565A62"));

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }

    public string Address => $"{Host}:{Port}";

    public string DetailText
    {
        get => _detailText;
        set => Raise(ref _detailText, value);
    }

    public string PingText
    {
        get => _pingText;
        set => Raise(ref _pingText, value);
    }

    public IBrush DotBrush
    {
        get => _dotBrush;
        set => Raise(ref _dotBrush, value);
    }
}

public sealed class ServersViewModel : PropertyChangedBase
{
    private readonly LauncherServices _services = LauncherServices.Current;

    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#565A62"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#46A758"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Gold = new SolidColorBrush(Color.Parse("#D4AF37"));

    private ServerRow? _selectedServer;
    private string _statusText = "";
    private string _newName = "";
    private string _newHost = "";
    private double _newPort = 25565;
    private string _addHint = "A lista a globális servers.json-ban tárolódik.";
    private double _progress;
    private bool _progressVisible;
    private bool _busy;

    public ObservableCollection<ServerRow> Servers { get; } = new();

    public ServerRow? SelectedServer
    {
        get => _selectedServer;
        set => Raise(ref _selectedServer, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Raise(ref _statusText, value);
    }

    public string NewName { get => _newName; set => Raise(ref _newName, value); }
    public string NewHost { get => _newHost; set => Raise(ref _newHost, value); }
    public double NewPort { get => _newPort; set => Raise(ref _newPort, value); }
    public string AddHint { get => _addHint; private set => Raise(ref _addHint, value); }
    public double Progress { get => _progress; private set => Raise(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; private set => Raise(ref _progressVisible, value); }
    public bool IsEmpty => Servers.Count == 0;

    public ServersViewModel()
    {
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _services.LoadSettingsAsync();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Betöltési hiba: " + ex.Message;
            LauncherLogger.Error("Szerver lista init hiba: " + ex.Message);
        }
    }

    private async Task ReloadAsync()
    {
        Servers.Clear();
        SelectedServer = null;
        try
        {
            var list = await _services.Servers.LoadAsync();
            foreach (var s in list.OrderBy(x => x.Name))
            {
                Servers.Add(new ServerRow
                {
                    Id = s.Id,
                    Name = s.Name,
                    Host = s.Host,
                    Port = s.Port
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = "Lista hiba: " + ex.Message;
            LauncherLogger.Error("Szerver lista hiba: " + ex.Message);
        }
    }

    public async Task AddAsync()
    {
        try
        {
            var added = await _services.Servers.AddAsync(
                NewName.Trim(), NewHost.Trim(), (int)Math.Clamp(NewPort, 1, 65535));
            NewName = "";
            NewHost = "";
            NewPort = 25565;
            AddHint = $"Hozzáadva: {added.Name}.";
            await ReloadAsync();
            SelectedServer = Servers.FirstOrDefault(s => s.Id == added.Id);
        }
        catch (Exception ex)
        {
            AddHint = "Hiba: " + ex.Message;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var row = SelectedServer;
        if (row is null)
        {
            StatusText = "Válassz ki egy szervert a törléshez.";
            return;
        }
        try
        {
            await _services.Servers.RemoveAsync(row.Id);
            StatusText = $"Törölve: {row.Name}";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Törlési hiba: " + ex.Message;
            LauncherLogger.Error("Szerver törlés hiba: " + ex.Message);
        }
    }

    public async Task PingAllAsync()
    {
        if (_busy || Servers.Count == 0) return;
        _busy = true;
        StatusText = "Szerverek pingelése…";
        try
        {
            await Task.WhenAll(Servers.Select(PingOneAsync));
            var online = Servers.Count(s => s.DotBrush == Green);
            StatusText = $"{online}/{Servers.Count} szerver elérhető.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PingOneAsync(ServerRow row)
    {
        row.DotBrush = Gold;
        row.DetailText = "Pingelés…";
        row.PingText = "";
        try
        {
            var status = await _services.ServerStatus.CheckServerStatusAsync(row.Host, row.Port);
            if (status.IsOnline)
            {
                row.DotBrush = Green;
                var players = status is { OnlinePlayers: not null, MaxPlayers: not null }
                    ? $"{status.OnlinePlayers}/{status.MaxPlayers} játékos"
                    : "online";
                var motd = string.IsNullOrWhiteSpace(status.Motd) ? "" : " · " + Strip(status.Motd);
                row.DetailText = $"{players}{motd}";
                if (!string.IsNullOrWhiteSpace(status.Version))
                    row.DetailText += $" ({status.Version})";
                row.PingText = $"{status.ResponseTimeMs} ms";
            }
            else
            {
                row.DotBrush = Red;
                row.DetailText = string.IsNullOrWhiteSpace(status.ErrorMessage)
                    ? "Nem elérhető."
                    : status.ErrorMessage;
                row.PingText = "";
            }
        }
        catch (Exception ex)
        {
            row.DotBrush = Red;
            row.DetailText = "Ping hiba: " + ex.Message;
            row.PingText = "";
        }
    }

    public async Task ConnectAsync(Window? owner)
    {
        var row = SelectedServer;
        if (row is null)
        {
            StatusText = "Válassz ki egy szervert a csatlakozáshoz.";
            return;
        }
        if (_busy) return;

        // Nouveau-figyelmeztetés: rákérdez, de a csatlakozást engedi.
        if (!await NouveauWarning.EnsureAcknowledgedAsync(owner))
        {
            StatusText = "Csatlakozás megszakítva a felhasználó kérésére.";
            return;
        }

        _busy = true;
        ProgressVisible = true;

        LauncherInstance? launched = null;
        try
        {
            var (activeId, list) = _services.Instances.Load();
            var instance = list.FirstOrDefault(x => x.Id == activeId)
                ?? list.OrderByDescending(x => x.LastUsed).FirstOrDefault();
            if (instance is null)
            {
                StatusText = "Nincs Instance — hozz létre egyet a Játék lapon.";
                return;
            }

            var progress = new Progress<LaunchProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = p.Text;
                    Progress = p.Percent;
                }));

            var message = await _services.LaunchInstanceAsync(
                instance, progress, row.Host, row.Port);

            // LastUsed frissítése a lemezen is.
            var (_, fresh) = _services.Instances.Load();
            var target = fresh.FirstOrDefault(x => x.Id == instance.Id);
            if (target is not null)
            {
                target.LastUsed = instance.LastUsed;
                _services.Instances.Save(fresh, instance.Id);
            }

            StatusText = message;
            launched = instance;
        }
        catch (Exception ex)
        {
            LauncherLogger.Error("Szerver csatlakozás sikertelen: " + ex);
            StatusText = "Indítási hiba: " + ex.Message;
        }
        finally
        {
            _busy = false;
            ProgressVisible = false;
            Progress = 0;
        }

        // Betöltőfigyelő a csatlakozáshoz is (csak sikeres indításkor).
        if (launched is not null)
        {
            GameLoading.Show(owner, launched.Name,
                $"{row.Host}:{row.Port} · MC {launched.MinecraftVersion}",
                _services.CurrentLauncher,
                _services.Settings.GameStartBehavior);
        }
    }

    private static string Strip(string motd)
    {
        // A MOTD §-kódjait és sortöréseit levágjuk a kompakt kijelzéshez.
        var sb = new System.Text.StringBuilder(motd.Length);
        for (var i = 0; i < motd.Length; i++)
        {
            if (motd[i] == '§' && i + 1 < motd.Length) { i++; continue; }
            sb.Append(motd[i] is '\n' or '\r' ? ' ' : motd[i]);
        }
        var s = sb.ToString().Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
    }
}
