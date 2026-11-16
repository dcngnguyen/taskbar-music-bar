using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MusicBar;

/// <summary>
/// The window that opens upward from the bar. Shows the whole playlist or album that is playing
/// (or Spotify's "Up next" queue when the playlist can't be read). Click a song to play it;
/// the + button adds it to the queue.
/// </summary>
public partial class QueuePopup : Window
{
    private const string QueueMode = "queue";
    private static readonly Regex ClientIdPattern = new("^[0-9a-fA-F]{32}$");

    private readonly SpotifyClient _spotify;
    private readonly Settings _settings;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _statusCts;
    private static readonly (string Key, string Label)[] SortOptions =
    [
        ("Custom", "Custom order"), ("Title", "Title"), ("Artist", "Artist"),
        ("Album", "Album"), ("DateAdded", "Date added"), ("Duration", "Duration"),
    ];

    private List<TrackRow> _rows = [];
    /// <summary>The rows in the order shown (after sorting).</summary>
    private List<TrackRow> _view = [];
    /// <summary>The playlist/album URI being shown, <see cref="QueueMode"/>, or null.</summary>
    private string? _shownList;
    /// <summary>The rows sorted but not filtered by the search box (playback order).</summary>
    private List<TrackRow> _sorted = [];
    /// <summary>A playlist picked from the header menu, shown instead of what's playing until the window closes.</summary>
    private string? _browseUri;
    private ContextMenu? _menu;
    /// <summary>The exact copy of the playlist being shown (a new copy arrives when Spotify is re-read).</summary>
    private ContextTracks? _shownTracks;

    /// <summary>Set when the app is quitting, so the window may really close.</summary>
    public bool AllowClose { get; set; }

    /// <summary>
    /// True while you're typing in the window (search or Client ID) or a menu is open,
    /// so moving the mouse away won't close it.
    /// </summary>
    public bool KeepOpen => (IsActive && Keyboard.FocusedElement is TextBox) || _menu?.IsOpen == true;

    public QueuePopup(SpotifyClient spotify, Settings settings)
    {
        InitializeComponent();
        _spotify = spotify;
        _settings = settings;
        RedirectBox.Text = SpotifyClient.RedirectUri;
        ClientIdBox.Text = settings.SpotifyClientId ?? "";

        Native.MakeToolWindow(new WindowInteropHelper(this).EnsureHandle(), noActivate: false);
        Deactivated += (_, _) => HideAnimated();
        // Alt+F4 would destroy the window for good; just hide it unless the app is quitting.
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            HideAnimated();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else HideAnimated();
        };
        // Each time it opens, start from what's playing with no search.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            SearchBox.Clear();
            if (_browseUri is not null)
            {
                _browseUri = null;
                _shownList = null; // reload what's playing next time
            }
        };
    }

    private bool _closing;
    private int _animationVersion;

    /// <summary>Shown and not on its way out.</summary>
    public bool IsOpen => IsVisible && !_closing;

    /// <summary>Opens centered on <paramref name="centerX"/> with its bottom edge at <paramref name="bottom"/> (screen DIPs).</summary>
    public void ShowAbove(double centerX, double bottom)
    {
        bool fresh = !IsVisible;
        _closing = false;
        _animationVersion++;
        if (fresh)
        {
            Left = centerX - Width / 2;
            Top = bottom - Height;
            Show(); // not activated, so hovering never takes keyboard focus from what you're doing
        }
        // Fade in while sliding up. If it was mid-close, it turns around from where it is.
        Animate(to: 1, shift: 0, fresh ? 0 : null, fresh ? 12 : null, EasingMode.EaseOut, null);
        _ = RefreshAsync(0, scrollToCurrent: fresh);
    }

    /// <summary>Fades out while sliding down, then hides: the opening animation in reverse.</summary>
    public void HideAnimated()
    {
        if (!IsOpen) return;
        _closing = true;
        int version = ++_animationVersion;
        Animate(to: 0, shift: 12, null, null, EasingMode.EaseIn, () =>
        {
            // Only hide if it wasn't reopened while fading out.
            if (version != _animationVersion) return;
            _closing = false;
            Hide();
        });
    }

    private void Animate(double to, double shift, double? fromOpacity, double? fromShift, EasingMode mode, Action? done)
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = mode };
        var fade = new DoubleAnimation { From = fromOpacity, To = to, Duration = duration, EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        Root.BeginAnimation(OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation { From = fromShift, To = shift, Duration = duration, EasingFunction = ease });
    }

    /// <summary>Called when the song changes. Spotify updates its side a moment later, so wait a bit.</summary>
    public void OnTrackChanged()
    {
        if (IsVisible) _ = RefreshAsync(1500, scrollToCurrent: false);
    }

    private async Task RefreshAsync(int delayMs, bool scrollToCurrent)
    {
        if (_connectCts is not null) return; // still waiting for the browser
        if (!_spotify.IsConnected)
        {
            ShowView(SetupView);
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        ShowView(ListPanel);
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            if (_rows.Count == 0) EmptyText.Text = "Loading...";

            var now = await _spotify.GetNowPlayingAsync(ct);
            ContextTracks? list = null;
            string? listUri = null;

            // A playlist picked from the header menu is shown until the window closes.
            if (_browseUri is { } browse)
            {
                list = await _spotify.GetContextTracksAsync(new NowPlaying("playlist", browse, now?.ItemUri, null, null), ct);
                ct.ThrowIfCancellationRequested();
                if (list is null)
                {
                    _browseUri = null;
                    ShowStatus("Spotify won't share that playlist's songs.");
                }
                else listUri = browse;
            }

            if (list is null && now is not null)
            {
                list = await _spotify.GetContextTracksAsync(now, ct);
                ct.ThrowIfCancellationRequested();
                listUri = now.ContextUri;
            }

            if (list is null && now?.ItemUri is { } playing && _settings.LastListUri is { } lastUri)
            {
                // Playing songs picked from a sorted list: keep showing that list (it's cached, so this is cheap).
                var type = lastUri.Contains(":album:") ? "album" : "playlist";
                var last = await _spotify.GetContextTracksAsync(now with { ContextType = type, ContextUri = lastUri }, ct);
                ct.ThrowIfCancellationRequested();
                if (last is not null && last.Rows.Any(r => r.Uri == playing)) (listUri, list) = (lastUri, last);
            }

            if (list is not null && listUri is not null)
            {
                HeaderText.Text = list.Name;
                // Reload when it's a different list, or the same one freshly re-read from Spotify
                // (songs added or removed there, so old positions would play the wrong song).
                if (_shownList != listUri || !ReferenceEquals(_shownTracks, list))
                {
                    ShowRows(list.Rows.ToList(), listUri);
                    _shownTracks = list;
                    scrollToCurrent = true;
                }
                foreach (var row in _rows) row.IsCurrent = row.Uri == now?.ItemUri;
                EmptyText.Text = _view.Count == 0 && SearchBox.Text.Length > 0 ? "No matching songs." : "";
            }
            else
            {
                var queue = await _spotify.GetQueueAsync(ct);
                ct.ThrowIfCancellationRequested();
                HeaderText.Text = "Up next";
                ShowRows(queue.Items.ToList(), QueueMode);
                scrollToCurrent = false;
                EmptyText.Text = queue.Items.Count > 0 ? ""
                    : queue.SpotifyActive ? "Your queue is empty."
                    : "Nothing is playing on Spotify.";
            }
            SetMessage(ListMessage, null);

            if (scrollToCurrent) ScrollToCurrent();
        }
        catch (OperationCanceledException)
        {
        }
        catch (SpotifyException ex) when (ex.Code == "not_connected")
        {
            ShowRows([], null);
            ShowView(SetupView);
            SetMessage(SetupMessage, ex.Message);
        }
        catch (SpotifyException ex)
        {
            if (ct.IsCancellationRequested) return;
            SetMessage(ListMessage, ex.Message);
            if (_rows.Count == 0) EmptyText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Write("List refresh failed: " + ex);
            SetMessage(ListMessage, "Something went wrong loading your songs.");
            if (_rows.Count == 0) EmptyText.Text = "";
        }
    }

    private bool IsPlaylistShown => _shownList is not null && _shownList != QueueMode;

    private bool IsSpotifyOrder => _settings.SortBy == "Custom" && !_settings.SortDescending;

    private void ShowRows(List<TrackRow> rows, string? source)
    {
        _rows = rows;
        _shownList = source;
        _shownTracks = null;
        SortButton.Visibility = IsPlaylistShown ? Visibility.Visible : Visibility.Collapsed;
        ApplySort();
        if (_view.Count > 0) TrackList.ScrollIntoView(_view[0]);
        // After the list has laid out (and scrolled to the playing song), fade the rows in.
        Dispatcher.BeginInvoke(FadeInRows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The songs on screen fade in one after another, top to bottom.</summary>
    private void FadeInRows()
    {
        int step = 0;
        for (int i = 0; i < _view.Count && step < 15; i++)
        {
            if (TrackList.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement row) continue;
            row.Opacity = 0;
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(step++ * 25),
            });
        }
    }

    /// <summary>After adding to the queue, the button turns into a green checkmark for a moment.</summary>
    private async void FlashCheckmark(Button button, TrackRow row)
    {
        var original = button.Content;
        var scale = new ScaleTransform();
        button.Content = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 14,
            Foreground = (Brush)FindResource("SpotifyGreen"),
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        await Task.Delay(1200);
        if (button.DataContext == row) button.Content = original; // the row may have been reused for another song
    }

    /// <summary>
    /// Orders the list by the chosen sort (the queue is always shown as Spotify gives it),
    /// then keeps only songs matching the search box.
    /// </summary>
    private void ApplySort()
    {
        if (!IsPlaylistShown)
        {
            _sorted = _rows;
        }
        else
        {
            var text = StringComparer.CurrentCultureIgnoreCase;
            var sorted = (_settings.SortBy switch
            {
                "Title" => _rows.OrderBy(r => r.Title, text),
                "Artist" => _rows.OrderBy(r => r.Subtitle, text),
                "Album" => _rows.OrderBy(r => r.Album, text),
                "DateAdded" => _rows.OrderBy(r => r.AddedAt ?? DateTime.MinValue),
                "Duration" => _rows.OrderBy(r => r.DurationMs),
                _ => _rows.OrderBy(r => r.Position),
            }).ThenBy(r => r.Position).ToList();
            if (_settings.SortDescending) sorted.Reverse();
            _sorted = sorted;
        }

        var query = SearchBox.Text.Trim();
        _view = query.Length == 0
            ? _sorted
            : _sorted.Where(r => Matches(r.Title) || Matches(r.Subtitle) || Matches(r.Album)).ToList();
        TrackList.ItemsSource = _view;
        if (query.Length > 0 && _rows.Count > 0)
            EmptyText.Text = _view.Count == 0 ? "No matching songs." : "";

        bool Matches(string value) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SearchBox.Text.Length == 0 && _rows.Count > 0) EmptyText.Text = "";
        ApplySort();
        if (_view.Count > 0) TrackList.ScrollIntoView(_view[0]);
    }

    private void ScrollToCurrent()
    {
        if (_view.FirstOrDefault(r => r.IsCurrent) is not { } current) return;
        TrackList.UpdateLayout();
        TrackList.ScrollIntoView(current);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = _menu = new ContextMenu { PlacementTarget = SortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (key, label) in SortOptions)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _settings.SortBy == key };
            item.Click += (_, _) => ChangeSort(key, _settings.SortDescending);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reverse = new MenuItem { Header = "Reverse order", IsCheckable = true, IsChecked = _settings.SortDescending };
        reverse.Click += (_, _) => ChangeSort(_settings.SortBy, !_settings.SortDescending);
        menu.Items.Add(reverse);
        menu.IsOpen = true;
    }

    /// <summary>The header menu: switch to one of your playlists, or back to what's playing.</summary>
    private async void Header_Click(object sender, RoutedEventArgs e)
    {
        if (!_spotify.IsConnected) return;
        IReadOnlyList<(string Name, string Uri)> playlists;
        try
        {
            playlists = await _spotify.GetMyPlaylistsAsync(CancellationToken.None);
        }
        catch (SpotifyException ex)
        {
            SetMessage(ListMessage, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Log.Write("Could not list playlists: " + ex);
            SetMessage(ListMessage, "Couldn't load your playlists.");
            return;
        }

        var menu = _menu = new ContextMenu
        {
            PlacementTarget = HeaderButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MaxHeight = 360,
        };
        var playing = new MenuItem { Header = "Now playing", IsCheckable = true, IsChecked = _browseUri is null };
        playing.Click += (_, _) => Browse(null);
        menu.Items.Add(playing);
        menu.Items.Add(new Separator());
        if (playlists.Count == 0) menu.Items.Add(new MenuItem { Header = "No playlists of your own", IsEnabled = false });
        foreach (var (name, uri) in playlists)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = _shownList == uri };
            item.Click += (_, _) => Browse(uri);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Browse(string? playlistUri)
    {
        _browseUri = playlistUri;
        _shownList = null; // force the list to reload
        SearchBox.Clear();
        _ = RefreshAsync(0, scrollToCurrent: true);
    }

    private void ChangeSort(string sortBy, bool descending)
    {
        _settings.SortBy = sortBy;
        _settings.SortDescending = descending;
        try { _settings.Save(); }
        catch (Exception ex) { Log.Write("Could not save sort: " + ex.Message); }
        ApplySort();
        ScrollToCurrent();
    }

    // ---------- Playing and queueing ----------

    private async void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return;
        try
        {
            if (IsPlaylistShown && IsSpotifyOrder)
            {
                // Normal order: play the playlist itself from this song.
                await _spotify.PlayInContextAsync(_shownList!, row.Position, CancellationToken.None);
            }
            else
            {
                // Sorted list or queue: Spotify can't play a playlist in another order (or jump inside
                // its queue), so play this song and the ones below it in the sorted order
                // (the whole list, not only songs matching a search).
                var fromHere = _sorted.SkipWhile(r => r != row).Where(r => r.CanQueue).Select(r => r.Uri);
                await _spotify.PlayTracksAsync(fromHere, CancellationToken.None);
                if (IsPlaylistShown && _settings.LastListUri != _shownList)
                {
                    _settings.LastListUri = _shownList;
                    try { _settings.Save(); }
                    catch (Exception ex) { Log.Write("Could not save last list: " + ex.Message); }
                }
            }
            if (IsPlaylistShown) foreach (var r in _rows) r.IsCurrent = r == row;
            SetMessage(ListMessage, null);
        }
        catch (SpotifyException ex)
        {
            SetMessage(ListMessage, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Play failed: " + ex);
            SetMessage(ListMessage, "Couldn't play that song.");
        }
    }

    private async void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not TrackRow row) return;
        try
        {
            await _spotify.AddToQueueAsync(row.Uri, CancellationToken.None);
            SetMessage(ListMessage, null);
            ShowStatus($"Added \"{row.Title}\" to your queue");
            FlashCheckmark(button, row);
            if (_shownList == QueueMode) _ = RefreshAsync(500, scrollToCurrent: false);
        }
        catch (SpotifyException ex)
        {
            SetMessage(ListMessage, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Add to queue failed: " + ex);
            SetMessage(ListMessage, "Couldn't add that song to the queue.");
        }
    }

    private async void ShowStatus(string text)
    {
        _statusCts?.Cancel();
        var cts = _statusCts = new CancellationTokenSource();
        StatusText.Text = text;
        try
        {
            await Task.Delay(2500, cts.Token);
            StatusText.Text = "";
        }
        catch (OperationCanceledException) { }
    }

    // ---------- Connecting ----------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        if (!ClientIdPattern.IsMatch(clientId))
        {
            SetMessage(SetupMessage, "That doesn't look like a Client ID. It should be 32 letters and numbers.");
            return;
        }

        try
        {
            _settings.SpotifyClientId = clientId;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
            SetMessage(SetupMessage, "Couldn't save your Client ID. Check the log in %APPDATA%\\MusicBar.");
            return;
        }

        SetMessage(SetupMessage, null);
        using var cts = _connectCts = new CancellationTokenSource();
        ShowView(ConnectingView);
        bool connected = false;
        try
        {
            await _spotify.ConnectAsync(clientId, cts.Token);
            connected = true;
        }
        catch (OperationCanceledException)
        {
            ShowView(SetupView);
        }
        catch (SpotifyException ex)
        {
            ShowView(SetupView);
            SetMessage(SetupMessage, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Spotify connect failed: " + ex);
            ShowView(SetupView);
            SetMessage(SetupMessage, "Something went wrong connecting to Spotify.");
        }
        finally
        {
            _connectCts = null;
        }

        if (connected) await RefreshAsync(0, scrollToCurrent: true);
    }

    private void CancelConnect_Click(object sender, RoutedEventArgs e) => _connectCts?.Cancel();

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        _spotify.Disconnect();
        ShowRows([], null);
        HeaderText.Text = "Up next";
        SetMessage(ListMessage, null);
        ShowView(SetupView);
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://developer.spotify.com/dashboard") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Could not open browser: " + ex.Message);
        }
    }

    private void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SpotifyClient.RedirectUri);
        }
        catch (Exception ex)
        {
            // The clipboard can be briefly locked by another app.
            Log.Write("Copy failed: " + ex.Message);
            RedirectBox.Focus();
            RedirectBox.SelectAll();
        }
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var v in new FrameworkElement[] { ListPanel, SetupView, ConnectingView })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetMessage(TextBlock target, string? message)
    {
        target.Text = message ?? "";
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
