using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Media.Control;

namespace TaskbarNowPlaying;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "SpotiBaiTaskbar_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new BarForm());
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "SpotiBai Taskbar";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(AppName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}

class BarForm : Form
{
    // ---- Win32 ----
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const int ULW_ALPHA = 2;
    const byte AC_SRC_OVER = 0;
    const byte AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    // ---- Settings ----
    const int BarWidth = 200;
    const int BarHeight = 28;
    const int DefaultLeftMargin = 12;   // distance from the left screen edge on first run

    // Layout: [thumb] then either the song text or the 4 buttons
    // Buttons: 0 = prev, 1 = play/pause, 2 = next, 3 = shuffle
    const int BtnCount = 4;
    const int BtnW = 24;
    const int ThumbSize = 22;
    const int ThumbX = 6;
    const int ThumbGap = 8;
    const int ShuffleExtraGap = 4;
    const int ContentX = ThumbX + ThumbSize + ThumbGap;

    const float AnimStep = 0.12f;       // lower = slower hover animation
    const int SlideTextPx = 6;
    const int SlideButtonsPx = 8;

    // Position is saved as distance from the LEFT edge
    static readonly string SaveFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarNowPlaying", "pos_v3.txt");

    // Windows icon glyphs (same codes in Segoe Fluent Icons and Segoe MDL2 Assets)
    const string GPrev = "\uE892";
    const string GNext = "\uE893";
    const string GPlay = "\uE768";
    const string GPause = "\uE769";
    const string GShuffle = "\uE8B1";

    // Browsers that can host Spotify Web
    static readonly string[] BrowserProcs =
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
    string? _verifiedWebAppId;
    string? _verifiedWebTitle;
    string? _verifiedWebArtist;
    DateTime _lastSpotifySessionSeenUtc;

    // Windows 11 -> Segoe Fluent Icons, Windows 10 -> Segoe MDL2 Assets
    readonly Font _iconFont = MakeIconFont();
    readonly Font _textFont = new("Segoe UI", 9f);

    static Font MakeIconFont()
    {
        using var fonts = new InstalledFontCollection();
        bool hasFluent = fonts.Families.Any(f => f.Name == "Segoe Fluent Icons");
        return new Font(hasFluent ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 10f);
    }

    static readonly StringFormat TextFmt = MakeFormat(StringAlignment.Near, true);
    static readonly StringFormat CenterFmt = MakeFormat(StringAlignment.Center, false);

    static StringFormat MakeFormat(StringAlignment align, bool ellipsis)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.Alignment = align;
        f.LineAlignment = StringAlignment.Center;
        f.FormatFlags |= StringFormatFlags.NoWrap;
        if (ellipsis) f.Trimming = StringTrimming.EllipsisCharacter;
        return f;
    }

    // ---- State ----
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
    string _text = "Nothing playing";
    bool _playing;
    bool _shuffle;
    readonly bool[] _enabled = new bool[BtnCount];
    Bitmap? _thumb;
    int _thumbTries;
    bool _busy;
    bool _dragging;
    bool _hovering;
    float _t;                           // hover animation progress, 0 = compact, 1 = controls
    int _dragStartMouseX, _dragStartLeft;
    int _hover = -1;
    int _pressed = -1;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public BarForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(BarWidth, BarHeight);
        Cursor = Cursors.SizeWE;

        // Right-click menu
        var menu = new ContextMenuStrip();
        var startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        startupItem.Click += (_, _) => Startup.Set(!Startup.IsEnabled());
        menu.Opening += (_, _) => startupItem.Checked = Startup.IsEnabled();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        ContextMenuStrip = menu;

        PlaceOnTaskbar();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) =>
        {
            if (_dragging) return;
            _hover = -1;
            _pressed = -1;
            Cursor = Cursors.SizeWE;
            SetHover(false);
        };
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        _animTimer.Tick += AnimTick;
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, EventArgs.Empty);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Redraw();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _animTimer.Stop();
        _timer.Stop();
        _iconFont.Dispose();
        _textFont.Dispose();
        _thumb?.Dispose();
        base.OnFormClosed(e);
    }

    void OnDisplayChanged(object? s, EventArgs e)
    {
        if (!_dragging) PlaceOnTaskbar();
    }

    // ---- Hover animation (timer only runs while transitioning) ----
    void SetHover(bool value)
    {
        _hovering = value;
        if (!_animTimer.Enabled) _animTimer.Start();
    }

    void AnimTick(object? s, EventArgs e)
    {
        float target = _hovering ? 1f : 0f;

        if (_t < target) _t = Math.Min(target, _t + AnimStep);
        else if (_t > target) _t = Math.Max(target, _t - AnimStep);

        Redraw();

        if (Math.Abs(_t - target) < 0.001f) _animTimer.Stop();
    }

    // ---- Layout ----
    static Rectangle ThumbRect() =>
        new(ThumbX, (BarHeight - ThumbSize) / 2, ThumbSize, ThumbSize);

    static Rectangle ButtonRect(int i)
    {
        int x = ContentX + i * BtnW + (i == 3 ? ShuffleExtraGap : 0);
        return new Rectangle(x, 2, BtnW, BarHeight - 4);
    }

    static int HitTest(Point p)
    {
        for (int i = 0; i < BtnCount; i++)
            if (ButtonRect(i).Contains(p)) return i;
        return -1;
    }

    // ---- Positioning ----
    int MinX => Screen.PrimaryScreen!.Bounds.Left + 4;
    int MaxX => Screen.PrimaryScreen!.Bounds.Right - Width - 4;

    void PlaceOnTaskbar()
    {
        var screen = Screen.PrimaryScreen!;
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;

        int taskbarHeight = bounds.Bottom - work.Bottom;
        if (taskbarHeight <= 0) taskbarHeight = 48;

        int y = work.Bottom + (taskbarHeight - BarHeight) / 2;

        int x = bounds.Left + DefaultLeftMargin;
        try
        {
            if (File.Exists(SaveFile) && int.TryParse(File.ReadAllText(SaveFile), out int saved))
                x = bounds.Left + saved;
        }
        catch { }

        Location = new Point(Math.Clamp(x, MinX, MaxX), y);
    }

    void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SaveFile)!);
            int offset = Left - Screen.PrimaryScreen!.Bounds.Left;
            File.WriteAllText(SaveFile, offset.ToString());
        }
        catch { }
    }

    // ---- Mouse: buttons click (only while hovering), everything else drags ----
    void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        int hit = (_hovering && _t > 0.6f) ? HitTest(e.Location) : -1;
        if (hit >= 0)
        {
            _pressed = hit;
            return;
        }

        _dragging = true;
        _dragStartMouseX = Cursor.Position.X;
        _dragStartLeft = Left;
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (_dragging)
        {
            int dx = Cursor.Position.X - _dragStartMouseX;
            Left = Math.Clamp(_dragStartLeft + dx, MinX, MaxX);
            return;
        }

        int hit = (_hovering && _t > 0.6f) ? HitTest(e.Location) : -1;
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.SizeWE;
            Redraw();
        }
    }

    async void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            SavePosition();

            // If the mouse ended up outside the bar, go back to the compact view
            if (!Bounds.Contains(Cursor.Position))
            {
                _hover = -1;
                Cursor = Cursors.SizeWE;
                SetHover(false);
            }
            return;
        }

        if (e.Button != MouseButtons.Left || _pressed < 0) return;

        int pressed = _pressed;
        _pressed = -1;
        if (HitTest(e.Location) != pressed) return; // released somewhere else = cancel

        await DoAction(pressed);
    }

    async Task DoAction(int button)
    {
        var ss = _session;
        if (ss == null || !_enabled[button]) return;

        try
        {
            switch (button)
            {
                case 0: await ss.TrySkipPreviousAsync(); break;
                case 1: await ss.TryTogglePlayPauseAsync(); break;
                case 2: await ss.TrySkipNextAsync(); break;
                case 3: await ss.TryChangeShuffleActiveAsync(!_shuffle); break;
            }
        }
        catch { }

        // Refresh quickly instead of waiting for the next timer tick
        await Task.Delay(250);
        Tick(null, EventArgs.Empty);
    }

    // ---- Thumbnail (loaded only when the track changes) ----
    static async Task<Bitmap?> LoadThumb(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            var reference = props.Thumbnail;
            if (reference == null) return null;

            using var rs = await reference.OpenReadAsync();
            using var stream = rs.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            using var raw = new Bitmap(ms);
            // Shrink to a tiny copy to keep memory low
            return new Bitmap(raw, new Size(ThumbSize * 2, ThumbSize * 2));
        }
        catch { return null; }
    }

    // ---- Spotify detection: desktop app OR Spotify Web in a browser ----
    async Task<bool> IsSpotifyAsync(GlobalSystemMediaTransportControlsSession session)
    {
        string appId = session.SourceAppUserModelId ?? "";

        // Desktop app
        if (appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return true;

        // Not a browser = not Spotify
        if (!BrowserProcs.Any(b => appId.Contains(b, StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            string title = props.Title ?? "";
            string artist = props.Artist ?? "";

            if (title.Length > 0 && artist.Length > 0)
            {
                foreach (var name in BrowserProcs)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        string t;
                        try { t = p.MainWindowTitle; }
                        catch { t = ""; }
                        finally { p.Dispose(); }

                        // Spotify Web tab title includes the current track title.
                        if (t.Contains(" • ") &&
                            t.Contains(title, StringComparison.OrdinalIgnoreCase))
                        {
                            _verifiedWebAppId = appId;
                            _verifiedWebTitle = title;
                            _verifiedWebArtist = artist;
                            return true;
                        }
                    }
                }
            }

            // Browsers may stop exposing the Spotify tab title after switching tabs.
            if (string.Equals(appId, _verifiedWebAppId, StringComparison.OrdinalIgnoreCase))
            {
                if (title.Length == 0 || artist.Length == 0) return true;
                return string.Equals(title, _verifiedWebTitle, StringComparison.Ordinal)
                    && string.Equals(artist, _verifiedWebArtist, StringComparison.Ordinal);
            }
        }
        catch { }

        return false;
    }

    // ---- Now playing ----
    async void Tick(object? sender, EventArgs e)
    {
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (_busy) return;
        _busy = true;
        try
        {
            _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            // Only show Spotify sessions (app or web); ignore every other app's media playback.
            GlobalSystemMediaTransportControlsSession? best = null;
            int bestScore = -1;
            foreach (var ss in _mgr.GetSessions())
            {
                if (!await IsSpotifyAsync(ss)) continue;

                bool playing = ss.GetPlaybackInfo().PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                int score = playing ? 1 : 0;
                if (score > bestScore) { bestScore = score; best = ss; }
            }

            var current = _mgr.GetCurrentSession();
            if (best == null && current != null && await IsSpotifyAsync(current))
                best = current;

            _session = best;
            if (best != null)
                _lastSpotifySessionSeenUtc = DateTime.UtcNow;

            string text = "Nothing playing";
            bool isPlaying = false;
            bool shuffle = false;
            bool[] enabled = new bool[BtnCount];
            Bitmap? newThumb = null;
            bool thumbLoaded = false;

            if (best != null)
            {
                var props = await best.TryGetMediaPropertiesAsync();
                var info = best.GetPlaybackInfo();
                var ctrl = info.Controls;

                isPlaying = info.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                shuffle = info.IsShuffleActive ?? false;

                enabled[0] = ctrl.IsPreviousEnabled;
                enabled[1] = ctrl.IsPlayPauseToggleEnabled || ctrl.IsPlayEnabled || ctrl.IsPauseEnabled;
                enabled[2] = ctrl.IsNextEnabled;
                enabled[3] = ctrl.IsShuffleEnabled;

                string title = props.Title ?? "";
                string artist = props.Artist ?? "";
                if (title.Length == 0 || artist.Length == 0)
                {
                    string appId = best.SourceAppUserModelId ?? "";
                    if (string.Equals(appId, _verifiedWebAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        title = _verifiedWebTitle ?? title;
                        artist = _verifiedWebArtist ?? artist;
                    }
                }

                if (!string.IsNullOrWhiteSpace(title))
                    text = string.IsNullOrWhiteSpace(artist)
                        ? title
                        : $"{artist} – {title}";

                // Load the thumbnail on a new track, or retry a few times if it came back empty
                bool newTrack = text != _text;
                if (newTrack) _thumbTries = 0;
                if (newTrack || (_thumb == null && _thumbTries < 5))
                {
                    _thumbTries++;
                    newThumb = await LoadThumb(props);
                    thumbLoaded = true;
                }
            }
            else if (_lastSpotifySessionSeenUtc != default &&
                     DateTime.UtcNow - _lastSpotifySessionSeenUtc < TimeSpan.FromSeconds(4))
            {
                text = _text;
                isPlaying = _playing;
                shuffle = _shuffle;
            }
            else if (_thumb != null)
            {
                thumbLoaded = true; // nothing playing: clear the old cover
            }

            bool changed = text != _text || isPlaying != _playing || shuffle != _shuffle;
            for (int i = 0; i < BtnCount; i++)
                if (enabled[i] != _enabled[i]) { _enabled[i] = enabled[i]; changed = true; }

            if (thumbLoaded)
            {
                var old = _thumb;
                _thumb = newThumb;
                old?.Dispose();
                changed = true;
            }

            if (changed)
            {
                _text = text;
                _playing = isPlaying;
                _shuffle = shuffle;
                Redraw();
            }
        }
        catch { /* ignore transient WinRT errors */ }
        finally { _busy = false; }
    }

    // ---- Drawing (per-pixel alpha, so the background is truly transparent) ----
    static Color WithAlpha(Color c, float f) =>
        Color.FromArgb(Math.Clamp((int)(c.A * f), 0, 255), c.R, c.G, c.B);

    static void DrawShadowed(Graphics g, string s, Font font, RectangleF r, Color c, StringFormat fmt)
    {
        if (c.A == 0) return;

        using (var shadow = new SolidBrush(Color.FromArgb(c.A * 150 / 255, 0, 0, 0)))
            g.DrawString(s, font, shadow, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), fmt);
        using var brush = new SolidBrush(c);
        g.DrawString(s, font, brush, r, fmt);
    }

    void Redraw()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        // Smoothstep easing for the hover transition
        float e = _t * _t * (3f - 2f * _t);

        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // Alpha 1 = invisible, but still catches the mouse (alpha 0 would be click-through)
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ---- Thumbnail: always visible, never moves ----
            var tr = ThumbRect();
            if (_thumb != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_thumb, tr);
            }
            else
            {
                using var b = new SolidBrush(Color.FromArgb(120, 70, 70, 70));
                g.FillRectangle(b, tr);
                DrawShadowed(g, "♪", _textFont, tr, Color.FromArgb(200, 200, 200), CenterFmt);
            }

            // ---- Song text: fades out and slides left as you hover ----
            float textAlpha = 1f - e;
            if (textAlpha > 0.01f)
            {
                var baseColor = _playing ? Color.White : Color.FromArgb(190, 190, 190);
                var textRect = new RectangleF(
                    ContentX - e * SlideTextPx, 0,
                    Width - ContentX - 8, Height);
                DrawShadowed(g, _text, _textFont, textRect, WithAlpha(baseColor, textAlpha), TextFmt);
            }

            // ---- Buttons: fade in and slide into place ----
            if (e > 0.01f)
            {
                float slide = (1f - e) * SlideButtonsPx;
                string[] glyphs = { GPrev, _playing ? GPause : GPlay, GNext, GShuffle };

                for (int i = 0; i < BtnCount; i++)
                {
                    var rect = ButtonRect(i);
                    var rectF = new RectangleF(rect.X + slide, rect.Y, rect.Width, rect.Height);

                    if (i == _hover && _enabled[i])
                        using (var b = new SolidBrush(WithAlpha(Color.FromArgb(60, 255, 255, 255), e)))
                            g.FillRectangle(b, rectF);

                    Color c;
                    if (!_enabled[i]) c = Color.FromArgb(140, 140, 140);
                    else if (i == 3 && _shuffle) c = Color.FromArgb(30, 215, 96); // Spotify green
                    else c = Color.White;

                    DrawShadowed(g, glyphs[i], _iconFont, rectF, WithAlpha(c, e), CenterFmt);
                }
            }
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp = SelectObject(memDc, hBmp);

        try
        {
            var size = new SIZE { cx = Width, cy = Height };
            var src = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBmp);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
