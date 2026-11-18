# Music Bar

A small music player bar that sits on the Windows 11 taskbar, just left of the tray icons and clock.

On the bar:

- The song, artist and album cover, with previous, play/pause and next buttons.
- A green line along the bottom shows how far into the song you are. Click or drag it to jump.
- Click the cover or song text to open Spotify, like its taskbar icon (click again to minimize it).
- Scroll over the bar to change Spotify's volume.
- Drag the thin line at the left edge to make the bar wider or narrower.
- Works with Spotify first, and with any other player that shows up in the Windows volume flyout.
- Starts automatically when you sign in to Windows, and hides during full-screen games and videos.

Hover over the song text and a window opens upward with the playlist or album that's playing:

- The current song is highlighted. Click a song to play it; hover a song and press the queue button to add it to your queue.
- Sort it like Spotify does (button at the top right), or type in the search box to filter.
- Click the playlist name to switch to another of your playlists.
- Spotify only shares playlists you created or collaborate on (plus albums). For other playlists the
  window shows Spotify's "Up next" queue instead.

## Install or update

From this folder in PowerShell:

```powershell
.\install.ps1
```

This builds the app, copies it to `%LOCALAPPDATA%\Programs\MusicBar`, and starts it. It needs the .NET 10 SDK.

## Connect Spotify (one time, needs Spotify Premium)

Spotify only shares your playlists and lets apps control playback when you register the app yourself,
and since February 2026 that requires Premium. Without connecting, the bar still shows and controls what's playing.

1. Hover over the song text on the bar. The window shows the steps.
2. Open the Spotify developer dashboard and create an app (any name and description).
3. Set the Redirect URI to `http://127.0.0.1:43821/callback` and tick **Web API**. Save.
4. Copy the app's **Client ID**, paste it into Music Bar, and press **Connect**.
5. Approve in the browser tab that opens.

If you see "Spotify refused the request", open your app in the dashboard, go to **User Management**, and add the email address of your Spotify account.

Your login is kept in Windows Credential Manager (entry `MusicBar.Spotify`). No password or secret is stored in the app.

## Quit, turn off auto-start, uninstall

- Quit: right-click the bar and choose **Quit Music Bar**.
- Stop it starting with Windows: open Task Scheduler, click Task Scheduler Library, right-click **MusicBar**, choose Disable.
  (It uses a sign-in task instead of Task Manager's Startup apps, because that list starts apps one by one and left the bar minutes late.)
- Uninstall: quit it, delete `%LOCALAPPDATA%\Programs\MusicBar` and `%APPDATA%\MusicBar`, then run
  `Unregister-ScheduledTask -TaskName MusicBar -Confirm:$false`.

## Notes

- Problems are written to `%APPDATA%\MusicBar\log.txt`.
- The weather widget sits under the bar. Turn it off in Settings, Personalization, Taskbar, Widgets.
- Settings (bar width, sort, Client ID) are in `%APPDATA%\MusicBar\settings.json`.
- Built with C# and WPF (.NET 10). Now-playing, progress and seeking come from Windows' media controls;
  playlists, playing songs, the queue and volume use the Spotify Web API.
