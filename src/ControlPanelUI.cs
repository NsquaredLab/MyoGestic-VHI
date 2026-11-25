using Godot;

namespace Vhi;

/// <summary>
/// Runtime control panel - the collapsible UI on the right edge of the scene
/// that lets the operator tweak the hands without restarting the app or opening
/// the Godot editor.
///
/// Hosts:
/// <list type="bullet">
///   <item><description>sliders for the control-hand movement timing
///     (<c>Frequency</c> / <c>HoldTime</c> / <c>RestTime</c>);</description></item>
///   <item><description>a freeze toggle (calls <see cref="ControlHandSkeleton.SetFrozen"/>);</description></item>
///   <item><description>a chirality toggle (currently disabled - both hands stay left);</description></item>
///   <item><description>a smoothing toggle + speed for the predicted hand
///     (<see cref="PredictedHandSkeleton.SetSmoothing"/>);</description></item>
///   <item><description>buttons to <b>Load Config File</b> (open a file dialog and
///     point VHI at any TOML on disk) and <b>Edit Config File</b>
///     (<c>OS.ShellOpen</c> the current config in the system text editor);</description></item>
///   <item><description>the <b>Connection</b> setting: where the hand looks for its pose
///     streams (<see cref="LSLWrapper.Connection"/>), with a live count of them. A change
///     needs a restart, because liblsl reads its config once per process.</description></item>
/// </list>
///
/// Collapsed by default; toggled with the <c>&gt;</c> chevron on the right edge, or the
/// stream-count chip beside it.
/// No driver-mode toggle - that is set in the Inspector or over gRPC
/// (the gRPC control plane).
/// </summary>
public partial class ControlPanelUI : Control
{
	// References to hand controllers
	private ControlHandSkeleton controlHand;
	private PredictedHandSkeleton predictedHand;

	// Control Hand UI elements
	private HSlider speedSlider;
	private Label speedValueLabel;
	private HSlider holdingTimeSlider;
	private Label holdingTimeValueLabel;
	private HSlider restingTimeSlider;
	private Label restingTimeValueLabel;

	// Predicted Hand UI elements
	private CheckBox smoothingToggle;
	private HSlider smoothingSpeedSlider;
	private Label smoothingSpeedValueLabel;
	private VBoxContainer smoothingSpeedContainer;

	// Collapse functionality
	private Button collapseButton;
	private Panel mainPanel;
	private bool isCollapsed = true; // Start collapsed

	// Hand chirality toggle

	// Config file buttons
	private Button openConfigButton;
	private Button loadConfigButton;
	private Label configFileLabel;

	// Freeze button
	private Button freezeButton;

	// Connection: where the hand looks for its pose streams (LSLWrapper.Connection)
	private LSLCommunicationController lsl;
	private Label statusDot;
	private Label statusLabel;
	private Label sourceLabel;
	private Label hintLabel;
	private Label addressLabel;
	private LineEdit addressEdit;
	private Control restartBox;
	private Button restartButton;
	private Button statusChip;
	private LSLWrapper.ConnectionMode chosenMode;
	private double statusClock;
	private static readonly Color LiveColor = new(0.48f, 0.88f, 0.63f);
	private static readonly Color WaitingColor = new(1f, 0.77f, 0.42f);

	// UI scaling

	// Public property to access hand chirality

	public override void _Ready()
	{
		// Get references to hand controllers
		controlHand = GetNode<ControlHandSkeleton>("/root/Main/ControlHand");
		predictedHand = GetNode<PredictedHandSkeleton>("/root/Main/PredictedHand");

		// Get collapse button and panel
		collapseButton = GetNode<Button>("CollapseButton");
		mainPanel = GetNode<Panel>("Panel");

		// Set initial collapsed state
		mainPanel.Visible = !isCollapsed;
		collapseButton.Text = isCollapsed ? ">" : "<";

		// Get hand chirality toggle

		// Get config buttons
		openConfigButton = GetNode<Button>("Panel/Scroll/VBoxContainer/ConfigGroup/OpenConfigButton");
		loadConfigButton = GetNode<Button>("Panel/Scroll/VBoxContainer/ConfigGroup/LoadConfigButton");
		configFileLabel = GetNode<Label>("Panel/Scroll/VBoxContainer/ConfigGroup/ConfigFileLabel");


		// Get freeze button
		freezeButton = GetNode<Button>("Panel/Scroll/VBoxContainer/ControlHandGroup/FreezeButton");

		// Set initial config file label
		UpdateConfigFileLabel();

		// Get UI element references
		speedSlider = GetNode<HSlider>("Panel/Scroll/VBoxContainer/ControlHandGroup/SpeedContainer/SpeedSlider");
		speedValueLabel = GetNode<Label>("Panel/Scroll/VBoxContainer/ControlHandGroup/SpeedContainer/SpeedValue");

		holdingTimeSlider = GetNode<HSlider>("Panel/Scroll/VBoxContainer/ControlHandGroup/HoldingTimeContainer/HoldingTimeSlider");
		holdingTimeValueLabel = GetNode<Label>("Panel/Scroll/VBoxContainer/ControlHandGroup/HoldingTimeContainer/HoldingTimeValue");

		restingTimeSlider = GetNode<HSlider>("Panel/Scroll/VBoxContainer/ControlHandGroup/RestingTimeContainer/RestingTimeSlider");
		restingTimeValueLabel = GetNode<Label>("Panel/Scroll/VBoxContainer/ControlHandGroup/RestingTimeContainer/RestingTimeValue");

		smoothingToggle = GetNode<CheckBox>("Panel/Scroll/VBoxContainer/PredictedHandGroup/SmoothingToggle");
		smoothingSpeedContainer = GetNode<VBoxContainer>("Panel/Scroll/VBoxContainer/PredictedHandGroup/SmoothingSpeedContainer");
		smoothingSpeedSlider = GetNode<HSlider>("Panel/Scroll/VBoxContainer/PredictedHandGroup/SmoothingSpeedContainer/SmoothingSpeedSlider");
		smoothingSpeedValueLabel = GetNode<Label>("Panel/Scroll/VBoxContainer/PredictedHandGroup/SmoothingSpeedContainer/SmoothingSpeedValue");

		// Set initial values from hand controllers
		speedSlider.Value = controlHand.Frequency;
		holdingTimeSlider.Value = controlHand.HoldTime;
		restingTimeSlider.Value = controlHand.RestTime;
		smoothingToggle.ButtonPressed = predictedHand.EnableSmoothing;
		smoothingSpeedSlider.Value = predictedHand.SmoothingSpeed;

		// Update labels
		UpdateSpeedLabel(speedSlider.Value);
		UpdateHoldingTimeLabel(holdingTimeSlider.Value);
		UpdateRestingTimeLabel(restingTimeSlider.Value);
		UpdateSmoothingSpeedLabel(smoothingSpeedSlider.Value);
		UpdateSmoothingSpeedVisibility(smoothingToggle.ButtonPressed);

		// Connect signals
		speedSlider.ValueChanged += OnSpeedChanged;
		holdingTimeSlider.ValueChanged += OnHoldingTimeChanged;
		restingTimeSlider.ValueChanged += OnRestingTimeChanged;
		smoothingToggle.Toggled += OnSmoothingToggled;
		smoothingSpeedSlider.ValueChanged += OnSmoothingSpeedChanged;
		collapseButton.Pressed += OnCollapseToggled;
		openConfigButton.Pressed += OnOpenConfigPressed;
		loadConfigButton.Pressed += OnLoadConfigPressed;
		freezeButton.Pressed += OnFreezePressed;

		ReadyConnection();
	}

	private void ReadyConnection()
	{
		const string group = "Panel/Scroll/VBoxContainer/ConnectionGroup";
		lsl = GetNode<LSLCommunicationController>("/root/Main/LSLCommunicationController");
		statusDot = GetNode<Label>($"{group}/StatusRow/StatusDot");
		statusLabel = GetNode<Label>($"{group}/StatusRow/StatusLabel");
		sourceLabel = GetNode<Label>($"{group}/SourceLabel");
		hintLabel = GetNode<Label>($"{group}/HintLabel");
		addressLabel = GetNode<Label>($"{group}/Options/AddressLabel");
		addressEdit = GetNode<LineEdit>($"{group}/Options/AddressEdit");
		restartBox = GetNode<Control>($"{group}/RestartBox");
		restartButton = GetNode<Button>($"{group}/RestartBox/RestartRows/RestartButton");
		statusChip = GetNode<Button>("StatusChip");
		var defaultOption = GetNode<CheckBox>($"{group}/Options/DefaultOption");
		var peerOption = GetNode<CheckBox>($"{group}/Options/PeerOption");

		// The first option is where this platform looks by default: itself, or on a phone
		// (which never runs MyoGestic) the network. Windows keeps liblsl's defaults, so there
		// is nothing to choose there.
		bool phone = OS.GetName() == "Android";
		var defaultMode = phone ? LSLWrapper.ConnectionMode.Network : LSLWrapper.ConnectionMode.ThisComputer;
		GetNode<Control>($"{group}/Options").Visible = !System.OperatingSystem.IsWindows();
		if (phone)
		{
			GetNode<Label>($"{group}/Options/FindLabel").Text = "Find MyoGestic by";
			defaultOption.Text = "Searching the network";
			peerOption.Text = "Its IP address";
			addressLabel.Text = "IP of the computer running MyoGestic";
			GetNode<Label>($"{group}/RestartBox/RestartRows/RestartNote").Text =
				"Takes effect when VHI is reopened. Android can't restart the app for you.";
			restartButton.Text = "Close VHI";
		}

		var (mode, peer) = LSLWrapper.Connection;
		chosenMode = mode;
		(mode == LSLWrapper.ConnectionMode.Peer ? peerOption : defaultOption).ButtonPressed = true;
		addressEdit.Text = peer;
		defaultOption.Toggled += on => { if (on) { chosenMode = defaultMode; UpdateConnectionChoice(); } };
		peerOption.Toggled += on => { if (on) { chosenMode = LSLWrapper.ConnectionMode.Peer; UpdateConnectionChoice(); } };
		addressEdit.TextChanged += _ => UpdateConnectionChoice();
		restartButton.Pressed += OnRestartPressed;
		statusChip.Pressed += OnCollapseToggled;
		UpdateConnectionChoice();
	}

	/// <summary>Show the address field for a peer, and the restart notice while the choice
	/// differs from what this process applied.</summary>
	private void UpdateConnectionChoice()
	{
		var (mode, peer) = LSLWrapper.Connection;
		string address = addressEdit.Text.Trim();
		bool toPeer = chosenMode == LSLWrapper.ConnectionMode.Peer;
		addressLabel.Visible = addressEdit.Visible = toPeer;
		restartBox.Visible = chosenMode != mode || (toPeer && address != peer);
		restartButton.Disabled = toPeer && !LSLWrapper.IsValidPeer(address);
	}

	private void OnRestartPressed()
	{
		LSLWrapper.SaveConnection(chosenMode, addressEdit.Text.Trim());
		// liblsl reads its config once per process, so the setting needs a new one.
		// Android cannot relaunch an app; the note there says to reopen it.
		if (OS.GetName() != "Android")
			OS.SetRestartOnExit(true, OS.GetCmdlineArgs());
		GetTree().Quit();
	}

	public override void _Process(double delta)
	{
		if ((statusClock -= delta) > 0)
			return;
		statusClock = 0.5;

		int live = lsl.LiveStreams;
		var (mode, peer) = LSLWrapper.Connection;
		int wait = Mathf.CeilToInt((float)lsl.SecondsToNextResolve);
		string next = wait > 0 ? $"next try in {wait} s" : "looking now";
		Color color = live > 0 ? LiveColor : WaitingColor;
		statusDot.Modulate = color;
		statusLabel.Text = live > 0 ? $"Receiving {live} pose stream{(live == 1 ? "" : "s")}" : "No pose streams yet";
		sourceLabel.Text = (mode, live > 0) switch
		{
			(LSLWrapper.ConnectionMode.Peer, true) => $"Asking {peer} only",
			(LSLWrapper.ConnectionMode.Peer, false) => $"No answer from {peer} · {next}",
			(LSLWrapper.ConnectionMode.Network, _) => "Searching the network",
			(_, true) => "Looking on this computer only",
			_ => $"Looking on this computer only · {next}",
		};
		hintLabel.Visible = mode == LSLWrapper.ConnectionMode.Peer && live == 0;
		statusChip.Visible = isCollapsed;
		statusChip.Text = live > 0 ? $"● {live} pose stream{(live == 1 ? "" : "s")}" : "● No pose streams";
		statusChip.AddThemeColorOverride("font_color", color);
	}

	private void OnSpeedChanged(double value)
	{
		controlHand.Frequency = (float)value;
		UpdateSpeedLabel(value);
	}

	private void OnHoldingTimeChanged(double value)
	{
		controlHand.HoldTime = (float)value;
		UpdateHoldingTimeLabel(value);
	}

	private void OnRestingTimeChanged(double value)
	{
		controlHand.RestTime = (float)value;
		UpdateRestingTimeLabel(value);
	}

	private void OnSmoothingToggled(bool enabled)
	{
		predictedHand.EnableSmoothing = enabled;
		UpdateSmoothingSpeedVisibility(enabled);
	}

	private void OnSmoothingSpeedChanged(double value)
	{
		predictedHand.SmoothingSpeed = (float)value;
		UpdateSmoothingSpeedLabel(value);
	}

	private void UpdateSpeedLabel(double value)
	{
		speedValueLabel.Text = $"{value:F2}";
	}

	private void UpdateHoldingTimeLabel(double value)
	{
		holdingTimeValueLabel.Text = $"{value:F1}s";
	}

	private void UpdateRestingTimeLabel(double value)
	{
		restingTimeValueLabel.Text = $"{value:F1}s";
	}

	private void UpdateSmoothingSpeedLabel(double value)
	{
		smoothingSpeedValueLabel.Text = $"{value:F1}";
	}

	private void UpdateSmoothingSpeedVisibility(bool visible)
	{
		smoothingSpeedContainer.Visible = visible;
	}

	private void OnCollapseToggled()
	{
		isCollapsed = !isCollapsed;
		mainPanel.Visible = !isCollapsed;
		collapseButton.Text = isCollapsed ? ">" : "<";
		statusChip.Visible = isCollapsed;
	}

	private void OnFreezePressed()
	{
		controlHand.ToggleFreeze();
		freezeButton.Text = controlHand.IsFrozen ? "Unfreeze (Space)" : "Freeze Pose (Space)";
	}

	private void UpdateConfigFileLabel()
	{
		string path = controlHand.ConfigFilePath;
		// Show just the filename
		configFileLabel.Text = path.GetFile();
		configFileLabel.TooltipText = ProjectSettings.GlobalizePath(path);
	}

	private void OnLoadConfigPressed()
	{
		string currentPath = ProjectSettings.GlobalizePath(controlHand.ConfigFilePath);
		string dir = System.IO.Path.GetDirectoryName(currentPath);
		if (!System.IO.Directory.Exists(dir))
			dir = OS.GetSystemDir(OS.SystemDir.Desktop);

		var callback = Callable.From<bool, string[], int>(OnNativeFileSelected);
		DisplayServer.FileDialogShow(
			"Load Movement Config",
			dir,
			"",
			false,
			DisplayServer.FileDialogMode.OpenFile,
			["*.toml"],
			callback
		);
	}

	private void OnNativeFileSelected(bool selected, string[] paths, int filterIndex)
	{
		if (selected && paths.Length > 0)
		{
			controlHand.LoadConfigFile(paths[0]);
			UpdateConfigFileLabel();
		}
	}

	private void OnOpenConfigPressed()
	{
		// Get the config file path from ControlHandSkeleton
		string configPath = controlHand.ConfigFilePath;

		// Convert Godot path (user://) to absolute system path
		string absolutePath = ProjectSettings.GlobalizePath(configPath);

		// Check if file exists, create it if it doesn't
		if (!System.IO.File.Exists(absolutePath))
		{
			GD.Print($"Config file not found at {absolutePath}, generating...");
			MovementConfigGenerator.GenerateDefaultConfig(absolutePath);
		}

		// Open with default system editor
		try
		{
			OS.ShellOpen(absolutePath);
			GD.Print($"Opening config file: {absolutePath}");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"Failed to open config file: {e.Message}");
		}
	}
}
