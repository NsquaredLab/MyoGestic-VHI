using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace Vhi;

/// <summary>
/// The "control" hand - the ground-truth / cued hand on the left of the scene.
///
/// Drives 16 finger joints in one of three modes selected by <see cref="DriverMode"/>:
/// <list type="bullet">
///   <item><description><b>Movement</b> (default): plays a named predefined movement
///     through the state machine <i>waiting → closing → holding → opening → resting</i>,
///     and listens for ←/→/↑/↓ keyboard input to cycle/start/stop. Movement poses
///     come from the TOML loaded via <see cref="MovementConfigLoader"/>; the available
///     set is filtered by <see cref="Mode"/> (AI vs Classifier).</description></item>
///   <item><description><b>Stream</b>: ignores the state machine and follows a continuous
///     9-DOF pose streamed in over the <c>MyoGestic_ControlPose</c> LSL inlet
///     (consumed via <see cref="LSLCommunicationController"/>).</description></item>
/// </list>
///
/// Frame-by-frame animation logic runs in <c>_Process</c>; the resulting pose is
/// published to the <c>VHI_Control</c> LSL outlet in <c>_PhysicsProcess</c>.
/// Commands originate from the gRPC control services (via the public
/// command API on this class) and from local keyboard input - the same methods are
/// called either way.
/// </summary>
public partial class ControlHandSkeleton : HandSkeleton
{
	/// <summary>How the control hand is driven each frame -
	/// <see cref="ControlHandDriverMode.Movement"/>,
	/// <see cref="ControlHandDriverMode.Stream"/>. Change at runtime with
	/// <see cref="SetDriverMode"/>, itself driven by the v2 Declare handshake.</summary>
	[Export] public ControlHandDriverMode DriverMode = ControlHandDriverMode.Movement;

	/// <summary>Godot resource path to the movements TOML config. Defaults to
	/// <c>user://movements.toml</c>; auto-generated from the hard-coded poses
	/// on first run, and hot-reloaded on change. To load a TOML from
	/// elsewhere at runtime, use the control panel's "Load Config File"
	/// button or call <see cref="LoadConfigFile"/>.</summary>
	[Export] public string ConfigFilePath = "user://movements.toml";

	/// <summary>Movement cycles per second in
	/// <see cref="ControlHandDriverMode.Movement"/> - the closing/opening
	/// interpolation speed. Live-adjustable via the control panel or the
	/// gRPC <c>SetSpeed</c> RPC.</summary>
	[Export] public float Frequency = 0.5f;

	/// <summary>Seconds held at max flexion in each movement cycle.</summary>
	[Export] public float HoldTime = 1.0f;

	/// <summary>Seconds held at rest in each movement cycle.</summary>
	[Export] public float RestTime = 1.0f;

	private LSLCommunicationController communicationController;
	private List<float> currentData = [];
	// Set on every live frame, read on the next non-live one, so _Process can tell a
	// falling edge (the stream just went stale) from an already-stale stream.
	private bool wasControlPoseLive = false;

	// Movement control system
	private Dictionary<string, float[][][]> movementPoses;
	private string[] availableMovements;
	private int currentMovementIndex = 0;
	private string animationState = "waiting";  // waiting, closing, holding, opening, resting, frozen
	private float animationArgument = 0.0f;
	private float stateTimer = 0.0f;
	private FileSystemWatcher configWatcher;  // Watches for config file changes

	public override void _Ready()
	{
		GD.Print("=== Control Hand Skeleton Controller _Ready() START ===");

		// Get communication controller
		GD.Print("  Getting communication controller...");
		communicationController = GetNode<LSLCommunicationController>("/root/Main/LSLCommunicationController");
		GD.Print("  Communication controller found");

		FindAndMapSkeleton();

		LoadMovementConfig();
		if (movementPoses != null && availableMovements != null && availableMovements.Length > 0)
		{
			GD.Print($"  {availableMovements.Length} movements; current: {availableMovements[currentMovementIndex]}");
			SetupConfigWatcher();  // hot-reload
		}
		else
		{
			GD.PrintErr("  Failed to load movements - using empty movement list");
			availableMovements = [];
		}

		// Apply skin color material to the hand mesh
		ApplySkinColor();

		GD.Print("=== Control Hand Skeleton Controller _Ready() COMPLETE ===");
	}

	public override void _Process(double delta)
	{
		// Presence decides, not a handshake. A control-pose stream that is delivering is a
		// client driving this hand; one that is not is a client that stopped, or was never
		// there. The predicted hand has always worked this way — it renders whatever arrives
		// on MyoGestic_Output — and the two hands differing on that was the whole reason
		// Declare had a side effect.
		if (communicationController != null && communicationController.ControlPoseLive)
		{
			currentData = communicationController.GetReceivedDataControl();
			// Standard values mean +1 is the direction the channel's name denotes. They stay
			// standard from here: MoveBonesFromStream multiplies by StandardPose.AtPlusOne,
			// so only the domain clamp is owed.
			StandardPose.Clamp(currentData);
			if (currentData.Count >= 9 && skeleton != null)
				MoveBonesFromStream();
			wasControlPoseLive = true;
			return;
		}

		// Falling edge: the producer that was driving this hand went quiet. Give the hand
		// back to its own movements rather than hold the last streamed pose forever — the
		// same contract LSLCommunicationController keeps for the predicted hand's buffer.
		if (wasControlPoseLive)
		{
			StopToRest();
			wasControlPoseLive = false;
		}

		HandleMovementInput();
		UpdateMovementAnimation((float)delta);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (communicationController != null && skeleton != null && boneMap.Count > 0)
			communicationController.SendControlData(ReadStandardPose());
	}

	private void MoveBonesFromStream()
	{
		if (skeleton == null || boneMap.Count == 0)
			return;


		// Wrist (indices 6, 7, 8: flexion, abduction, rotation)
		SetBoneRotation(0, currentData[6] * StandardPose.AtPlusOne[0][0], currentData[8] * StandardPose.AtPlusOne[0][1], currentData[7] * StandardPose.AtPlusOne[0][2]);

		// Thumb (uses indices 0 and 1: flexion and abduction)
		SetBoneRotation(1, currentData[0] * StandardPose.AtPlusOne[1][0], 0, currentData[1] * StandardPose.AtPlusOne[1][2]);
		SetBoneRotation(2, currentData[0] * StandardPose.AtPlusOne[2][0], 0, currentData[1] * StandardPose.AtPlusOne[2][2]);
		SetBoneRotation(3, currentData[0] * StandardPose.AtPlusOne[3][0], 0, currentData[1] * StandardPose.AtPlusOne[3][2]);

		// Index (uses index 2)
		SetBoneRotation(4, currentData[2] * StandardPose.AtPlusOne[4][0], 0, 0);
		SetBoneRotation(5, currentData[2] * StandardPose.AtPlusOne[5][0], 0, 0);
		SetBoneRotation(6, currentData[2] * StandardPose.AtPlusOne[6][0], 0, 0);

		// Middle (uses index 3)
		SetBoneRotation(7, currentData[3] * StandardPose.AtPlusOne[7][0], 0, 0);
		SetBoneRotation(8, currentData[3] * StandardPose.AtPlusOne[8][0], 0, 0);
		SetBoneRotation(9, currentData[3] * StandardPose.AtPlusOne[9][0], 0, 0);

		// Ring (uses index 4)
		SetBoneRotation(10, currentData[4] * StandardPose.AtPlusOne[10][0], 0, 0);
		SetBoneRotation(11, currentData[4] * StandardPose.AtPlusOne[11][0], 0, 0);
		SetBoneRotation(12, currentData[4] * StandardPose.AtPlusOne[12][0], 0, 0);

		// Pinky (uses index 5)
		SetBoneRotation(13, currentData[5] * StandardPose.AtPlusOne[13][0], 0, 0);
		SetBoneRotation(14, currentData[5] * StandardPose.AtPlusOne[14][0], 0, 0);
		SetBoneRotation(15, currentData[5] * StandardPose.AtPlusOne[15][0], 0, 0);
	}

	// ========== MOVEMENT CONTROL SYSTEM ==========

	private void HandleMovementInput()
	{
		// While a MyoGestic recording session is active, MyoGestic is the sole
		// movement authority — ignore local keyboard control.
		if (SessionActive)
			return;

		// Left / Right Arrow - cycle the selected movement
		if (Input.IsActionJustPressed("ui_left"))
			CycleMovement(-1);
		if (Input.IsActionJustPressed("ui_right"))
			CycleMovement(1);

		// Down Arrow - start the selected movement
		if (Input.IsActionJustPressed("ui_down"))
			StartCurrentMovement();

		// Up Arrow - stop, return to rest
		if (Input.IsActionJustPressed("ui_up"))
			StopToRest();

		// Space - toggle freeze (hold at max flexion)
		if (Input.IsActionJustPressed("ui_accept"))
			ToggleFreeze();
	}

	// ===== Programmatic command API =====
	// Shared by keyboard input (above) and the gRPC control service
	// (the gRPC control services). All of these run on Godot's main thread.

	/// <summary>True when a MyoGestic recording session is currently active.
	/// While set, VHI's local keyboard input is gated off so the gRPC client
	/// is the sole movement source. Toggled via the
	/// <c>VhiControl.SetRecordingSession</c> RPC.</summary>
	public bool SessionActive { get; set; } = false;

	// --- recording trajectories (v2 recording aid) --------------------------------
	//
	// A recording trajectory is a *recording* concern, not a control one: it
	// deliberately keeps the control hand moving so the VHI_Control stream sweeps a
	// continuous kinematic range for EMG windows to be aligned against. It is built
	// out of the existing movement machinery (SetSpeed + a cycling SetMovement)
	// rather than a second animation path, so there is only ever one thing driving
	// these bones.

	/// <summary>Whether a v2 recording trajectory is cycling this hand right now.</summary>
	/// <remarks>
	/// Tracked here rather than in the gRPC service because the service is constructed
	/// per call — grpc-dotnet's activator gives every RPC a fresh instance, so a flag
	/// held there would always read false. The hand is the thing that is running a
	/// trajectory, so the hand is where the fact belongs.
	/// </remarks>
	public bool RecordingTrajectoryActive { get; private set; }

	/// <summary>The movement a running recording trajectory is cycling, or empty.</summary>
	public string RecordingTrajectoryMovement { get; private set; } = "";

	/// <summary>
	/// Begin cycling <paramref name="movement"/> to generate a recording trajectory.
	/// </summary>
	/// <remarks>
	/// Non-positive <paramref name="frequencyHz"/> and negative hold/rest times leave
	/// VHI's current timing alone, matching <see cref="SetSpeed"/>.
	/// </remarks>
	/// <returns><see langword="false"/> if the hand is not in Movement mode or the
	/// movement name is unknown — in which case nothing was started.</returns>
	public bool StartRecordingTrajectory(string movement, float frequencyHz, float holdTimeS, float restTimeS)
	{
		SetSpeed(frequencyHz, holdTimeS, restTimeS);
		// cycle:true is the whole difference from a discrete control command: the hand
		// sweeps rest -> flex -> hold -> release repeatedly instead of snapping and
		// holding, which is what makes the recorded stream a continuous range.
		if (!SetMovement(movement, true))
			return false;
		RecordingTrajectoryActive = true;
		RecordingTrajectoryMovement = movement;
		GD.Print($"Recording trajectory started: {movement} (freq={Frequency} hold={HoldTime} rest={RestTime})");
		return true;
	}

	/// <summary>Stop a recording trajectory, resting the hand only if one was running. Idempotent.</summary>
	public void StopRecordingTrajectory()
	{
		// Rest only if a trajectory was actually running. Teardown paths call this
		// without knowing, and resting regardless erased a held state the control
		// path had set — the caller asked to stop a trajectory, not to move the hand.
		bool wasRunning = RecordingTrajectoryActive;
		RecordingTrajectoryActive = false;
		RecordingTrajectoryMovement = "";
		if (!wasRunning)
			return;
		// A trajectory stopped mid-cycle would otherwise leave the hand holding a
		// half-flexed pose, and the recording that follows would start from somewhere
		// arbitrary.
		SetMovement("Rest", false);
	}

	/// <summary>Movement names valid for the current mode.</summary>
	public string[] GetAvailableMovements() => availableMovements ?? [];

	/// <summary>"AI" or "Classifier".</summary>

	/// <summary>
	/// Cycle the selected movement by <paramref name="delta"/> steps (e.g. -1 /
	/// +1, wrapping) and reset to the waiting state.
	/// </summary>
	/// <param name="delta">Direction and magnitude of the cycle step. Pass
	/// <c>+1</c> for the next movement, <c>-1</c> for the previous; wraps at
	/// the ends of the available-movements list.</param>
	public void CycleMovement(int delta)
	{
		if (availableMovements == null || availableMovements.Length == 0)
			return;

		int n = availableMovements.Length;
		currentMovementIndex = ((currentMovementIndex + delta) % n + n) % n;
		ResetBones();
		animationState = "waiting";
		stateTimer = 0;
		GD.Print($"Movement selected: {availableMovements[currentMovementIndex]}");
	}

	/// <summary>Start playing the currently-selected movement from rest.</summary>
	public void StartCurrentMovement()
	{
		if (availableMovements == null || availableMovements.Length == 0)
			return;

		animationState = "closing";
		stateTimer = 0;
		animationArgument = 0.0f;
		GD.Print($"▼ START movement: {availableMovements[currentMovementIndex]}");
	}

	/// <summary>
	/// Select a movement by name and start playing it. The name "Rest" returns
	/// the hand to its resting state. Returns false if the name is neither a
	/// known movement nor "Rest".
	/// </summary>
	/// <param name="name">A name from <see cref="GetAvailableMovements"/>, or
	/// the special value <c>"Rest"</c> to return to the resting state.</param>
	/// <param name="cycle">If <see langword="false"/> (default), snap to the
	/// movement's end pose and hold it - the right behaviour for a classifier
	/// output. If <see langword="true"/>, play the open/close cycle in a loop -
	/// used when recording regression data so VHI_Control sweeps a continuous
	/// kinematic range.</param>
	/// <returns><see langword="true"/> if the command was applied;
	/// <see langword="false"/> if <paramref name="name"/> was rejected.</returns>
	public bool SetMovement(string name, bool cycle = false)
	{
		// Movement commands only apply when no control-pose stream is driving this hand —
		// the same presence check _Process itself uses, so the two can never disagree.
		if ((communicationController != null && communicationController.ControlPoseLive)
			|| availableMovements == null)
			return false;

		int idx = Array.IndexOf(availableMovements, name);
		if (idx >= 0)
		{
			currentMovementIndex = idx;
			ResetBones();
			if (cycle)
			{
				// Play the open/close movement cycle — used when recording
				// regression data so the control-hand kinematics sweep a
				// continuous range for the model to regress against.
				StartCurrentMovement();
			}
			else
			{
				// Discrete state command (e.g. a classifier output): snap to
				// the movement's end pose and hold it.
				animationState = "frozen";
				animationArgument = Mathf.Pi * 0.5f;
				stateTimer = 0;
				GD.Print($"SetMovement: holding '{name}' end pose");
			}
			return true;
		}

		if (string.Equals(name, "Rest", StringComparison.OrdinalIgnoreCase))
		{
			StopToRest();
			return true;
		}

		return false;
	}

	/// <summary>Stop any movement and return the hand to its resting state.</summary>
	public void StopToRest()
	{
		animationState = "waiting";
		stateTimer = 0;
		ResetBones();
		GD.Print("▲ STOP movement");
	}

	/// <summary>
	/// Set the control-hand animation timing (the UI speed/hold/rest sliders).
	/// A non-positive frequency is ignored; negative hold/rest values are ignored.
	/// </summary>
	/// <param name="frequencyHz">Movement cycles per second. Values &lt;= 0
	/// leave the current frequency unchanged.</param>
	/// <param name="holdTimeS">Seconds to hold at max flexion in each cycle.
	/// Negative values are ignored.</param>
	/// <param name="restTimeS">Seconds to hold at rest in each cycle. Negative
	/// values are ignored.</param>
	public void SetSpeed(float frequencyHz, float holdTimeS, float restTimeS)
	{
		if (communicationController != null && communicationController.ControlPoseLive)
			return;
		if (frequencyHz > 0) Frequency = frequencyHz;
		if (holdTimeS >= 0) HoldTime = holdTimeS;
		if (restTimeS >= 0) RestTime = restTimeS;
		GD.Print($"Speed set: freq={Frequency} hold={HoldTime} rest={RestTime}");
	}

	/// <summary>Toggle freeze mode (used by keyboard input).</summary>
	public void ToggleFreeze() => SetFrozen(!IsFrozen);

	/// <summary>
	/// Freeze the control hand at its current pose, or release it back to the
	/// resting state.
	/// </summary>
	/// <param name="frozen"><see langword="true"/> to hold the live pose
	/// indefinitely; <see langword="false"/> to release back to the resting
	/// state and resume the normal movement state machine.</param>
	public void SetFrozen(bool frozen)
	{
		if (communicationController != null && communicationController.ControlPoseLive)
			return;
		if (frozen)
		{
			// Hold wherever the animation currently is — animationArgument is
			// left untouched so the live pose freezes in place.
			animationState = "frozen";
			stateTimer = 0;
			GD.Print($"■ FREEZE movement: {availableMovements[currentMovementIndex]}");
		}
		else
		{
			animationState = "waiting";
			stateTimer = 0;
			ResetBones();
			GD.Print("▲ UNFREEZE movement");
		}
	}

	/// <summary>True while the control hand is in the frozen state - set by
	/// <see cref="SetFrozen"/> with <c>frozen=true</c>, or by
	/// <see cref="SetMovement"/> with <c>cycle=false</c> after reaching the
	/// end pose.</summary>
	public bool IsFrozen => animationState == "frozen";

	/// <summary>
	/// Set how the control hand is driven. Switching to Movement resets to the
	/// resting state; Idle holds the rest pose; Stream lets the next streamed
	/// sample take over. Driven by the v2 Declare handshake.
	/// </summary>
	/// <param name="mode">The target driver mode -
	/// <see cref="ControlHandDriverMode.Movement"/>,
	/// <see cref="ControlHandDriverMode.Stream"/>, or
	/// <see cref="ControlHandDriverMode.Idle"/>.</param>
	public void SetDriverMode(ControlHandDriverMode mode)
	{
		if (mode == DriverMode)
			return;
		DriverMode = mode;
		GD.Print($"Control hand driver mode: {mode}");
		if (mode == ControlHandDriverMode.Movement)
		{
			animationState = "waiting";
			stateTimer = 0;
		}
		if (mode != ControlHandDriverMode.Stream)
			ResetBones();
	}

	private void UpdateMovementAnimation(float delta)
	{
		if (animationState == "waiting" || skeleton == null || movementPoses == null)
			return;

		// Frozen state: hold whatever pose we were at — animationArgument keeps
		// its value, and the state machine below has no "frozen" case, so the
		// pose is re-applied unchanged each frame. SetFrozen freezes the live
		// pose; SetMovement(cycle:false) sets the end pose before freezing.

		string currentMovement = availableMovements[currentMovementIndex];
		if (!movementPoses.ContainsKey(currentMovement))
		{
			GD.PrintErr($"Movement '{currentMovement}' not found in poses dictionary");
			return;
		}

		float[][][] poses = movementPoses[currentMovement];

		stateTimer += delta;

		// State machine for movement animation
		switch (animationState)
		{
			case "closing":
				// Interpolate from rest (state 1) to max flexion (state 0)
				animationArgument = Mathf.Pi * 0.5f * (stateTimer / (1.0f / Frequency));
				if (animationArgument >= Mathf.Pi * 0.5f)
				{
					animationArgument = Mathf.Pi * 0.5f;
					animationState = "holding";
					stateTimer = 0;
				}
				break;

			case "holding":
				// Hold at max flexion
				animationArgument = Mathf.Pi * 0.5f;
				if (stateTimer >= HoldTime)
				{
					animationState = "opening";
					stateTimer = 0;
				}
				break;

			case "opening":
				// Interpolate from max flexion (state 0) to rest (state 1)
				animationArgument = Mathf.Pi * 0.5f * (1.0f - (stateTimer / (1.0f / Frequency)));
				if (animationArgument <= 0)
				{
					animationArgument = 0;
					animationState = "resting";
					stateTimer = 0;
				}
				break;

			case "resting":
				// Hold at rest
				animationArgument = 0;
				if (stateTimer >= RestTime)
				{
					animationState = "closing";
					stateTimer = 0;
				}
				break;
		}

		// Apply bone rotations with sine wave interpolation
		ApplyMovementPose(poses, animationArgument);
	}

	private void ApplyMovementPose(float[][][] poses, float argument)
	{
		if (skeleton == null || boneMap.Count == 0)
			return;

		// 0 at rest, 1 at the target. This used to be negated, which was not an
		// interpolation at all: rest + (max - rest) * -1 is 2*rest - max, and it only looked
		// right because every rest was 0 and the pose table was signed the other way. Where a
		// rest was not 0 it missed — Movements.Thumb's middle joint (rest 10, max 55) ended at
		// -35 rather than 55. The table now holds rig degrees, so this interpolates plainly.
		float sinValue = Mathf.Sin(argument);

		for (int jointIdx = 0; jointIdx < 16; jointIdx++)
		{
			if (jointIdx >= BoneNames.Length || !boneMap.ContainsKey(BoneNames[jointIdx]))
				continue;

			// Get max flexion (state 0) and rest (state 1) poses
			float[] maxPose = poses[jointIdx][0];
			float[] restPose = poses[jointIdx][1];

			// Interpolate between rest and max using sine wave
			float x = restPose[0] + (maxPose[0] - restPose[0]) * sinValue;
			float y = restPose[1] + (maxPose[1] - restPose[1]) * sinValue;
			float z = restPose[2] + (maxPose[2] - restPose[2]) * sinValue;

			SetBoneRotation(jointIdx, x, y, z);
		}
	}

	public string GetCurrentMovementName()
	{
		if (availableMovements == null || currentMovementIndex >= availableMovements.Length)
			return "None";

		return availableMovements[currentMovementIndex];
	}

	public string GetAnimationState()
	{
		return animationState;
	}

	// ========== CONFIG LOADING SYSTEM ==========

	private void LoadMovementConfig()
	{
		string configPath = ProjectSettings.GlobalizePath(ConfigFilePath);

		// Generate default config if it doesn't exist
		if (!File.Exists(configPath))
		{
			GD.Print($"Config file not found at {configPath}, generating default...");
			MovementConfigGenerator.GenerateDefaultConfig(configPath);
		}
		else
		{
			// Migrate a Unity-signed config once, in place, rather than converting it on
			// every load forever. A permanent dual-convention reader is a second convention:
			// it keeps files around whose numbers mean the opposite of what they say, and
			// the next person to hand-edit one gets a hand that bends backwards. After this
			// the file says what it means and the shim never runs again.
			MovementConfigGenerator.MigrateToRigNative(configPath);
		}

		// Load config
		var loadedPoses = MovementConfigLoader.LoadConfig(configPath);

		if (loadedPoses == null || loadedPoses.Count == 0)
		{
			GD.PrintErr("Failed to load movement config, falling back to hardcoded poses");
			// Fallback to hardcoded poses
			var hardcodedPoses = MovementPoses.GetMovementPoses();
			movementPoses = new Dictionary<string, float[][][]>();
			foreach (var kvp in hardcodedPoses)
			{
				movementPoses[kvp.Key.ToString()] = kvp.Value;
			}

			var hardcodedMovements = MovementPoses.AIModeMovements;
			availableMovements = new string[hardcodedMovements.Length];
			for (int i = 0; i < hardcodedMovements.Length; i++)
			{
				availableMovements[i] = hardcodedMovements[i].ToString();
			}
		}
		else
		{
			movementPoses = loadedPoses;
			availableMovements = FilterMovementsByMode(loadedPoses);
			GD.Print($"Loaded {availableMovements.Length} movements from config");
		}
	}

	/// <summary>
	/// Reduce a loaded movement set to the entries available in the current
	/// <see cref="Mode"/>. A movement is hidden only if it belongs exclusively
	/// to the *other* mode's set — movements in this mode, and any custom
	/// movements not tied to a mode, stay available in config order. The full
	/// pose dictionary is left intact; this only narrows what is cycled
	/// through and reported by GetState. If filtering would leave nothing (a
	/// fully cross-mode custom config), the whole config is exposed instead.
	/// </summary>
	private string[] FilterMovementsByMode(Dictionary<string, float[][][]> poses)
	{
		var otherOnly = new HashSet<string>();
		foreach (var m in MovementPoses.ClassifierModeMovements) otherOnly.Add(m.ToString());
		foreach (var m in MovementPoses.AIModeMovements) otherOnly.Remove(m.ToString());

		var filtered = new List<string>();
		foreach (var name in MovementConfigLoader.GetMovementNames(poses))
		{
			if (!otherOnly.Contains(name))
				filtered.Add(name);
		}

		return filtered.Count > 0
			? filtered.ToArray()
			: MovementConfigLoader.GetMovementNames(poses);
	}

	private void SetupConfigWatcher()
	{
		string configPath = ProjectSettings.GlobalizePath(ConfigFilePath);
		string directory = Path.GetDirectoryName(configPath);
		string filename = Path.GetFileName(configPath);

		if (!Directory.Exists(directory))
		{
			GD.PrintErr($"Config directory does not exist: {directory}");
			return;
		}

		try
		{
			configWatcher = new FileSystemWatcher(directory, filename);
			configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
			configWatcher.Changed += OnConfigFileChanged;
			configWatcher.EnableRaisingEvents = true;
			GD.Print($"Watching config file: {configPath}");
		}
		catch (Exception e)
		{
			GD.PrintErr($"Failed to set up config watcher: {e.Message}");
		}
	}

	private DateTime lastConfigReload = DateTime.MinValue;
	private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
	{
		// Debounce: ignore rapid consecutive changes
		if ((DateTime.Now - lastConfigReload).TotalMilliseconds < 500)
			return;

		lastConfigReload = DateTime.Now;

		GD.Print($"Config file changed, reloading...");

		// Call deferred to avoid threading issues
		CallDeferred(nameof(ReloadConfig));
	}

	private void ReloadConfig()
	{
		string configPath = ProjectSettings.GlobalizePath(ConfigFilePath);
		var loadedPoses = MovementConfigLoader.LoadConfig(configPath);

		if (loadedPoses != null && loadedPoses.Count > 0)
		{
			int oldIndex = currentMovementIndex;
			string oldMovement = availableMovements[oldIndex];

			movementPoses = loadedPoses;
			availableMovements = FilterMovementsByMode(loadedPoses);

			// Try to preserve current movement selection
			currentMovementIndex = Array.IndexOf(availableMovements, oldMovement);
			if (currentMovementIndex < 0)
				currentMovementIndex = 0;

			GD.Print($"Config reloaded: {availableMovements.Length} movements");

			// Reset animation state to prevent issues
			animationState = "waiting";
			ResetBones();
		}
		else
		{
			GD.PrintErr("Config reload failed, keeping current configuration");
		}
	}

	/// <summary>
	/// Load a new config file, replacing the current one.
	/// Updates ConfigFilePath, reloads movements, and re-watches the new file.
	/// </summary>
	/// <param name="absolutePath">Absolute filesystem path to a TOML movement
	/// config. If the path is inside Godot's user data directory it is stored
	/// back as a <c>user://</c> path; otherwise the absolute path is kept.</param>
	public void LoadConfigFile(string absolutePath)
	{
		var loadedPoses = MovementConfigLoader.LoadConfig(absolutePath);
		if (loadedPoses == null || loadedPoses.Count == 0)
		{
			GD.PrintErr($"Failed to load config from: {absolutePath}");
			return;
		}

		// Convert to user:// path if inside user data dir, otherwise use globalized path
		string userDir = ProjectSettings.GlobalizePath("user://");
		if (absolutePath.StartsWith(userDir))
			ConfigFilePath = "user://" + absolutePath.Substring(userDir.Length);
		else
			ConfigFilePath = absolutePath;

		// Apply loaded movements
		movementPoses = loadedPoses;
		availableMovements = FilterMovementsByMode(loadedPoses);
		currentMovementIndex = 0;
		animationState = "waiting";
		ResetBones();

		GD.Print($"Loaded config: {absolutePath} ({availableMovements.Length} movements)");

		// Re-watch the new file
		if (configWatcher != null)
		{
			configWatcher.EnableRaisingEvents = false;
			configWatcher.Dispose();
			configWatcher = null;
		}
		SetupConfigWatcher();
	}

	public override void _ExitTree()
	{
		// Clean up file watcher
		if (configWatcher != null)
		{
			configWatcher.EnableRaisingEvents = false;
			configWatcher.Dispose();
			configWatcher = null;
		}
	}

	private void ApplySkinColor()
	{
		// Find all MeshInstance3D children recursively and apply skin color
		var meshInstances = FindMeshInstancesRecursive(this);

		if (meshInstances.Count == 0)
		{
			GD.Print("  No mesh instances found for skin color application");
			return;
		}

		// Create a skin-colored material (peachy/beige skin tone)
		var skinMaterial = new StandardMaterial3D();
		skinMaterial.AlbedoColor = new Color(0.95f, 0.76f, 0.65f); // Light peachy skin tone
		skinMaterial.Roughness = 0.7f;
		skinMaterial.Metallic = 0.0f;

		// Apply material to all mesh instances
		foreach (var meshInstance in meshInstances)
		{
			// Override surface material
			for (int i = 0; i < meshInstance.GetSurfaceOverrideMaterialCount(); i++)
			{
				meshInstance.SetSurfaceOverrideMaterial(i, skinMaterial);
			}
			// If no override materials, set the material directly
			if (meshInstance.GetSurfaceOverrideMaterialCount() == 0 && meshInstance.Mesh != null)
			{
				for (int i = 0; i < meshInstance.Mesh.GetSurfaceCount(); i++)
				{
					meshInstance.SetSurfaceOverrideMaterial(i, skinMaterial);
				}
			}
		}

		GD.Print($"  Applied skin color material to {meshInstances.Count} mesh instance(s)");
	}

	private List<MeshInstance3D> FindMeshInstancesRecursive(Node node)
	{
		var meshes = new List<MeshInstance3D>();

		if (node is MeshInstance3D meshInstance)
		{
			meshes.Add(meshInstance);
		}

		foreach (Node child in node.GetChildren())
		{
			meshes.AddRange(FindMeshInstancesRecursive(child));
		}

		return meshes;
	}
}
