using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace TurulMC.Launcher;

/// <summary>
/// Betöltőképernyő a Minecraft ablaka helyett: amíg a Java processz tölt
/// (sok moddal akár percekig), a játékos látja, hol tart a folyamat: fázislista,
/// élő napló és hasznos tippek.
/// </summary>
public sealed partial class GameLoadingWindow : Window
{
    private readonly Queue<string> _lines = new();
    private const int MaxLines = 60;
    private bool _finished;
    private int _stage = -1;
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private int _tipIndex;

    private static readonly string[] Tips =
    {
        "A sok mod lassíthatja az első betöltést — a következő indítás már gyorsabb.",
        "Ha a játék nem indul, nyomd meg a launcherben a Smart Repair gombot: ellenőrzi a Minecraft fájlokat, a Fabric Loadert és a modverziókat.",
        "Több RAM-ot a Beállításokban vagy az Instance szerkesztőjében adhatsz (4–8 GB általában elég).",
        "A Doctor (Játék oldal) végigvizsgálja a Javát, a hálózatot és a mappákat, és ahol lehet, javít is.",
        "Mod-ütközésnél a Modok oldal „Kompatibilitás” gombja megmutatja, melyik mod nem való a verziódhoz.",
        "Az Instance-eket a Játék oldalon tudod másolni: a launcher jelzi, ha a két verzió eltér, és felajánlja a modok frissítését.",
        "Az automatikus Java telepítés gondoskodik róla, hogy minden Minecraft-verzióhoz legyen megfelelő Java.",
        "Ha a játék összeomlik, a launcher Hibamagyarázója megmondja, mi volt a baj."
    };

    public GameLoadingWindow(string instanceName, string? themeName = null, string? detail = null)
    {
        InitializeComponent();
        TitleText.Text = $"Minecraft betöltése… ({instanceName})";
        if (!string.IsNullOrWhiteSpace(detail)) DetailText.Text = detail;
        ApplyLogo(themeName ?? "yellow");
        try { LogoPulse.Begin(); } catch { }

        TipText.Text = Tips[0];
        try
        {
            _tipTimer.Tick += (_, _) =>
            {
                _tipIndex = (_tipIndex + 1) % Tips.Length;
                TipText.Text = Tips[_tipIndex];
            };
            _tipTimer.Start();
        }
        catch { }

        SetStage(0);
    }

    private void ApplyLogo(string themeName)
    {
        // Ugyanazok a téma-logók, mint a splash-en; ismeretlen témánál az alap.
        var key = (themeName ?? "").ToLowerInvariant();
        var (file, accent) = key switch
        {
            "green" => ("turul-logo-green.png", "#38D996"),
            "red" => ("turul-logo-red.png", "#FF5D6C"),
            "purple" => ("turul-logo-purple.png", "#A970FF"),
            "sky" => ("turul-logo-sky.png", "#56C7FF"),
            _ => ("turul-logo-yellow.png", "#D4AF37"),
        };
        try { GlowStop.Color = Tinted(accent, 0x33); } catch { }
        try { LogoRing.Foreground = new SolidColorBrush(ColorFromHex(accent)); } catch { }
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Themes", file);
            if (!System.IO.File.Exists(path))
                path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "turul-logo.png");
            if (System.IO.File.Exists(path))
                LogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("file:///" + path.Replace('\\', '/')));
        }
        catch { }
    }

    private static Windows.UI.Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return Microsoft.UI.Colors.White;
        return Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static Windows.UI.Color Tinted(string hex, byte alpha)
    {
        var c = ColorFromHex(hex);
        c.A = alpha;
        return c;
    }

    /// <summary>
    /// Fázis kiemelése: 0 = Java, 1 = Minecraft/Fabric fájlok, 2 = játék indítása,
    /// 3 = játékablak. A korábbi fázisok késznek számítanak.
    /// </summary>
    public void SetStage(int stage)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetStage(stage));
            return;
        }

        var target = Math.Clamp(stage, 0, 3);
        if (target <= _stage && _stage >= 0) return;
        _stage = target;

        var accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD4, 0xAF, 0x37));
        var done = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x38, 0xD9, 0x96));
        var idle = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2A, 0x32, 0x3C));
        var activeText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE7, 0xEB, 0xF0));
        var idleText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0x95, 0xA1));

        var dots = new[] { Stage1Dot, Stage2Dot, Stage3Dot, Stage4Dot };
        var labels = new[] { Stage1Text, Stage2Text, Stage3Text, Stage4Text };
        for (var i = 0; i < dots.Length; i++)
        {
            if (i < target)
            {
                dots[i].Fill = done;
                labels[i].Foreground = idleText;
            }
            else if (i == target)
            {
                dots[i].Fill = accent;
                labels[i].Foreground = activeText;
                labels[i].FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                dots[i].Fill = idle;
                labels[i].Foreground = idleText;
            }
        }
    }

    public void UpdateStatus(string status)
    {
        if (DispatcherQueue.HasThreadAccess) StatusText.Text = status;
        else DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    public void AppendLog(string line)
    {
        if (DispatcherQueue.HasThreadAccess) AppendLogCore(line);
        else DispatcherQueue.TryEnqueue(() => AppendLogCore(line));
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        var text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        if (DispatcherQueue.HasThreadAccess) ElapsedText.Text = text;
        else DispatcherQueue.TryEnqueue(() => ElapsedText.Text = text);
    }

    public void MarkLoaded()
    {
        if (_finished) return;
        _finished = true;
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(MarkLoaded); return; }
        try { _tipTimer.Stop(); } catch { }
        LoadingRingStop();
        SetStage(3);
        TitleText.Text = "Minecraft betöltve!";
        StatusText.Text = "Játékablak észlelve — jó játékot!";
        TipText.Text = "A játék elindult. A launchert a tálcán is megtalálod, ha elrejtetted.";
    }

    public void MarkFailed(string error)
    {
        if (_finished) return;
        _finished = true;
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => MarkFailed(error)); return; }
        try { _tipTimer.Stop(); } catch { }
        LoadingRingStop();
        TitleText.Text = "A Minecraft leállt betöltés közben";
        StatusText.Text = string.IsNullOrWhiteSpace(error) ? "Ismeretlen hiba." : error.Trim();
        TipText.Text = "Nyisd meg a launcher Hibamagyarázóját, vagy futtass Smart Repairt — a napló itt fent marad.";
    }

    private void LoadingRingStop()
    {
        try { LogoRing.IsActive = false; } catch { }
    }

    private void AppendLogCore(string line)
    {
        line = TurulMC.Core.Minecraft.GameLoadingStatus.TrimForLog(line);
        if (string.IsNullOrWhiteSpace(line)) return;
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines) _lines.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, _lines);
        try { LogCountText.Text = $"{_lines.Count} sor"; } catch { }
        try { LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true); } catch { }
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        try { _tipTimer.Stop(); } catch { }
        try { Close(); } catch { }
    }
}
