using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MusicBar;

/// <summary>The slim bar drawn on top of the taskbar, just left of the tray icons.</summary>
public partial class BarWindow : Window
{
    private const double BarHeight = 40, GapFromTray = 8, PopupGap = 8, MinBarWidth = 220, MaxBarWidth = 900;

    private readonly IntPtr _hwnd;
    private readonly MediaService _media;
    private readonly QueuePopup _popup;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _openDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _closeDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Native.WinEventProc _foregroundChanged;
    private IntPtr _foregroundHook;
    private readonly Settings _settings;
    private double _barWidth;
    private bool _resizing;
    private int _barRightPx;
    private double _scale = 1;
    private int _renderVersion;
    private string _trackKey = "";
    private readonly SpotifyClient _spotify;
    private MediaSnapshot _lastSnapshot = MediaSnapshot.Empty;

    // Progress line
    private bool _seeking;
    private double _seekFraction;

    // Volume (mouse wheel)
    private int? _volume;
    private bool _volumeLoading;
    private readonly DispatcherTimer _volumeSend = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public BarWindow()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.MakeToolWindow(_hwnd, noActivate: true);
        HoverGlow.Attach(Bar, Cover);

        _settings = Settings.Load();
        _barWidth = Math.Clamp(_settings.BarWidth, MinBarWidth, MaxBarWidth);
        _spotify = new SpotifyClient { ClientId = _settings.SpotifyClientId };
        _popup = new QueuePopup(_spotify, _settings);
        // Signing out or shutting down Windows must be able to close everything.
        Application.Current.SessionEnding += (_, _) => _quitting = _popup.AllowClose = true;
        _volumeSend.Tick += async (_, _) => await SendVolumeAsync();
        _noteTimer.Tick += (_, _) =>
        {
            // Volume note done: show the artist again, and re-read the volume next time
            // (it may have been changed in Spotify meanwhile).
            _noteTimer.Stop();
            _volume = null;
            ShowVolumePanel(false);
            Render(_lastSnapshot);
        };

        // Hover over the song text to open the queue; it closes once the mouse has left both windows.
        HoverZone.MouseEnter += (_, _) =>
        {
            _closeDelay.Stop();
            if (!_popup.IsOpen) _openDelay.Start();
        };
        HoverZone.MouseLeave += (_, _) =>
        {
            _openDelay.Stop();
            if (_popup.IsOpen) _closeDelay.Start();
        };
        _popup.MouseEnter += (_, _) => _closeDelay.Stop();
        _popup.MouseLeave += (_, _) => _closeDelay.Start();
        _openDelay.Tick += (_, _) =>
        {
            _openDelay.Stop();
            if (HoverZone.IsMouseOver && IsVisible) OpenPopup();
        };
        _closeDelay.Tick += (_, _) =>
        {
            _closeDelay.Stop();
            // While you're typing in the window (e.g. pasting), it stays until you click elsewhere.
            if (!HoverZone.IsMouseOver && !_popup.IsMouseOver && !_popup.KeepOpen) _popup.HideAnimated();
        };

        _media = new MediaService(Dispatcher);
        _media.Changed += OnMediaChanged;

        _foregroundChanged = (_, _, _, _, _, _, _) => UpdatePlacement();
        _timer.Tick += (_, _) =>
        {
            UpdatePlacement();
            UpdateProgress();
        };
    }

    public async void Start()
    {
        UpdatePlacement();
        _timer.Start();
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundChanged, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        try
        {
            await _media.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Media controls failed to start: " + ex);
            TitleText.Text = "Media controls unavailable";
        }
    }

    // ---------- Placement ----------

    /// <summary>Keeps the bar on the taskbar, above it in the stack, and hidden during full-screen apps.</summary>
    private void UpdatePlacement()
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out var tb) ||
            Native.IsFullscreenAppActive() || !Native.IsTaskbarShown(taskbar, tb))
        {
            if (IsVisible && !_barHiding) FadeBar(show: false);
            if (_popup.IsVisible) _popup.Hide();
            return;
        }
        if (_barHiding) FadeBar(show: true); // came back before the fade-out finished

        uint dpi = Native.GetDpiForWindow(taskbar);
        double scale = dpi > 0 ? dpi / 96.0 : 1;
        int Px(double dip) => (int)Math.Round(dip * scale);

        int height = Px(BarHeight);
        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr) && tr.Width > 0
            ? tr.Left - Px(GapFromTray)
            : tb.Right - Px(360);
        // Never wider than the space left of the tray (keeps the Start button clear).
        int width = Math.Min(Px(_barWidth), right - tb.Left - Px(80));
        _barRightPx = right;
        int x = right - width;
        int y = tb.Top + (tb.Height - height) / 2;
        _scale = scale;

        bool firstShow = !IsVisible;
        if (firstShow)
        {
            Left = x / scale;
            Top = y / scale;
            Opacity = 0;
            Show();
            FadeBar(show: true);
        }

        // Clicking the taskbar lifts it over us, so lift the bar back on top when that happens.
        uint flags = Native.SWP_NOACTIVATE;
        if (!firstShow && !Native.IsAbove(taskbar, _hwnd)) flags |= Native.SWP_NOZORDER;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, flags);
    }

    private bool _barHiding;
    private int _barFadeVersion;

    /// <summary>Fades the whole bar in, or out and then hides it (e.g. when a full-screen game starts).</summary>
    private void FadeBar(bool show)
    {
        int version = ++_barFadeVersion;
        _barHiding = !show;
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 220 : 150));
        if (!show)
        {
            fade.Completed += (_, _) =>
            {
                if (version != _barFadeVersion) return; // shown again meanwhile
                _barHiding = false;
                Hide();
            };
        }
        BeginAnimation(OpacityProperty, fade);
    }

    // ---------- Now playing ----------

    private async void OnMediaChanged()
    {
        int version = ++_renderVersion;
        var snapshot = await _media.GetSnapshotAsync();
        if (version == _renderVersion) Render(snapshot);
    }

    private void Render(MediaSnapshot s)
    {
        bool playStateChanged = s.IsPlaying != _lastSnapshot.IsPlaying;
        _lastSnapshot = s;
        bool hasTrack = s.HasSession && !string.IsNullOrWhiteSpace(s.Title);
        var key = hasTrack ? s.Title + "\n" + s.Artist : "";
        bool newSong = key != _trackKey;
        if (newSong && hasTrack && _trackKey.Length > 0) SlideSong(); // shows the new song halfway through
        else if (!_sliding) ShowSong(s);

        if (playStateChanged) PopPlayIcon();
        PlayButton.Content = s.IsPlaying ? "" : "";
        PlayButton.IsEnabled = s.HasSession;
        PrevButton.IsEnabled = s.CanPrevious;
        NextButton.IsEnabled = s.CanNext;

        if (newSong)
        {
            _trackKey = key;
            _popup.OnTrackChanged();
        }
    }

    /// <summary>Title, artist and cover.</summary>
    private void ShowSong(MediaSnapshot s)
    {
        bool hasTrack = s.HasSession && !string.IsNullOrWhiteSpace(s.Title);
        TitleText.Text = hasTrack ? s.Title : "Nothing playing";
        if (!_noteTimer.IsEnabled) ShowSecondLine(hasTrack ? s.Artist : "");

        if (hasTrack && s.Cover is not null)
        {
            Cover.Background = new ImageBrush(s.Cover) { Stretch = Stretch.UniformToFill };
            CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            Cover.Background = (Brush)FindResource("Hover");
            CoverPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private bool _sliding;
    private int _slideVersion;
    private int _skipDirection;
    private DateTime _skipAtUtc;

    /// <summary>
    /// New song: the old title slides out and the new one follows in the same direction:
    /// rightward for Next (and songs ending on their own), leftward for Previous.
    /// </summary>
    private void SlideSong()
    {
        int direction = DateTime.UtcNow - _skipAtUtc < TimeSpan.FromSeconds(3) ? _skipDirection : 1;
        _skipAtUtc = default;
        int version = ++_slideVersion;
        _sliding = true;

        var outTime = TimeSpan.FromMilliseconds(120);
        var inTime = TimeSpan.FromMilliseconds(220);
        var fadeOut = new DoubleAnimation(0, outTime) { EasingFunction = EaseIn };
        fadeOut.Completed += (_, _) =>
        {
            if (version != _slideVersion) return; // another song change took over
            _sliding = false;
            ShowSong(_lastSnapshot); // the newest info, including a cover that arrived meanwhile
            HoverZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, inTime) { EasingFunction = EaseOut });
            SongShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-direction * 14, 0, inTime) { EasingFunction = EaseOut });
        };
        HoverZone.BeginAnimation(OpacityProperty, fadeOut);
        SongShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 14, outTime) { EasingFunction = EaseIn });
    }

    /// <summary>Play/pause: the icon shrinks a little and springs back as it switches.</summary>
    private void PopPlayIcon()
    {
        if (PlayButton.RenderTransform is not ScaleTransform scale)
            PlayButton.RenderTransform = scale = new ScaleTransform();
        var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void ShowSecondLine(string text)
    {
        ArtistText.Text = text;
        ArtistText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows a short message (like the volume) in place of the artist for a moment.</summary>
    private void ShowNote(string text)
    {
        ShowSecondLine(text);
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    // ---------- Progress line ----------

    private void UpdateProgress()
    {
        if (_seeking) return; // the line follows the mouse while dragging
        var timeline = _lastSnapshot.HasSession ? _media.GetTimeline() : null;
        ProgressZone.Visibility = timeline is null ? Visibility.Hidden : Visibility.Visible;
        if (timeline is { } t) SetProgress(t.Position.TotalMilliseconds / t.Duration.TotalMilliseconds, glide: true);
    }

    /// <summary>
    /// Moves the green line. With glide, small forward steps animate over one timer tick so the
    /// line moves continuously; jumps (new song, seeking) snap straight there.
    /// </summary>
    private void SetProgress(double fraction, bool glide = false)
    {
        double target = Math.Clamp(fraction, 0, 1) * ProgressTrack.ActualWidth;
        double current = ProgressFill.ActualWidth;
        double step = target - current;
        if (glide && step >= 0 && step < ProgressTrack.ActualWidth * 0.05)
        {
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, _timer.Interval));
        }
        else
        {
            ProgressFill.BeginAnimation(WidthProperty, null);
            ProgressFill.Width = target;
        }
    }

    private double FractionAt(System.Windows.Input.MouseEventArgs e) =>
        ProgressTrack.ActualWidth > 0 ? Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1) : 0;

    private void Progress_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _seeking = ProgressZone.CaptureMouse();
        _seekFraction = FractionAt(e);
        SetProgress(_seekFraction);
        e.Handled = true;
    }

    private void Progress_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seeking) return;
        _seekFraction = FractionAt(e);
        SetProgress(_seekFraction);
    }

    private void Progress_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => ProgressZone.ReleaseMouseCapture();

    private async void Progress_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seeking) return;
        if (_media.GetTimeline() is { } t)
        {
            try { await _media.SeekAsync(t.Duration * _seekFraction); }
            catch (Exception ex) { Log.Write("Seek failed: " + ex.Message); }
        }
        _seeking = false;
    }

    // ---------- Volume with the mouse wheel ----------

    /// <summary>Scroll over the bar to change Spotify's volume in steps of 1%.</summary>
    private async void Bar_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_spotify.IsConnected)
        {
            ShowNote("Connect Spotify to change volume");
            return;
        }
        if (_volumeLoading) return;

        if (_volume is null)
        {
            _volumeLoading = true;
            try
            {
                _volume = await _spotify.GetVolumeAsync(CancellationToken.None);
            }
            catch (SpotifyException ex)
            {
                ShowNote(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Log.Write("Could not read volume: " + ex);
                ShowNote("Couldn't read the volume");
                return;
            }
            finally
            {
                _volumeLoading = false;
            }
            if (_volume is null)
            {
                ShowNote("This device's volume can't be changed");
                return;
            }
        }

        ShowVolume(Math.Clamp(_volume.Value + Math.Sign(e.Delta), 0, 100));
        _settings.MutedVolume = null; // scrolling replaces any earlier mute
        // Wait until scrolling pauses, then send one change (Spotify limits how often apps can call it).
        _volumeSend.Stop();
        _volumeSend.Start();
    }

    private bool _volumeShown;

    /// <summary>
    /// Middle-click anywhere on the bar mutes Spotify (volume 0, remembering the old level);
    /// middle-click again restores it.
    /// </summary>
    private async void Bar_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        e.Handled = true;
        if (!_spotify.IsConnected)
        {
            ShowNote("Connect Spotify to mute");
            return;
        }
        if (_volumeLoading) return;

        _volumeLoading = true;
        try
        {
            _volumeSend.Stop(); // a pending scroll change would undo the mute
            var current = await _spotify.GetVolumeAsync(CancellationToken.None);
            if (current is null)
            {
                ShowNote("This device's volume can't be changed");
                return;
            }

            int target;
            if (current > 0)
            {
                _settings.MutedVolume = current;
                target = 0;
            }
            else
            {
                target = _settings.MutedVolume is > 0 and var before ? before : 50;
                _settings.MutedVolume = null;
            }
            try { _settings.Save(); }
            catch (Exception ex) { Log.Write("Could not save mute: " + ex.Message); }

            await _spotify.SetVolumeAsync(target, CancellationToken.None);
            ShowVolume(target);
        }
        catch (SpotifyException ex)
        {
            ShowNote(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Mute failed: " + ex);
            ShowNote("Couldn't change the volume");
        }
        finally
        {
            _volumeLoading = false;
        }
    }

    /// <summary>Shows the volume in place of the buttons for a moment.</summary>
    private void ShowVolume(int percent)
    {
        _volume = percent;
        VolumeIcon.Text = percent == 0 ? "" : ""; // muted / speaker
        VolumeText.Text = $"{percent}%";
        ShowVolumePanel(true);
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    /// <summary>
    /// Swaps the three buttons for the volume reading: the buttons fade out upward while the
    /// volume fades in from below, and the reverse when it goes away.
    /// </summary>
    private void ShowVolumePanel(bool show)
    {
        if (_volumeShown == show) return; // don't restart the animation on every scroll step
        _volumeShown = show;
        ButtonsPanel.IsHitTestVisible = !show;

        var duration = TimeSpan.FromMilliseconds(160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimation To(double value) => new(value, duration) { EasingFunction = ease };

        ButtonsPanel.BeginAnimation(OpacityProperty, To(show ? 0 : 1));
        ButtonsShift.BeginAnimation(TranslateTransform.YProperty, To(show ? -5 : 0));
        VolumePanel.BeginAnimation(OpacityProperty, To(show ? 1 : 0));
        VolumeShift.BeginAnimation(TranslateTransform.YProperty, To(show ? 0 : 5));
    }

    private async Task SendVolumeAsync()
    {
        _volumeSend.Stop();
        if (_volume is not { } percent) return;
        try
        {
            await _spotify.SetVolumeAsync(percent, CancellationToken.None);
        }
        catch (SpotifyException ex)
        {
            ShowNote(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Could not set volume: " + ex);
            ShowNote("Couldn't change the volume");
        }
    }

    // ---------- Resizing with the grip ----------

    private void Grip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _resizing = Grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_resizing || !Native.GetCursorPos(out var cursor)) return;
        // The right edge stays put by the tray; the left edge follows the mouse.
        _barWidth = Math.Clamp((_barRightPx - cursor.X) / _scale, MinBarWidth, MaxBarWidth);
        UpdatePlacement();
    }

    private void Grip_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => Grip.ReleaseMouseCapture();

    private void Grip_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        _settings.BarWidth = _barWidth;
        try { _settings.Save(); }
        catch (Exception ex) { Log.Write("Could not save bar width: " + ex.Message); }
    }

    // ---------- Buttons ----------

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await RunMediaCommand(_media.PlayPauseAsync);
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        (_skipDirection, _skipAtUtc) = (1, DateTime.UtcNow);
        await RunMediaCommand(_media.NextAsync);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        (_skipDirection, _skipAtUtc) = (-1, DateTime.UtcNow);
        await RunMediaCommand(_media.PreviousAsync);
    }

    private static async Task RunMediaCommand(Func<Task> command)
    {
        try { await command(); }
        catch (Exception ex) { Log.Write("Media command failed: " + ex.Message); }
    }

    private void OpenPopup()
    {
        if (!Native.GetWindowRect(_hwnd, out var bar)) return;
        _popup.ShowAbove((bar.Left + bar.Right) / 2.0 / _scale, bar.Top / _scale - PopupGap);
    }

    /// <summary>
    /// The bar never takes focus, but a menu only notices clicks elsewhere when its window has focus.
    /// So take focus while the menu is open; clicking away then closes it.
    /// </summary>
    private void Menu_Opened(object sender, RoutedEventArgs e) => Native.SetForegroundWindow(_hwnd);

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        _popup.AllowClose = true;
        _quitting = true;
        Application.Current.Shutdown();
    }

    private bool _quitting;

    /// <summary>
    /// Alt+F4 (possible after using the right-click menu) must not destroy the bar while the app
    /// keeps running invisibly. Only "Quit Music Bar" closes it.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting) e.Cancel = true;
        base.OnClosing(e);
    }
}
