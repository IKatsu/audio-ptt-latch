namespace AudioPttLatch;

/// <summary>
/// Main configuration window for the utility.
/// It owns the UI controls, persists settings, and starts/stops the latch controller.
/// </summary>
public sealed class Form1 : Form
{
    // Settings and controller are kept for the lifetime of the window.
    private readonly AppSettings _settings;
    private readonly LatchController _controller;
    private readonly NotifyIcon _notifyIcon;

    // UI controls are fields because settings and controller events update them after construction.
    private ComboBox _deviceKindCombo = null!;
    private ComboBox _deviceCombo = null!;
    private Button _keyButton = null!;
    private NumericUpDown _thresholdInput = null!;
    private NumericUpDown _silenceDurationInput = null!;
    private NumericUpDown _releaseDelayInput = null!;
    private CheckBox _enabledCheck = null!;
    private CheckBox _minimizeToTrayCheck = null!;
    private ProgressBar _levelBar = null!;
    private Label _statusLabel = null!;
    private Label _deviceLabel = null!;

    // True while the next key press should become the configured activation key.
    private bool _capturingKey;

    // Distinguishes tray-menu Exit from normal close-to-tray behavior.
    private bool _reallyClose;

    /// <summary>
    /// Creates the window, loads saved settings, populates devices, and starts monitoring if enabled.
    /// </summary>
    public Form1()
    {
        _settings = AppSettings.Load();
        _controller = new LatchController(_settings);
        _controller.StateChanged += OnControllerStateChanged;

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Audio Key Latch",
            Visible = false,
            ContextMenuStrip = BuildTrayMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        BuildUi();
        LoadSettingsIntoUi();
        RefreshDevices();
        ApplySettings();
    }

    /// <summary>
    /// Saves settings and releases native resources, unless close-to-tray is active.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing from the window button behaves like "hide to tray" when enabled.
        // The tray Exit command flips _reallyClose so the app can actually shut down.
        if (!_reallyClose && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        _settings.Save();
        _controller.Dispose();
        _notifyIcon.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Converts minimizing into hiding to the notification area when that option is enabled.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
        {
            MinimizeToTray();
        }
    }

    /// <summary>
    /// Captures the user's next key press when configuring the activation key.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturingKey)
        {
            // Capture a plain virtual key for PTT. Modifiers are intentionally
            // stripped because the hook latches one physical key at a time.
            SetActivationKey(keyData & Keys.KeyCode);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Builds the WinForms UI entirely in code to keep the project small and easy to edit.
    /// </summary>
    private void BuildUi()
    {
        Text = "Audio PTT Latch";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 420);
        Size = new Size(620, 470);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 11,
            Padding = new Padding(18),
            AutoSize = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < 9; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _enabledCheck = new CheckBox { Text = "Enable latch", AutoSize = true };
        root.Controls.Add(_enabledCheck, 1, 0);

        _keyButton = new Button { Text = "Capture key", Height = 29, Dock = DockStyle.Fill };
        _keyButton.Click += (_, _) =>
        {
            _capturingKey = true;
            _keyButton.Text = "Press a key...";
            _keyButton.Focus();
        };
        AddRow(root, "Activation key", _keyButton, 1);

        _deviceKindCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _deviceKindCombo.Items.Add(AudioDeviceKind.Input);
        _deviceKindCombo.Items.Add(AudioDeviceKind.Output);
        _deviceKindCombo.SelectedIndexChanged += (_, _) => RefreshDevices();
        AddRow(root, "Monitor", _deviceKindCombo, 2);

        var devicePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        devicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        devicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _deviceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        var refreshButton = new Button { Text = "Refresh", Dock = DockStyle.Fill };
        refreshButton.Click += (_, _) => RefreshDevices();
        devicePanel.Controls.Add(_deviceCombo, 0, 0);
        devicePanel.Controls.Add(refreshButton, 1, 0);
        AddRow(root, "Sound device", devicePanel, 3);

        _thresholdInput = new NumericUpDown
        {
            DecimalPlaces = 3,
            Increment = 0.005M,
            Minimum = 0.001M,
            Maximum = 1.000M,
            Dock = DockStyle.Left,
            Width = 110
        };
        AddRow(root, "Activity threshold", _thresholdInput, 4);

        _silenceDurationInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 10000,
            Increment = 25,
            Dock = DockStyle.Left,
            Width = 110
        };
        AddRow(root, "Silence duration (ms)", _silenceDurationInput, 5);

        _releaseDelayInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 5000,
            Increment = 25,
            Dock = DockStyle.Left,
            Width = 110
        };
        AddRow(root, "Release delay (ms)", _releaseDelayInput, 6);

        _minimizeToTrayCheck = new CheckBox { Text = "Minimize to notification area", AutoSize = true };
        root.Controls.Add(_minimizeToTrayCheck, 1, 7);

        _levelBar = new ProgressBar { Minimum = 0, Maximum = 1000, Dock = DockStyle.Fill };
        AddRow(root, "Current level", _levelBar, 8);

        var statusPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        _statusLabel = new Label { AutoSize = true, Text = "Status: idle" };
        _deviceLabel = new Label { AutoSize = true, Text = "Device: none" };
        statusPanel.Controls.Add(_statusLabel);
        statusPanel.Controls.Add(_deviceLabel);
        root.Controls.Add(statusPanel, 1, 9);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        var applyButton = new Button { Text = "Apply", Width = 88, Height = 30 };
        applyButton.Click += (_, _) => ApplySettings();
        var saveButton = new Button { Text = "Save", Width = 88, Height = 30 };
        saveButton.Click += (_, _) =>
        {
            ApplySettings();
            _settings.Save();
        };
        var hideButton = new Button { Text = "Hide", Width = 88, Height = 30 };
        hideButton.Click += (_, _) => MinimizeToTray();
        var testButton = new Button { Text = "Test key", Width = 88, Height = 30 };
        testButton.Click += (_, _) => TestConfiguredKey();
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(applyButton);
        buttons.Controls.Add(testButton);
        buttons.Controls.Add(hideButton);
        root.Controls.Add(buttons, 0, 10);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
    }

    /// <summary>
    /// Adds one labeled row to the two-column settings layout.
    /// </summary>
    private static void AddRow(TableLayoutPanel root, string label, Control control, int row)
    {
        var rowLabel = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(rowLabel, 0, row);
        root.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// Creates the notification-area context menu.
    /// </summary>
    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _reallyClose = true;
            Close();
        });
        return menu;
    }

    /// <summary>
    /// Copies loaded settings into the UI controls before monitoring starts.
    /// </summary>
    private void LoadSettingsIntoUi()
    {
        _enabledCheck.Checked = _settings.Enabled;
        _deviceKindCombo.SelectedItem = _settings.DeviceKind;
        _thresholdInput.Value = (decimal)Math.Clamp(_settings.ActivityThreshold, 0.001f, 1f);
        _silenceDurationInput.Value = Math.Clamp(_settings.SilenceDurationMs, 0, 10000);
        _releaseDelayInput.Value = Math.Clamp(_settings.ReleaseDelayMs, 0, 5000);
        _minimizeToTrayCheck.Checked = _settings.MinimizeToTray;
        _keyButton.Text = ((Keys)_settings.ActivationKey).ToString();
    }

    /// <summary>
    /// Reads the UI controls back into settings and starts, stops, or reconfigures monitoring.
    /// </summary>
    private void ApplySettings()
    {
        if (_deviceCombo.SelectedItem is AudioEndpoint endpoint)
        {
            _settings.DeviceId = endpoint.Id;
            _settings.DeviceKind = endpoint.Kind;
        }
        else
        {
            _settings.DeviceId = null;
            _settings.DeviceKind = (AudioDeviceKind)_deviceKindCombo.SelectedItem!;
        }

        _settings.Enabled = _enabledCheck.Checked;
        _settings.ActivityThreshold = (float)_thresholdInput.Value;
        _settings.SilenceDurationMs = (int)_silenceDurationInput.Value;
        _settings.ReleaseDelayMs = (int)_releaseDelayInput.Value;
        _settings.MinimizeToTray = _minimizeToTrayCheck.Checked;

        try
        {
            if (_settings.Enabled)
            {
                if (_controller.IsRunning)
                {
                    _controller.Reconfigure(_settings);
                }
                else
                {
                    _controller.Start();
                }
            }
            else
            {
                _controller.Stop();
            }

            _statusLabel.Text = _controller.IsRunning ? "Status: listening" : "Status: disabled";
            _deviceLabel.Text = _deviceCombo.SelectedItem is AudioEndpoint selected
                ? $"Device: {selected.Name}"
                : "Device: default";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Status: failed to start";
            var deviceName = _deviceCombo.SelectedItem is AudioEndpoint selected
                ? selected.Name
                : "default device";
            MessageBox.Show(
                this,
                $"Could not start monitoring {_settings.DeviceKind} device '{deviceName}'.\n\n{ex.Message}",
                "Audio PTT Latch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Rebuilds the device dropdown from active Windows input/output endpoints.
    /// </summary>
    private void RefreshDevices()
    {
        if (_deviceKindCombo.SelectedItem == null)
        {
            _deviceKindCombo.SelectedItem = _settings.DeviceKind;
        }

        var kind = (AudioDeviceKind)_deviceKindCombo.SelectedItem!;
        _deviceCombo.Items.Clear();

        IReadOnlyList<AudioEndpoint> endpoints;
        try
        {
            endpoints = new[]
            {
                new AudioEndpoint(null, kind == AudioDeviceKind.Input ? "Default input device" : "Default output device", kind)
            }
            .Concat(CoreAudioMeter.Enumerate(kind))
            .ToArray();
        }
        catch (Exception ex)
        {
            endpoints =
            [
                new AudioEndpoint(null, kind == AudioDeviceKind.Input ? "Default input device" : "Default output device", kind)
            ];
            _statusLabel.Text = $"Status: using default device only ({ex.Message})";
        }

        foreach (var endpoint in endpoints)
        {
            _deviceCombo.Items.Add(endpoint);
        }

        // Prefer the saved endpoint, then the Windows default endpoint, then the
        // first active endpoint so the app remains usable after device changes.
        var desiredId = kind == _settings.DeviceKind ? _settings.DeviceId : null;
        var selected = endpoints.FirstOrDefault(device => device.Id == desiredId) ?? endpoints.FirstOrDefault();
        if (selected != null)
        {
            _deviceCombo.SelectedItem = selected;
        }
    }

    /// <summary>
    /// Stores a newly captured activation key and updates the key button label.
    /// </summary>
    private void SetActivationKey(Keys key)
    {
        if (key == Keys.None)
        {
            return;
        }

        _capturingKey = false;
        _settings.ActivationKey = (int)key;
        _keyButton.Text = key.ToString();
    }

    /// <summary>
    /// Sends one configured key press so the user can validate SendInput in Notepad or another target app.
    /// </summary>
    private void TestConfiguredKey()
    {
        _statusLabel.Text = "Status: test key sends in 2 seconds";
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            try
            {
                KeySender.SendKeyPress((Keys)_settings.ActivationKey);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Audio PTT Latch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Updates status text and the live level meter when the controller reports changes.
    /// </summary>
    private void OnControllerStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        var peak = Math.Clamp(_controller.LastPeak, 0f, 1f);
        _levelBar.Value = Math.Clamp((int)(peak * 1000), _levelBar.Minimum, _levelBar.Maximum);

        if (!_controller.IsRunning)
        {
            _statusLabel.Text = "Status: disabled";
        }
        else if (_controller.IsLatched)
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(_controller.LastInputError)
                ? "Status: latched, waiting for silence"
                : $"Status: input send failed ({_controller.LastInputError})";
        }
        else
        {
            _statusLabel.Text = "Status: listening";
        }
    }

    /// <summary>
    /// Hides the window and shows the notification-area icon, if enabled.
    /// </summary>
    private void MinimizeToTray()
    {
        if (!_settings.MinimizeToTray)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }

        _notifyIcon.Visible = true;
        Hide();
    }

    /// <summary>
    /// Restores the main window from the notification area.
    /// </summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _notifyIcon.Visible = false;
    }
}
