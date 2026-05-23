namespace AudioPttLatch;

/// <summary>
/// Coordinates the push-to-talk latch state machine.
/// It listens for the configured physical key, watches the selected audio endpoint,
/// suppresses key-up when needed, and later sends the delayed key-up.
/// </summary>
public sealed class LatchController : IDisposable
{
    private static readonly TimeSpan SyntheticKeyDownRefreshInterval = TimeSpan.FromMilliseconds(50);

    // KeyboardHook owns the global low-level keyboard callback.
    private readonly KeyboardHook _keyboardHook = new();

    // CoreAudioMeter reads current endpoint activity without recording or playing audio.
    private readonly CoreAudioMeter _meter = new();

    // WinForms timer keeps callbacks on the UI thread, which simplifies status updates.
    private readonly System.Windows.Forms.Timer _timer = new();
    private AppSettings _settings;

    // True only while the user's physical PTT key is still held down.
    private bool _physicalKeyDown;

    // True after the physical key was released and synthetic hold is active.
    private bool _latched;

    // Timestamp for when audio first dropped below threshold during a latch.
    private DateTime? _silentSince;

    // Last endpoint peak value read from Core Audio, exposed to the UI level meter.
    private float _lastPeak;

    // Last time a synthetic key-down was sent while latched.
    private DateTime _lastSyntheticKeyDown = DateTime.MinValue;

    // Last synthetic-input error, surfaced to the UI without crashing timer callbacks.
    private string? _lastInputError;

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
    /// Last SendInput error seen while trying to maintain the synthetic hold.
    /// </summary>
    public string? LastInputError => _lastInputError;

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
    /// Starts a synthetic hold if audio is still active when the physical key is released.
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
            // Let the real key-up reach the game, then take over with synthetic
            // key-down events. This avoids relying on hook suppression semantics,
            // which some games or input paths do not honor consistently.
            _latched = true;
            BeginSyntheticHold();
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
            if (!_physicalKeyDown)
            {
                KeepLatchedKeyDown(force: false);
            }

            _silentSince = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Silence must remain below threshold long enough to count as intentional
        // quiet, then the optional release delay is applied.
        _silentSince ??= DateTime.UtcNow;
        KeepLatchedKeyDown(force: false);
        var quietDurationMs = _settings.SilenceDurationMs + _settings.ReleaseDelayMs;
        if ((DateTime.UtcNow - _silentSince.Value).TotalMilliseconds >= quietDurationMs)
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
    /// Queues the first synthetic key-down after the physical key-up has been allowed through.
    /// </summary>
    private void BeginSyntheticHold()
    {
        _lastSyntheticKeyDown = DateTime.MinValue;
        var timer = new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_latched && !_physicalKeyDown)
            {
                KeepLatchedKeyDown(force: true);
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Sends or refreshes the synthetic key-down while the latch is active.
    /// </summary>
    private void KeepLatchedKeyDown(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastSyntheticKeyDown < SyntheticKeyDownRefreshInterval)
        {
            return;
        }

        try
        {
            KeySender.SendKeyDown((Keys)_settings.ActivationKey);
            _lastSyntheticKeyDown = now;
            _lastInputError = null;
        }
        catch (Exception ex)
        {
            _lastInputError = ex.Message;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Sends the synthetic key-up that ends the artificial hold.
    /// </summary>
    private void ReleaseIfLatched()
    {
        if (!_latched)
        {
            return;
        }

        // This completes the suppressed physical key-up from OnKeyUp.
        try
        {
            KeySender.SendKeyUp((Keys)_settings.ActivationKey);
            _lastInputError = null;
        }
        catch (Exception ex)
        {
            _lastInputError = ex.Message;
        }

        _latched = false;
        _silentSince = null;
        _lastSyntheticKeyDown = DateTime.MinValue;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
