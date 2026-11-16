using System.IO;
using System.Text.Json;

namespace MusicBar;

/// <summary>Saved in %APPDATA%\MusicBar\settings.json. Holds nothing secret.</summary>
public sealed class Settings
{
    public string? SpotifyClientId { get; set; }

    /// <summary>How the playlist list is sorted: Custom, Title, Artist, Album, DateAdded or Duration.</summary>
    public string SortBy { get; set; } = "Custom";

    public bool SortDescending { get; set; }

    /// <summary>
    /// The playlist or album you last played sorted songs from. Spotify then stops reporting the
    /// playlist, so this keeps it on screen while its songs play, even after a restart.
    /// </summary>
    public string? LastListUri { get; set; }

    /// <summary>Spotify's volume before a middle-click mute, restored by the next middle-click.</summary>
    public int? MutedVolume { get; set; }

    /// <summary>Bar width in device-independent pixels (changed by dragging the grip).</summary>
    public double BarWidth { get; set; } = 300;

    private static string FilePath => Path.Combine(Log.DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings, using defaults: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Log.DataDir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, overwrite: true);
    }
}
