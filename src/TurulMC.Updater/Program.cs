using System.Diagnostics;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TurulMC.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            var parsed = ParseArgs(args);
            var preview = parsed.ContainsKey("preview");
            var options = preview
                ? UpdaterOptions.CreatePreview()
                : new UpdaterOptions(
                    int.Parse(Require(parsed, "pid")),
                    Require(parsed, "url"),
                    Require(parsed, "sha256"),
                    Path.GetFullPath(Require(parsed, "install-dir")),
                    Path.GetFullPath(Require(parsed, "launcher")),
                    parsed.GetValueOrDefault("version", "unknown"),
                    false);

            if (!preview && !IsAllowedUpdateUrl(options.Url))
                throw new InvalidOperationException("Az update URL nem engedélyezett.");
            if (!preview && !System.Text.RegularExpressions.Regex.IsMatch(options.Sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidOperationException("Érvénytelen SHA-256 hash.");

            Application.Run(new UpdaterForm(options));
            return 0;
        }
        catch (Exception ex)
        {
            WriteEmergencyLog(ex);
            MessageBox.Show($"A TurulLauncher updater nem indítható el.\n\n{ex.Message}",
                "TurulLauncher Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                result[key] = args[++i];
            else
                result[key] = "true";
        }
        return result;
    }

    private static string Require(Dictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}.");

    internal static bool IsAllowedUpdateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return uri.Host.Equals("turulnetwork.hu", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".turulnetwork.hu", StringComparison.OrdinalIgnoreCase);
    }

    internal static void WriteEmergencyLog(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurulMC", "updates");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "updater-error.log"), $"[{DateTimeOffset.Now:O}] {ex}\n\n");
        }
        catch { }
    }
}

internal sealed record UpdaterOptions(int Pid, string Url, string Sha256, string InstallDir, string Launcher, string Version, bool Preview)
{
    public static UpdaterOptions CreatePreview() => new(0, "https://turulnetwork.hu/launcher/releases/preview.zip", new string('0', 64), "", "", "4.4.5 PREVIEW", true);
}

internal sealed class UpdaterForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private readonly UpdaterOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<string> _logs = new();
    private volatile bool _paused;
    private volatile bool _finished;
    private bool _logVisible;
    private readonly Image _logo;
    private readonly Panel _titleBar = new();
    private readonly Label _title = new();
    private readonly Label _updater = new();
    private readonly PictureBox _titleLogo = new();
    private readonly FlatWindowButton _minButton = new("—");
    private readonly FlatWindowButton _closeButton = new("×");
    private readonly SpinnerControl _spinner = new();
    private readonly Label _status = new();
    private readonly Label _subStatus = new();
    private readonly SmoothProgressBar _progress = new();
    private readonly Label _percent = new();
    private readonly Label _stats = new();
    private readonly Panel _bottomBar = new();
    private readonly Button _pauseResume = new();
    private readonly Button _cancel = new();
    private readonly Button _logButton = new();
    private readonly Panel _logPanel = new();
    private readonly RichTextBox _logBox = new();
    private readonly Label _logHeader = new();
    private readonly StepStrip _steps = new();
    private readonly Label _versionChip = new();
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 220 };
    private int _previewPct = 68;

    // A launcher témájához igazodó színek (a régi kék helyett). A tényleges accentet a
    // launcher settings.json-jából olvassuk, hogy az updater ugyanolyan színű legyen.
    private readonly Color Bg = Color.FromArgb(9, 13, 18);
    private readonly Color PanelBg = Color.FromArgb(12, 17, 23);
    private readonly Color Border = Color.FromArgb(48, 59, 72);
    private readonly Color TextMain = Color.FromArgb(237, 242, 248);
    private readonly Color TextMuted = Color.FromArgb(140, 154, 173);
    private Color Gold = Color.FromArgb(212, 175, 55);
    private Color GoldDark = Color.FromArgb(184, 150, 31);
    private Color GoldSoft = Color.FromArgb(68, 54, 19);

    /// <summary>A launcher témaszínei — ugyanazok, mint a splash képernyőn.</summary>
    private static (string Accent, string Dark, string Soft) ResolveTheme(string themeName) =>
        (themeName ?? "").Trim().ToLowerInvariant() switch
        {
            "green" => ("#38D996", "#20B879", "#174435"),
            "red" => ("#FF5D6C", "#D94453", "#4B2028"),
            "purple" => ("#A970FF", "#8651DB", "#35234F"),
            "sky" => ("#56C7FF", "#2CA4E1", "#183C4E"),
            _ => ("#D4AF37", "#B8961F", "#443613")
        };

    private static string ReadConfiguredTheme()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurulMC", "settings.json");
            if (!File.Exists(path)) return "yellow";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var name in new[] { "theme", "Theme" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return value.GetString() ?? "yellow";
            }
        }
        catch { }
        return "yellow";
    }

    private static Color FromHex(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6) return Color.FromArgb(212, 175, 55);
        return Color.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    public UpdaterForm(UpdaterOptions options)
    {
        _options = options;
        _logo = LoadEmbeddedLogo();

        // A launcher beállított témáját követjük (a splash/launcher ugyanazt használja).
        var theme = ResolveTheme(ReadConfiguredTheme());
        Gold = FromHex(theme.Accent);
        GoldDark = FromHex(theme.Dark);
        GoldSoft = FromHex(theme.Soft);

        Text = "TurulLauncher UPDATER";
        Icon = TryLoadEmbeddedIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextMain;
        ClientSize = new Size(760, 500);
        MinimumSize = new Size(620, 420);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;

        BuildUi();
        Shown += OnShown;
        Resize += (_, _) => LayoutUi();
        FormClosing += OnFormClosing;
        Paint += PaintBackground;
    }

    private void BuildUi()
    {
        _titleBar.Height = 52;
        _titleBar.Dock = DockStyle.Top;
        _titleBar.BackColor = Color.FromArgb(13, 18, 24);
        _titleBar.MouseDown += DragWindow;
        Controls.Add(_titleBar);

        _titleLogo.SizeMode = PictureBoxSizeMode.Zoom;
        _titleLogo.Image = _logo;
        _titleLogo.BackColor = Color.Transparent;
        _titleLogo.MouseDown += DragWindow;
        _titleBar.Controls.Add(_titleLogo);

        _title.Text = "TurulLauncher";
        _title.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        _title.ForeColor = TextMain;
        _title.AutoSize = true;
        _title.MouseDown += DragWindow;
        _titleBar.Controls.Add(_title);

        _updater.Text = "U P D A T E R";
        _updater.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        _updater.ForeColor = Color.FromArgb(158, 181, 211);
        _updater.AutoSize = true;
        _updater.MouseDown += DragWindow;
        _titleBar.Controls.Add(_updater);

        _minButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _closeButton.IsClose = true;
        _closeButton.Click += (_, _) => RequestCancelOrClose();
        _titleBar.Controls.Add(_minButton);
        _titleBar.Controls.Add(_closeButton);

        _spinner.Logo = _logo;
        _spinner.BackColor = Color.Transparent;
        Controls.Add(_spinner);

        ConfigureCenterLabel(_status, 16f, FontStyle.Bold, TextMain);
        ConfigureCenterLabel(_subStatus, 10f, FontStyle.Regular, TextMuted);
        ConfigureCenterLabel(_percent, 10f, FontStyle.Bold, Color.FromArgb(202, 217, 235));
        ConfigureCenterLabel(_stats, 9f, FontStyle.Regular, TextMuted);
        Controls.Add(_status);
        Controls.Add(_subStatus);
        Controls.Add(_progress);
        Controls.Add(_percent);
        Controls.Add(_stats);

        _bottomBar.Height = 62;
        _bottomBar.Dock = DockStyle.Bottom;
        _bottomBar.BackColor = Color.FromArgb(11, 16, 21);
        _bottomBar.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Border), 0, 0, _bottomBar.Width, 0);
        Controls.Add(_bottomBar);

        ConfigureButton(_pauseResume, "Pause", primary: true);
        ConfigureButton(_cancel, "Cancel", primary: false);
        ConfigureButton(_logButton, "Log", primary: false);
        _pauseResume.Click += (_, _) => TogglePause();
        _cancel.Click += (_, _) => RequestCancelOrClose();
        _logButton.Click += (_, _) => ToggleLog();
        _bottomBar.Controls.Add(_pauseResume);
        _bottomBar.Controls.Add(_cancel);
        _bottomBar.Controls.Add(_logButton);

        _logPanel.BackColor = Color.FromArgb(7, 10, 14);
        _logPanel.Visible = false;
        Controls.Add(_logPanel);

        _logHeader.Dock = DockStyle.Top;
        _logHeader.Height = 30;
        _logHeader.Text = "  NAPLÓ";
        _logHeader.TextAlign = ContentAlignment.MiddleLeft;
        _logHeader.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _logHeader.ForeColor = Color.FromArgb(160, 170, 184);
        _logHeader.BackColor = Color.FromArgb(11, 15, 20);
        _logHeader.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Border), 0, _logHeader.Height - 1, _logHeader.Width, _logHeader.Height - 1);
        _logPanel.Controls.Add(_logHeader);

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.BackColor = Color.FromArgb(7, 10, 14);
        _logBox.ForeColor = Color.FromArgb(166, 183, 205);
        _logBox.Font = new Font("Cascadia Mono", 8.5f, FontStyle.Regular);
        _logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _logPanel.Controls.Add(_logBox);
        _logBox.BringToFront();

        // Fázisjelző: Letöltés → Ellenőrzés → Telepítés → Kész
        _steps.BackColor = Color.Transparent;
        _steps.Stages = new[] { "Letöltés", "Ellenőrzés", "Telepítés", "Kész" };
        _steps.Accent = Gold;
        _steps.AccentSoft = GoldSoft;
        Controls.Add(_steps);

        _spinner.Accent = Gold;
        _spinner.AccentSoft = GoldDark;
        _progress.AccentDark = GoldDark;
        _progress.AccentLight = Lighten(Gold, 0.35f);

        _versionChip.AutoSize = false;
        _versionChip.TextAlign = ContentAlignment.MiddleCenter;
        _versionChip.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _versionChip.ForeColor = Gold;
        _versionChip.BackColor = Color.FromArgb(20, 18, 10);
        _versionChip.Text = "→ " + _options.Version;
        _versionChip.MouseDown += DragWindow;
        _titleBar.Controls.Add(_versionChip);

        _previewTimer.Tick += (_, _) =>
        {
            if (_paused) return;
            _previewPct = _previewPct >= 96 ? 42 : _previewPct + 1;
            UpdateProgress(_previewPct, $"{_previewPct * 12} MB / 1.20 GB  •  24.6 MB/s  •  ~{Math.Max(1, 34 - _previewPct / 3)} seconds left");
        };

        LayoutUi();
    }

    private static Color Lighten(Color color, float amount)
        => Color.FromArgb(color.A,
            Math.Min(255, (int)(color.R + (255 - color.R) * amount)),
            Math.Min(255, (int)(color.G + (255 - color.G) * amount)),
            Math.Min(255, (int)(color.B + (255 - color.B) * amount)));

    private static void ConfigureCenterLabel(Label label, float size, FontStyle style, Color color)
    {
        label.Font = new Font("Segoe UI", size, style);
        label.ForeColor = color;
        label.BackColor = Color.Transparent;
        label.TextAlign = ContentAlignment.MiddleCenter;
        label.AutoEllipsis = true;
    }

    private void ConfigureButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(212, 175, 55) : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(120, 98, 26) : Color.FromArgb(24, 31, 40);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(96, 78, 20) : Color.FromArgb(18, 24, 31);
        button.BackColor = primary ? Color.FromArgb(58, 48, 16) : Color.FromArgb(15, 21, 28);
        button.ForeColor = TextMain;
        button.Font = new Font("Segoe UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
    }

    private void LayoutUi()
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        var bottomH = _bottomBar.Height;
        var logH = _logVisible ? Math.Min(170, Math.Max(110, h / 4)) : 0;
        var mainBottom = h - bottomH - logH;

        _titleLogo.SetBounds(18, 14, 26, 26);
        _title.Location = new Point(54, 15);
        _updater.Location = new Point(_title.Right + 12, 19);
        _versionChip.SetBounds(_updater.Right + 12, 16, 86, 22);
        _closeButton.SetBounds(w - 48, 0, 48, 52);
        _minButton.SetBounds(w - 96, 0, 48, 52);

        var cx = w / 2;
        var spinnerSize = Math.Clamp((mainBottom - 120) / 4, 82, 108);
        var centerY = 74 + Math.Max(0, (mainBottom - 74 - 300) / 2);
        _spinner.SetBounds(cx - spinnerSize / 2, centerY, spinnerSize, spinnerSize);
        _status.SetBounds(60, _spinner.Bottom + 12, w - 120, 32);
        _subStatus.SetBounds(60, _status.Bottom + 1, w - 120, 24);

        var stepsW = Math.Min(520, w - 120);
        _steps.SetBounds(cx - stepsW / 2, _subStatus.Bottom + 8, stepsW, 46);

        var barW = Math.Min(540, w - 150);
        var barX = cx - barW / 2 - 18;
        _progress.SetBounds(barX, _steps.Bottom + 12, barW, 14);
        _percent.SetBounds(_progress.Right + 12, _progress.Top - 6, 58, 26);
        _stats.SetBounds(60, _progress.Bottom + 11, w - 120, 24);

        var bw = 104; var gap = 10; var right = w - 18;
        _logButton.SetBounds(right - bw, 14, bw, 36); right -= bw + gap;
        _cancel.SetBounds(right - bw, 14, bw, 36); right -= bw + gap;
        _pauseResume.SetBounds(right - 122, 14, 122, 36);

        _logPanel.SetBounds(0, mainBottom, w, logH);
        _logPanel.Visible = _logVisible;
        Invalidate();
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        FitToWorkingArea();
        if (_options.Preview)
        {
            _paused = true;
            _status.Text = "Downloading update...";
            _subStatus.Text = "Preparing files (41 / 60)";
            UpdateProgress(68, "812 MB / 1.20 GB  •  24.6 MB/s  •  16 seconds left");
            _pauseResume.Text = "Resume";
            AddLog("PREVIEW MODE — no files will be changed.");
            AddLog("Updater UI loaded at current Windows DPI.");
            AddLog("Use Resume / Cancel / Log to inspect interactive states.");
            return;
        }

        _status.Text = "Preparing update...";
        _subStatus.Text = $"TurulLauncher {_options.Version}";
        AddLog($"Starting update to {_options.Version}.");
        try
        {
            await RunUpdateAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Update cancelled by user.");
            SetStatus("Update cancelled", "Opening the current launcher...", 0);
            await Task.Delay(450);
            await WaitForProcessAsync(_options.Pid, CancellationToken.None);
            StartLauncher();
            CloseSafely();
        }
        catch (Exception ex)
        {
            Program.WriteEmergencyLog(ex);
            AddLog("ERROR: " + ex);
            SetStatus("Update failed", ex.Message, _progress.Value);
            _pauseResume.Enabled = false;
            _cancel.Text = "Close";
            _finished = true;
            ShowLogOnError();
        }
    }

    private async Task RunUpdateAsync()
    {
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurulMC", "updates", _options.Version);
        Directory.CreateDirectory(updateRoot);
        var partPath = Path.Combine(updateRoot, $"TurulLauncher-{_options.Version}.zip.part");
        var packagePath = Path.Combine(updateRoot, $"TurulLauncher-{_options.Version}.zip");

        await DownloadPackageAsync(partPath, packagePath, _cts.Token);
        SetStatus("Verifying update...", "Checking SHA-256", 100);
        AddLog("Verifying SHA-256...");
        await using (var stream = File.OpenRead(packagePath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, _cts.Token)).ToLowerInvariant();
            if (!hash.Equals(_options.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                throw new InvalidDataException("SHA-256 verification failed. Downloaded package was deleted.");
            }
        }
        AddLog("SHA-256 OK.");

        SetStatus("Installing update...", "Waiting for TurulLauncher to close", 0);
        await WaitForProcessAsync(_options.Pid, _cts.Token);
        await InstallPackageAsync(packagePath, _cts.Token);

        File.WriteAllText(Path.Combine(_options.InstallDir, "update-success.txt"),
            $"TurulLauncher {_options.Version} installed at {DateTimeOffset.Now:O}");
        SetStatus("Update complete", $"TurulLauncher {_options.Version} is ready", 100);
        AddLog("Update installed successfully.");
        _finished = true;
        _pauseResume.Enabled = false;
        _cancel.Enabled = false;
        await Task.Delay(700);
        StartLauncher();
        CloseSafely();
    }

    private async Task DownloadPackageAsync(string partPath, string finalPath, CancellationToken token)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        var downloaded = existing;

        // Keep every handle that may touch the .part file inside this scope.  In the
        // previous implementation File.Move ran while the output FileStream was still
        // alive because `await using var` disposes at the end of the method scope.
        // Windows therefore correctly rejected the rename with ERROR_SHARING_VIOLATION.
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            AddLog(existing > 0 ? $"Resuming download at {FormatBytes(existing)}." : "Starting download.");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                AddLog("Server did not accept Range; restarting download from 0.");
                existing = 0;
                downloaded = 0;
                if (File.Exists(partPath)) File.Delete(partPath);
            }
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentRange?.Length
                        ?? ((response.Content.Headers.ContentLength ?? 0) + existing);
            if (total <= 0) total = 1;

            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                var sw = Stopwatch.StartNew();
                var lastBytes = downloaded;
                var lastTick = sw.Elapsed;

                while (true)
                {
                    await WaitWhilePausedAsync(token);
                    var read = await input.ReadAsync(buffer, token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    downloaded += read;

                    var now = sw.Elapsed;
                    if ((now - lastTick).TotalMilliseconds >= 220 || downloaded == total)
                    {
                        var deltaSec = Math.Max(.001, (now - lastTick).TotalSeconds);
                        var speed = (downloaded - lastBytes) / deltaSec;
                        var remain = Math.Max(0, total - downloaded);
                        var eta = speed > 1 ? TimeSpan.FromSeconds(remain / speed) : TimeSpan.Zero;
                        var pct = (int)Math.Clamp(downloaded * 100L / total, 0, 100);
                        var stat = $"{FormatBytes(downloaded)} / {FormatBytes(total)}  •  {FormatBytes((long)speed)}/s  •  {FormatEta(eta)}";
                        SetStatus("Downloading update...", $"TurulLauncher {_options.Version}", pct, stat);
                        lastBytes = downloaded;
                        lastTick = now;
                    }
                }

                await output.FlushAsync(token);
            }
        }

        // All download streams are disposed now.  A very short retry loop also handles
        // transient locks caused by Defender/indexers without hiding persistent errors.
        Exception? lastMoveError = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(partPath, finalPath, true);
                lastMoveError = null;
                break;
            }
            catch (IOException ex) when (attempt < 6)
            {
                lastMoveError = ex;
                AddLog($"Package finalize busy; retry {attempt}/5...");
                await Task.Delay(150 * attempt, token);
            }
        }

        if (lastMoveError is not null)
            throw new IOException("The update package could not be finalized after the download completed.", lastMoveError);

        AddLog($"Download complete: {FormatBytes(downloaded)}.");
    }

    private async Task InstallPackageAsync(string packagePath, CancellationToken token)
    {
        if (!Directory.Exists(_options.InstallDir)) throw new DirectoryNotFoundException(_options.InstallDir);

        // FONTOS: nem a %TEMP%-et használjuk. A felhasználói Temp mappa lehet írásvédett,
        // tele, vagy egy vírusirtó blokkolhatja (ez okozta a
        // "Access to the path '...\Temp\<random>' is denied" hibát), ráadásul a
        // single-file .NET hoszt is oda csomagol ki. Helyette a launcher saját,
        // garantáltan írható adatkönyvtárában dolgozunk.
        var workBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurulMC", "updates", "work");
        var workRoot = CreateWorkDirectory(workBase);
        var staging = Path.Combine(workRoot, "staging");
        var backup = Path.Combine(workRoot, "backup");
        Directory.CreateDirectory(staging); Directory.CreateDirectory(backup);
        var written = new List<string>();
        try
        {
            SetStatus("Installing update...", "Extracting package", 4);
            ExtractSafely(packagePath, staging);
            var payloadRoot = ResolvePayloadRoot(staging);
            var files = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories).ToArray();
            for (var i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(token);
                var source = files[i];
                var rel = Path.GetRelativePath(payloadRoot, source);
                if (rel.StartsWith("..", StringComparison.Ordinal)) throw new InvalidDataException("Invalid update path.");
                var destination = Path.GetFullPath(Path.Combine(_options.InstallDir, rel));
                var installRoot = _options.InstallDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update attempted path traversal.");
                var backupFile = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(destination, backupFile, true);
                }
                CopyWithRetry(source, destination);
                written.Add(rel);
                var pct = 8 + (int)((i + 1) * 91.0 / Math.Max(1, files.Length));
                SetStatus("Installing update...", $"Preparing files ({i + 1} / {files.Length})", pct,
                    $"{i + 1} / {files.Length} files  •  rollback protected");
            }
        }
        catch
        {
            AddLog("Install interrupted; rolling back changed files.");
            Rollback(_options.InstallDir, backup, written);
            throw;
        }
        finally { TryDelete(workRoot); }
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        while (_paused)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(90, token);
        }
    }

    private static async Task WaitForProcessAsync(int pid, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(token);
        }
        catch (ArgumentException) { }
    }

    private void TogglePause()
    {
        if (_finished) return;
        _paused = !_paused;
        _pauseResume.Text = _paused ? "Resume" : "Pause";
        AddLog(_paused ? "Paused by user." : "Resumed by user.");
        if (_options.Preview)
        {
            if (_paused) _previewTimer.Stop(); else _previewTimer.Start();
        }
        _spinner.Paused = _paused;
    }

    private void RequestCancelOrClose()
    {
        if (_finished || _options.Preview)
        {
            _previewTimer.Stop();
            Close();
            return;
        }
        if (!_cts.IsCancellationRequested)
        {
            AddLog("Cancellation requested.");
            _cancel.Enabled = false;
            _cts.Cancel();
        }
    }

    private void ToggleLog()
    {
        _logVisible = !_logVisible;
        _logButton.Text = _logVisible ? "Hide Log" : "Log";
        var area = Screen.FromControl(this).WorkingArea;
        var target = _logVisible ? Math.Min(660, area.Height - 36) : Math.Min(500, area.Height - 36);
        Height = Math.Max(MinimumSize.Height, target);
        LayoutUi();
        if (_logVisible) { _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret(); }
    }

    private void ShowLogOnError()
    {
        if (_logVisible) return;
        ToggleLog();
    }

    private void SetStatus(string status, string sub, int pct, string? stat = null)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(status, sub, pct, stat))); return; }
        _status.Text = status; _subStatus.Text = sub; UpdateProgress(pct, stat ?? _stats.Text);
        _steps.Active = status switch
        {
            _ when status.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase) => 0,
            _ when status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) => 0,
            _ when status.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase) => 1,
            _ when status.StartsWith("Installing", StringComparison.OrdinalIgnoreCase) => 2,
            _ when status.StartsWith("Update complete", StringComparison.OrdinalIgnoreCase) => 3,
            _ when status.StartsWith("Update failed", StringComparison.OrdinalIgnoreCase) => -1,
            _ when status.StartsWith("Update cancelled", StringComparison.OrdinalIgnoreCase) => -1,
            _ => _steps.Active
        };
    }

    private void UpdateProgress(int pct, string stat)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => UpdateProgress(pct, stat))); return; }
        pct = Math.Clamp(pct, 0, 100);
        _progress.Value = pct; _percent.Text = pct + "%"; _stats.Text = stat;
    }

    private void AddLog(string line)
    {
        var full = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _logs.Add(full);
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLogLine(full))); return; }
        AppendLogLine(full);
    }

    private void AppendLogLine(string line)
    {
        _logBox.AppendText(line + Environment.NewLine);
        if (_logVisible) { _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret(); }
    }

    private void StartLauncher()
    {
        if (_options.Preview || string.IsNullOrWhiteSpace(_options.Launcher) || !File.Exists(_options.Launcher)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _options.Launcher, WorkingDirectory = _options.InstallDir, UseShellExecute = true }); }
        catch (Exception ex) { AddLog("Could not restart launcher: " + ex.Message); }
    }

    private void CloseSafely()
    {
        if (InvokeRequired) BeginInvoke(new Action(Close)); else Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_finished && !_options.Preview && !_cts.IsCancellationRequested)
        {
            e.Cancel = true;
            RequestCancelOrClose();
        }
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void FitToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var maxW = (int)(area.Width * .90);
        var maxH = (int)(area.Height * .90);
        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;
        MinimumSize = new Size(Math.Min(620, maxW), Math.Min(420, maxH));
        CenterToScreen();
        LayoutUi();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void PaintBackground(object? sender, PaintEventArgs e)
    {
        var contentTop = _titleBar.Height;
        var contentBottom = ClientSize.Height - _bottomBar.Height - (_logVisible ? _logPanel.Height : 0);
        var rect = new Rectangle(0, contentTop, ClientSize.Width, Math.Max(1, contentBottom - contentTop));
        using var brush = new LinearGradientBrush(rect, Color.FromArgb(8, 12, 17), Color.FromArgb(12, 19, 27), 35f);
        e.Graphics.FillRectangle(brush, rect);
        using var ia = new ImageAttributes();
        var cm = new ColorMatrix { Matrix33 = .055f };
        ia.SetColorMatrix(cm);
        var size = Math.Min(320, rect.Height - 30);
        if (size > 80)
        {
            var x = (ClientSize.Width - size) / 2;
            var y = contentTop + (rect.Height - size) / 2 - 22;
            e.Graphics.DrawImage(_logo, new Rectangle(x, y, size, size), 0, 0, _logo.Width, _logo.Height, GraphicsUnit.Pixel, ia);
        }
    }

    private static Image LoadEmbeddedLogo()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("turul-logo.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new Bitmap(1, 1);
        using var stream = asm.GetManifestResourceStream(name)!;
        using var temp = new Bitmap(stream);
        return new Bitmap(temp);
    }

    private static Icon? TryLoadEmbeddedIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { return null; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB"];
        double v = Math.Max(0, bytes); var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero || eta.TotalDays > 1) return "calculating...";
        if (eta.TotalMinutes >= 1) return $"~{Math.Ceiling(eta.TotalMinutes)} min left";
        return $"~{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))} seconds left";
    }

    private static void ExtractSafely(string zipPath, string destinationRoot)
    {
        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe ZIP path: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static string ResolvePayloadRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "TurulMC.Launcher.exe"))) return staging;
        var dirs = Directory.GetDirectories(staging); var files = Directory.GetFiles(staging);
        if (dirs.Length == 1 && files.Length == 0 && File.Exists(Path.Combine(dirs[0], "TurulMC.Launcher.exe"))) return dirs[0];
        throw new InvalidDataException("Update ZIP must contain TurulMC.Launcher.exe at its root (or one top-level folder).");
    }

    private static void CopyWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try { File.Copy(source, destination, true); return; }
            catch (IOException ex) { last = ex; Thread.Sleep(250); }
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(250); }
        }
        throw new IOException($"Could not replace {destination}.", last);
    }

    private static void Rollback(string installDir, string backup, IEnumerable<string> written)
    {
        foreach (var rel in written.Reverse())
        {
            try
            {
                var destination = Path.Combine(installDir, rel); var backupFile = Path.Combine(backup, rel);
                if (File.Exists(backupFile)) CopyWithRetry(backupFile, destination);
                else if (File.Exists(destination)) File.Delete(destination);
            }
            catch { }
        }
    }

    /// <summary>
    /// Munkakönyvtár létrehozása több útvonalon át: ha az egyik hely (pl. jogosultság vagy
    /// vírusirtó miatt) nem írható, a következőt próbáljuk — így nem áll meg a frissítés.
    /// </summary>
    private static string CreateWorkDirectory(string preferredBase)
    {
        var candidates = new List<string>
        {
            preferredBase,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurulMC", "updates", "work")
        };

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(candidate, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex)
            {
                last = ex;
                Program.WriteEmergencyLog(ex);
            }
        }

        throw new IOException(
            "Nem sikerült írható munkakönyvtárat létrehozni a frissítéshez. " +
            "Ellenőrizd, hogy van-e írási jogod a %LOCALAPPDATA%\\TurulMC mappához. " +
            (last?.Message ?? ""), last);
    }

    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}

internal sealed class SpinnerControl : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 28 };
    private float _angle;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Logo { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Paused { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Color.FromArgb(212, 175, 55);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentSoft { get; set; } = Color.FromArgb(184, 150, 31);

    public SpinnerControl()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        _timer.Tick += (_, _) => { if (!Paused) { _angle = (_angle + 5.5f) % 360; Invalidate(); } };
        _timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pad = Math.Max(5, Width / 14);
        var rect = new Rectangle(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);

        // Halvány témaszínű fény a logó mögött.
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(rect);
            using var glowBrush = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(48, Accent),
                SurroundColors = new[] { Color.FromArgb(0, Accent) }
            };
            e.Graphics.FillPath(glowBrush, glowPath);
        }

        using var basePen = new Pen(Color.FromArgb(62, AccentSoft), Math.Max(3, Width / 32f));
        e.Graphics.DrawArc(basePen, rect, 0, 360);
        using var glow = new Pen(Accent, Math.Max(4, Width / 24f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawArc(glow, rect, _angle, 96);
        using var tail = new Pen(Color.FromArgb(120, Accent), Math.Max(3, Width / 34f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawArc(tail, rect, _angle + 96, 42);
        if (Logo is not null)
        {
            var s = Width / 3;
            e.Graphics.DrawImage(Logo, new Rectangle((Width - s) / 2, (Height - s) / 2, s, s));
        }
    }
}

/// <summary>
/// Fázisjelző csík (Letöltés → Ellenőrzés → Telepítés → Kész): a kész fázisok
/// kipipálva, az aktuális arannyal kiemelve. -1 = hiba/megszakítás.
/// </summary>
internal sealed class StepStrip : Control
{
    private int _active;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Stages { get; set; } = Array.Empty<string>();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Color.FromArgb(212, 175, 55);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentSoft { get; set; } = Color.FromArgb(68, 54, 19);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; Invalidate(); }
    }

    public StepStrip()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Stages.Length == 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var chipW = Math.Max(84, (Width - 8 * (Stages.Length - 1)) / Stages.Length);
        var gap = (Width - chipW * Stages.Length) / Math.Max(1, Stages.Length - 1);
        var h = 26;
        var y = 10;
        var done = Color.FromArgb(40, 52, 44);
        var doneText = Color.FromArgb(120, 216, 165);
        var idle = Color.FromArgb(20, 26, 33);
        var idleText = Color.FromArgb(120, 130, 143);

        for (var i = 0; i < Stages.Length; i++)
        {
            var x = i * (chipW + gap);
            var rect = new Rectangle(x, y, chipW, h);
            var isActive = i == _active;
            var isDone = _active >= 0 && i < _active;
            var failed = _active < 0 && i == 0;

            using (var path = Rounded(rect, h / 2))
            using (var brush = new SolidBrush(isActive ? AccentSoft : isDone ? done : idle))
            using (var pen = new Pen(isActive ? Accent : failed ? Color.FromArgb(182, 80, 80) : Color.FromArgb(34, 42, 52)))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            var label = isDone ? Stages[i] + " ✓" : Stages[i];
            var color = isActive ? Accent : isDone ? doneText : idleText;
            using var font = new Font("Segoe UI", 8.5f, isActive ? FontStyle.Bold : FontStyle.Regular);
            using var textBrush = new SolidBrush(color);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(label, font, textBrush, rect, format);

            // Összekötő vonal a következő fázis felé.
            if (i < Stages.Length - 1)
            {
                var lineY = y + h / 2;
                using var pen = new Pen(isDone ? Color.FromArgb(70, 120, 96) : Color.FromArgb(30, 37, 46), 1.5f);
                e.Graphics.DrawLine(pen, rect.Right + 2, lineY, rect.Right + gap - 2, lineY);
            }
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }
}

internal sealed class SmoothProgressBar : Control
{
    private int _value;
    private float _shimmer;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentDark { get; set; } = Color.FromArgb(184, 150, 31);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentLight { get; set; } = Color.FromArgb(240, 208, 96);

    public SmoothProgressBar()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true; BackColor = Color.Transparent;
        // Finom, folyamatosan futó fény a sávon: látszik, hogy "dolgozik".
        _timer.Tick += (_, _) => { _shimmer = (_shimmer + 0.035f) % 1f; if (Visible && _value > 0) Invalidate(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var h = Math.Max(8, Height - 2); var y = (Height - h) / 2;
        var track = new Rectangle(0, y, Width - 1, h);
        using (var path = Rounded(track, h / 2))
        using (var b = new SolidBrush(Color.FromArgb(26, 33, 42))) e.Graphics.FillPath(b, path);

        var fillW = (int)((Width - 1) * (_value / 100.0));
        if (fillW > 1)
        {
            var fill = new Rectangle(0, y, fillW, h);
            using var path = Rounded(fill, h / 2);
            using var b = new LinearGradientBrush(fill, AccentDark, AccentLight, 0f);
            e.Graphics.FillPath(b, path);

            // Mozgó fénycsík a kész részen (a régión belül marad, ezért nem lóg ki).
            if (fillW > 60)
            {
                var glowW = Math.Min(120, Math.Max(40, fillW / 4));
                var travel = fillW - glowW;
                if (travel > 0)
                {
                    var gx = (int)(_shimmer * travel);
                    var glowRect = new Rectangle(gx, y, glowW, h);
                    using var clip = Rounded(fill, h / 2);
                    var old = e.Graphics.Clip;
                    e.Graphics.SetClip(clip);
                    using var glowBrush = new LinearGradientBrush(
                        new Rectangle(glowRect.X - glowW, glowRect.Y, glowW * 3, h),
                        Color.FromArgb(0, 255, 255, 255), Color.FromArgb(255, 255, 255, 255), 0f);
                    var blend = new ColorBlend(3)
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 245, 200), Color.FromArgb(90, 255, 245, 200), Color.FromArgb(0, 255, 245, 200) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    glowBrush.InterpolationColors = blend;
                    e.Graphics.FillRectangle(glowBrush, glowRect);
                    e.Graphics.Clip = old;
                }
            }
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }
}

internal sealed class FlatWindowButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsClose { get; set; }
    public FlatWindowButton(string text)
    {
        Text = text; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent; ForeColor = Color.FromArgb(185, 196, 210);
        Font = new Font("Segoe UI", 13f, FontStyle.Regular); TabStop = false;
        FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 40, 50);
    }
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e); BackColor = IsClose ? Color.FromArgb(182, 50, 57) : Color.FromArgb(28, 35, 44); ForeColor = Color.White;
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e); BackColor = Color.Transparent; ForeColor = Color.FromArgb(185, 196, 210);
    }
}
