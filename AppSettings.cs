using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPttLatch;

/// <summary>
/// Stores the user-configurable behavior for the latch and handles persistence.
/// The settings are intentionally simple JSON so they can be inspected or reset by hand.
/// </summary>
public sealed class AppSettings
{
    // Shared JSON options for both load and save so enum values stay readable.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Virtual-key code for the physical push-to-talk key to monitor and latch.
    /// </summary>
    public int ActivationKey { get; set; } = (int)Keys.CapsLock;

    /// <summary>
    /// Whether the selected audio endpoint is a microphone/capture device or speaker/render device.
    /// </summary>
    public AudioDeviceKind DeviceKind { get; set; } = AudioDeviceKind.Input;

    /// <summary>
    /// Windows endpoint ID for the selected device. Null means "use the current default".
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Minimum peak level from the audio endpoint before it is considered active.
    /// </summary>
    public float ActivityThreshold { get; set; } = 0.02f;

    /// <summary>
    /// Milliseconds to wait after audio falls below threshold before releasing the latched key.
    /// </summary>
    public int ReleaseDelayMs { get; set; } = 100;

    /// <summary>
    /// When true, minimize/close hides the window in the notification area.
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Master switch for the keyboard hook and audio monitoring loop.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Full path to the per-user settings file under LocalAppData.
    /// </summary>
    public static string SettingsPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioKeyLatch");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "settings.json");
        }
    }

    /// <summary>
    /// Reads saved settings from disk, falling back to defaults if the file is missing or invalid.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes the current settings to disk.
    /// </summary>
    public void Save()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>
/// User-facing choice for the kind of audio endpoint to monitor.
/// </summary>
public enum AudioDeviceKind
{
    Input,
    Output
}
