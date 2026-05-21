namespace AudioPttLatch;

/// <summary>
/// Coordinates the push-to-talk latch state machine.
/// It listens for the configured physical key, watches the selected audio endpoint,
/// suppresses key-up when needed, and later sends the delayed key-up.
/// </summary>
public sealed class LatchController : IDisposable
{
    // KeyboardHook owns the global low-level keyboard callback.
    private readonly KeyboardHook _keyboardHook = new();

    // CoreAudioMeter reads current endpoint activity without recording or playing audio.
    private readonly CoreAudioMeter _meter = new();

    // WinForms timer keeps callbacks on the UI thread, which simplifies status updates.
    private readonly System.Windows.Forms.Timer _timer = new();
    private AppSettings _settings;

    // True only while the user's physical PTT key is still held down.
    private bool _physicalKeyDown;

    // True after a physical key-up was swallowed and before our synthetic key-up is sent.
    private bool _latched;

    // Timestamp for when audio first dropped below threshold during a latch.
    private DateTime? _silentSince;

    // Last endpoint peak value read from Core Audio, exposed to the UI level meter.
    private float _lastPeak;

    /// <summary>
    /// Creates the controller and wires keyboard/audio polling callbacks.
    /// </summary>
    public LatchController(AppSettings settings)
    {
        _settings = settings;
        _keyboardHook.KeyDown += OnKeyDown;
        _keyboardHook.KeyUp += OnKeyUp;
        _timer.Interval = 25;
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Raised whenever running, latch, or audio-level state changes for the UI.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// True while the hook and polling timer are active.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// True while the app is artificially holding the configured key down.
    /// </summary>
    public bool IsLatched => _latched;

    /// <summary>
    /// Most recent 0..1 peak level from the selected audio endpoint.
    /// </summary>
    public float LastPeak => _lastPeak;

    /// <summary>
    /// Opens the selected audio device, installs the keyboard hook, and starts polling.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        Reconfigure(_settings);
        _keyboardHook.Start();
        _timer.Start();
        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops monitoring and releases the key first if the app is currently latched.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        ReleaseIfLatched();
        _timer.Stop();
        _keyboardHook.Stop();
        _meter.Dispose();
        _physicalKeyDown = false;
        IsRunning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies new settings while keeping the controller alive.
    /// </summary>
    public void Reconfigure(AppSettings settings)
    {
        _settings = settings;
        // Re-open the meter whenever the user changes input/output type or device.
        _meter.Open(_settings.DeviceKind, _settings.DeviceId);
        _silentSince = null;
    }

    /// <summary>
    /// Releases native resources owned by the hook, meter, and timer.
    /// </summary>
    public void Dispose()
    {
        Stop();
        _keyboardHook.Dispose();
        _meter.Dispose();
        _timer.Dispose();
    }

    /// <summary>
    /// Tracks when the configured physical key is pressed.
    /// </summary>
    private void OnKeyDown(object? sender, KeyboardHookEventArgs e)
    {
        if (!_settings.Enabled || e.Key != (Keys)_settings.ActivationKey)
        {
            return;
        }

        _physicalKeyDown = true;
        _silentSince = null;
    }

    /// <summary>
    /// Decides whether the configured physical key-up should pass through or be latched.
    /// </summary>
    private void OnKeyUp(object? sender, KeyboardHookEventArgs e)
    {
        if (!_settings.Enabled || e.Key != (Keys)_settings.ActivationKey)
        {
            return;
        }

        _physicalKeyDown = false;
        if (IsAudioActive())
        {
            // The key-down already reached the game. Suppress the physical key-up
            // so push-to-talk remains active while the voice changer finishes output.
            _latched = true;
            e.Handled = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Polls audio activity and releases the latch after sustained silence.
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Polling the endpoint peak is cheap and keeps the latch logic independent
        // from any particular audio driver or voice changer.
        _lastPeak = _meter.GetPeakValue();

        if (!_latched)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_physicalKeyDown || IsAudioActive())
        {
            _silentSince = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Silence must remain below threshold for the configured delay before the
        // synthetic key-up is sent. Brief gaps in speech should not release PTT.
        _silentSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _silentSince.Value).TotalMilliseconds >= _settings.ReleaseDelayMs)
        {
            ReleaseIfLatched();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Converts the current endpoint peak into an active/silent decision.
    /// </summary>
    private bool IsAudioActive() => _lastPeak >= Math.Clamp(_settings.ActivityThreshold, 0.001f, 1f);

    /// <summary>
    /// Sends the synthetic key-up that completes a previously suppressed physical key-up.
    /// </summary>
    private void ReleaseIfLatched()
    {
        if (!_latched)
        {
            return;
        }

        // This completes the suppressed physical key-up from OnKeyUp.
        KeySender.SendKeyUp((Keys)_settings.ActivationKey);
        _latched = false;
        _silentSince = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
