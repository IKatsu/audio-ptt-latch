namespace AudioPttLatch;

/// <summary>
/// Lightweight description of a Windows audio endpoint shown in the device dropdown.
/// </summary>
public sealed class AudioEndpoint
{
    /// <summary>
    /// Creates a display-friendly wrapper around the endpoint ID returned by Core Audio.
    /// </summary>
    public AudioEndpoint(string id, string name, AudioDeviceKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    /// <summary>
    /// Stable Windows endpoint identifier used to re-open the selected device later.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Friendly device name as shown in Windows sound settings.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Whether this endpoint belongs to capture/input or render/output devices.
    /// </summary>
    public AudioDeviceKind Kind { get; }

    /// <summary>
    /// ComboBox uses this so the UI shows the friendly name without custom drawing.
    /// </summary>
    public override string ToString() => Name;
}
