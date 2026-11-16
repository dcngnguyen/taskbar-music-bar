using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicBar;

public sealed record MediaSnapshot(
    bool HasSession, string Title, string Artist, bool IsPlaying,
    bool CanPrevious, bool CanNext, BitmapImage? Cover)
{
    public static readonly MediaSnapshot Empty = new(false, "", "", false, false, false, null);
}

/// <summary>
/// Reads "now playing" from Windows' media controls (the same info as the volume flyout).
/// Prefers Spotify when it is running, otherwise uses whatever is playing.
/// </summary>
internal sealed class MediaService
{
    private readonly Dispatcher _dispatcher;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onProperties;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Raised on the UI thread whenever the song or play state may have changed.</summary>
    public event Action? Changed;

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _onProperties = (_, _) => Notify();
        _onPlayback = (_, _) => Notify();
    }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(PickSession);
        _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(PickSession);
        PickSession();
    }

    private void Notify() => _dispatcher.InvokeAsync(() => Changed?.Invoke());

    private void PickSession()
    {
        if (_manager is null) return;

        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            pick = _manager.GetSessions().FirstOrDefault(s =>
                       s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                   ?? _manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            Log.Write("Could not list media sessions: " + ex.Message);
        }

        if (_session is not null)
        {
            try
            {
                _session.MediaPropertiesChanged -= _onProperties;
                _session.PlaybackInfoChanged -= _onPlayback;
            }
            catch (Exception ex)
            {
                // The old player may already be gone; carry on with the new one.
                Log.Write("Could not detach old media session: " + ex.Message);
            }
        }
        _session = pick;
        if (_session is not null)
        {
            try
            {
                _session.MediaPropertiesChanged += _onProperties;
                _session.PlaybackInfoChanged += _onPlayback;
            }
            catch (Exception ex)
            {
                Log.Write("Could not attach media session: " + ex.Message);
            }
        }
        Changed?.Invoke();
    }

    public async Task<MediaSnapshot> GetSnapshotAsync()
    {
        var session = _session;
        if (session is null) return MediaSnapshot.Empty;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var cover = props?.Thumbnail is null ? null : await LoadCoverAsync(props.Thumbnail);
            return new MediaSnapshot(
                true,
                props?.Title ?? "",
                props?.Artist ?? "",
                info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                info?.Controls.IsPreviousEnabled ?? false,
                info?.Controls.IsNextEnabled ?? false,
                cover);
        }
        catch (Exception ex)
        {
            Log.Write("Could not read now playing: " + ex.Message);
            return MediaSnapshot.Empty;
        }
    }

    private static async Task<BitmapImage?> LoadCoverAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var source = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write("Could not load album cover: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How far into the song we are. Players report the position only every few seconds,
    /// so while playing, the time since that report is added on.
    /// </summary>
    public (TimeSpan Position, TimeSpan Duration)? GetTimeline()
    {
        var session = _session;
        if (session is null) return null;
        try
        {
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration <= TimeSpan.Zero) return null;

            var position = t.Position - t.StartTime;
            var sinceReport = DateTimeOffset.Now - t.LastUpdatedTime;
            bool playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && sinceReport > TimeSpan.Zero && sinceReport < duration) position += sinceReport;

            return (position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position, duration);
        }
        catch (Exception)
        {
            return null; // the player went away mid-read
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session is { } s) await s.TryChangePlaybackPositionAsync(position.Ticks);
    }

    public async Task PlayPauseAsync()
    {
        if (_session is { } s) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_session is { } s) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session is { } s) await s.TrySkipPreviousAsync();
    }
}
