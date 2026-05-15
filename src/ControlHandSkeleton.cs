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
///   <item><description><b>Idle</b>: resets to the neutral pose and holds it;
///     ignores keyboard, stream, and commands.</description></item>
/// </list>
///
/// Frame-by-frame animation logic runs in <c>_Process</c>; the resulting pose is
/// published to the <c>VHI_Control</c> LSL outlet in <c>_PhysicsProcess</c>.
/// Commands originate from the gRPC <see cref="VhiControlService"/> (via the public
/// command API on this class) and from local keyboard input - the same methods are
/// called either way.
/// </summary>
public partial class ControlHandSkeleton : Node3D
{
	/// <summary>Path to the <c>Skeleton3D</c> to animate. Left empty, the
	/// skeleton is auto-discovered inside the FBX child.</summary>
	[Export] public NodePath SkeletonPath;

	/// <summary>How the control hand is driven each frame -
	/// <see cref="ControlHandDriverMode.Movement"/>,
	/// <see cref="ControlHandDriverMode.Stream"/>, or
	/// <see cref="ControlHandDriverMode.Idle"/>. Change at runtime with
	/// <see cref="SetDriverMode"/> or the gRPC <c>SetControlMode</c> RPC.</summary>
	[Export] public ControlHandDriverMode DriverMode = ControlHandDriverMode.Movement;

	/// <summary>Enable the predefined-movement state machine. When
	/// <see langword="false"/>, the hand holds its current pose and ignores
	/// keyboard input.</summary>
	[Export] public bool EnableMovementControl = true;

	/// <summary>Which subset of movements is exposed in
	/// <see cref="GetAvailableMovements"/>. <see cref="MovementMode.AI"/> = 17;
	/// <see cref="MovementMode.Classifier"/> = 15. Filtering rule: hide
	/// movements exclusive to the other mode.</summary>
	[Export] public MovementMode Mode = MovementMode.AI;

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

	private Skeleton3D skeleton;
	private LSLCommunicationController communicationController;
	private List<float> currentData = [];

	// Bone name to index mapping
	private readonly Dictionary<string, int> boneMap = [];

	// Maximum movement for each joint (from Unity)
	private readonly float[][][] jointMaximumMovement = new float[16][][];

	// Movement control system
	private Dictionary<string, float[][][]> movementPoses;
	private string[] availableMovements;
	private int currentMovementIndex = 0;
	private string animationState = "waiting";  // waiting, closing, holding, opening, resting, frozen
	private float animationArgument = 0.0f;
	private float stateTimer = 0.0f;
	private DateTime animationStartTime;
	private FileSystemWatcher configWatcher;  // Watches for config file changes

	// Bone names in the FBX model (WaveBone naming convention)
	// Based on the Unity hand structure, mapping to WaveBone_0 through WaveBone_15
	private string[] boneNames =
	[
		"WaveBone_1",   // 0 - wrist
		"WaveBone_3",   // 1 - thumb2 (proximal)
		"WaveBone_4",   // 2 - thumb1 (middle)
		"WaveBone_5",   // 3 - thumb0 (distal)
		"WaveBone_7",   // 4 - index2 (proximal)
		"WaveBone_8",   // 5 - index1 (middle)
		"WaveBone_9",   // 6 - index0 (distal)
		"WaveBone_12",   // 7 - middle2 (proximal)
		"WaveBone_13",   // 8 - middle1 (middle)
		"WaveBone_14",   // 9 - middle0 (distal)
		"WaveBone_17",  // 10 - ring2 (proximal)
		"WaveBone_18",  // 11 - ring1 (middle)
		"WaveBone_19",  // 12 - ring0 (distal)
		"WaveBone_22",  // 13 - pinkie2 (proximal)
		"WaveBone_23",  // 14 - pinkie1 (middle)
		"WaveBone_24"   // 15 - pinkie0 (distal)
	];

	public override void _Ready()
	{
		GD.Print("=== Control Hand Skeleton Controller _Ready() START ===");

		// Get communication controller
		GD.Print("  Getting communication controller...");
		communicationController = GetNode<LSLCommunicationController>("/root/Main/LSLCommunicationController");
		GD.Print("  Communication controller found");

		// Find skeleton
		if (SkeletonPath != null)
		{
			skeleton = GetNode<Skeleton3D>(SkeletonPath);
		}
		else
		{
			// Try to find skeleton automatically
			skeleton = FindSkeletonRecursive(this);
		}

		if (skeleton != null)
		{
			GD.Print($"✅ Found Skeleton3D with {skeleton.GetBoneCount()} bones");
			MapBones();
		}
		else
		{
			GD.PrintErr("⚠️ No Skeleton3D found! Hand won't animate.");
		}

		// Set up maximum movements
		GD.Print("  Initializing maximum movements...");
		InitializeMaximumMovements();

		// Initialize movement control system
		if (EnableMovementControl)
		{
			GD.Print("  Initializing movement control system...");
			LoadMovementConfig();
			animationStartTime = DateTime.Now;

			if (movementPoses != null && availableMovements != null && availableMovements.Length > 0)
			{
				GD.Print($"  Movement mode: {Mode} ({availableMovements.Length} movements available)");
				GD.Print($"  Current movement: {availableMovements[currentMovementIndex]}");

				// Movement state will be sent when movement is first initiated (not while in waiting state)

				// Set up file watcher for hot-reload
				SetupConfigWatcher();
			}
			else
			{
				GD.PrintErr("  Failed to load movements - using empty movement list");
				availableMovements = [];
			}
		}

		// Apply skin color material to the hand mesh
		ApplySkinColor();

		GD.Print("=== Control Hand Skeleton Controller _Ready() COMPLETE ===");
	}

	private Skeleton3D FindSkeletonRecursive(Node node)
	{
		if (node is Skeleton3D skel)
			return skel;

		foreach (Node child in node.GetChildren())
		{
			var result = FindSkeletonRecursive(child);
			if (result != null)
				return result;
		}
		return null;
	}

	private void MapBones()
	{
		boneMap.Clear();

		// First, print all available bone names
		GD.Print($"\n  === Available bones in skeleton ({skeleton.GetBoneCount()} total) ===");
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			GD.Print($"  [{i}] {skeleton.GetBoneName(i)}");
		}
		GD.Print("  ===============================================\n");

		GD.Print("\n  === Attempting to map hand joints to WaveBones ===");
		for (int i = 0; i < boneNames.Length; i++)
		{
			int boneIdx = skeleton.FindBone(boneNames[i]);
			if (boneIdx != -1)
			{
				boneMap[boneNames[i]] = boneIdx;
				GD.Print($"  Mapped {boneNames[i]} → bone index {boneIdx}");
			}
			else
			{
				GD.PrintErr($"  ⚠️ Bone '{boneNames[i]}' not found in skeleton!");
			}
		}
	}

	private void InitializeMaximumMovements()
	{
		for (int i = 0; i < jointMaximumMovement.Length; i++)
		{
			jointMaximumMovement[i] = new float[2][];
			for (int j = 0; j < 2; j++)
			{
				jointMaximumMovement[i][j] = new float[3];
			}
		}

		// Thumb (joints 1-3)
		jointMaximumMovement[1][0] = [-45, 0, 30];
		jointMaximumMovement[2][0] = [-55, 0, -35];
		jointMaximumMovement[3][0] = [-80, 0, 0];

		// Index (joints 4-6)
		jointMaximumMovement[4][0] = [-85, 0, 0];
		jointMaximumMovement[5][0] = [-75, 0, 0];
		jointMaximumMovement[6][0] = [-60, 0, 0];

		// Middle (joints 7-9)
		jointMaximumMovement[7][0] = [-85, 0, 0];
		jointMaximumMovement[8][0] = [-85, 0, 0];
		jointMaximumMovement[9][0] = [-60, 0, 0];

		// Ring (joints 10-12)
		jointMaximumMovement[10][0] = [-85, 0, 0];
		jointMaximumMovement[11][0] = [-85, 0, 0];
		jointMaximumMovement[12][0] = [-60, 0, 0];

		// Pinky (joints 13-15)
		jointMaximumMovement[13][0] = [-85, 0, 0];
		jointMaximumMovement[14][0] = [-85, 0, 0];
		jointMaximumMovement[15][0] = [-60, 0, 0];
	}

	public override void _Process(double delta)
	{
		switch (DriverMode)
		{
			case ControlHandDriverMode.Movement:
				// Predefined-movement state machine + local keyboard.
				if (EnableMovementControl)
				{
					HandleMovementInput();
					UpdateMovementAnimation((float)delta);
				}
				break;

			case ControlHandDriverMode.Stream:
				// Continuous pose streamed in over the MyoGestic_ControlPose inlet.
				if (communicationController != null)
				{
					currentData = communicationController.GetReceivedDataControl();
					if (currentData.Count >= 9 && skeleton != null)
						MoveBonesFromStream();
				}
				break;

			case ControlHandDriverMode.Idle:
				// Hold whatever pose; ignore keyboard, stream, and commands.
				break;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		SendControlHandData();
	}

	private void MoveBonesFromStream()
	{
		if (skeleton == null || boneMap.Count == 0)
			return;

		int shift = 0;

		// Thumb (uses indices 0 and 1: flexion and abduction)
		SetBoneRotation(1, currentData[0 + shift] * jointMaximumMovement[1][0][0], 0, currentData[1 + shift] * jointMaximumMovement[1][0][2]);
		SetBoneRotation(2, currentData[0 + shift] * jointMaximumMovement[2][0][0], 0, currentData[1 + shift] * jointMaximumMovement[2][0][2]);
		SetBoneRotation(3, currentData[0 + shift] * jointMaximumMovement[3][0][0], 0, currentData[1 + shift] * jointMaximumMovement[3][0][2]);

		// Index (uses index 2)
		SetBoneRotation(4, currentData[2 + shift] * jointMaximumMovement[4][0][0], 0, 0);
		SetBoneRotation(5, currentData[2 + shift] * jointMaximumMovement[5][0][0], 0, 0);
		SetBoneRotation(6, currentData[2 + shift] * jointMaximumMovement[6][0][0], 0, 0);

		// Middle (uses index 3)
		SetBoneRotation(7, currentData[3 + shift] * jointMaximumMovement[7][0][0], 0, 0);
		SetBoneRotation(8, currentData[3 + shift] * jointMaximumMovement[8][0][0], 0, 0);
		SetBoneRotation(9, currentData[3 + shift] * jointMaximumMovement[9][0][0], 0, 0);

		// Ring (uses index 4)
		SetBoneRotation(10, currentData[4 + shift] * jointMaximumMovement[10][0][0], 0, 0);
		SetBoneRotation(11, currentData[4 + shift] * jointMaximumMovement[11][0][0], 0, 0);
		SetBoneRotation(12, currentData[4 + shift] * jointMaximumMovement[12][0][0], 0, 0);

		// Pinky (uses index 5)
		SetBoneRotation(13, currentData[5 + shift] * jointMaximumMovement[13][0][0], 0, 0);
		SetBoneRotation(14, currentData[5 + shift] * jointMaximumMovement[14][0][0], 0, 0);
		SetBoneRotation(15, currentData[5 + shift] * jointMaximumMovement[15][0][0], 0, 0);
	}

	private void SetBoneRotation(int jointIndex, float xDeg, float yDeg, float zDeg)
	{
		if (jointIndex < 0 || jointIndex >= boneNames.Length)
			return;

		if (!boneMap.ContainsKey(boneNames[jointIndex]))
			return;

		int boneIdx = boneMap[boneNames[jointIndex]];

		// Create rotation from degrees (convert to radians)
		Vector3 eulerRadians = new(Mathf.DegToRad(xDeg), Mathf.DegToRad(yDeg), Mathf.DegToRad(zDeg));
		Quaternion rotation = new(Basis.FromEuler(eulerRadians));

		// Set the bone pose
		skeleton.SetBonePoseRotation(boneIdx, rotation);
	}

	private void SendControlHandData()
	{
		if (communicationController == null || skeleton == null || boneMap.Count == 0)
			return;

		List<float> outputData = [];

		// Extract current bone rotations and normalize them
		// Thumb Flexion
		var thumb2Rot = GetBoneRotationDegrees(1);
		outputData.Add(thumb2Rot.X / jointMaximumMovement[1][0][0]);

		// Thumb Abduction
		outputData.Add(thumb2Rot.Z / jointMaximumMovement[1][0][2]);

		// Index Flexion
		var index2Rot = GetBoneRotationDegrees(4);
		outputData.Add(index2Rot.X / jointMaximumMovement[4][0][0]);

		// Middle Flexion
		var middle2Rot = GetBoneRotationDegrees(7);
		outputData.Add(middle2Rot.X / jointMaximumMovement[7][0][0]);

		// Ring Flexion
		var ring2Rot = GetBoneRotationDegrees(10);
		outputData.Add(ring2Rot.X / jointMaximumMovement[10][0][0]);

		// Pinky Flexion
		var pinky2Rot = GetBoneRotationDegrees(13);
		outputData.Add(pinky2Rot.X / jointMaximumMovement[13][0][0]);

		// Wrist (not used, but needed for compatibility)
		outputData.Add(0); // Wrist Flexion
		outputData.Add(0); // Wrist Abduction
		outputData.Add(0); // Wrist Rotation

		communicationController.SendControlData(outputData);
	}

	private Vector3 GetBoneRotationDegrees(int jointIndex)
	{
		if (jointIndex < 0 || jointIndex >= boneNames.Length || !boneMap.ContainsKey(boneNames[jointIndex]))
			return Vector3.Zero;

		int boneIdx = boneMap[boneNames[jointIndex]];
		Quaternion rot = skeleton.GetBonePoseRotation(boneIdx);
		return rot.GetEuler() * (180.0f / Mathf.Pi);
	}

	/// <summary>Reset all 16 animated joints to their rest pose (neutral
	/// rotation). Called whenever the hand needs to clear back to neutral -
	/// stopping a movement, switching driver mode, or releasing freeze.</summary>
	public void ResetBones()
	{
		if (skeleton == null)
			return;

		foreach (var bone in boneMap.Values)
		{
			skeleton.SetBonePoseRotation(bone, Quaternion.Identity);
		}

		GD.Print("Control hand bones reset");
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
	// (VhiControlService). All of these run on Godot's main thread.

	/// <summary>True when a MyoGestic recording session is currently active.
	/// While set, VHI's local keyboard input is gated off so the gRPC client
	/// is the sole movement source. Toggled via the
	/// <c>VhiControlService.SetSessionActive</c> RPC.</summary>
	public bool SessionActive { get; set; } = false;

	/// <summary>Movement names valid for the current mode.</summary>
	public string[] GetAvailableMovements() => availableMovements ?? [];

	/// <summary>"AI" or "Classifier".</summary>
	public string GetModeName() => Mode.ToString();

	/// <summary>
	/// Cycle the selected movement by <paramref name="delta"/> steps (e.g. -1 /
	/// +1, wrapping) and reset to the waiting state.
	/// </summary>
	/// <param name="delta">Direction and magnitude of the cycle step. Pass
	/// <c>+1</c> for the next movement, <c>-1</c> for the previous; wraps at
	/// the ends of the available-movements list.</param>
	public void CycleMovement(int delta)
	{
		if (!EnableMovementControl || availableMovements == null || availableMovements.Length == 0)
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
		if (!EnableMovementControl || availableMovements == null || availableMovements.Length == 0)
			return;

		animationState = "closing";
		stateTimer = 0;
		animationArgument = 0.0f;
		animationStartTime = DateTime.Now;
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
		// Movement commands only apply when the control hand is in Movement mode.
		if (DriverMode != ControlHandDriverMode.Movement
			|| !EnableMovementControl || availableMovements == null)
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
		if (DriverMode != ControlHandDriverMode.Movement)
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
		if (DriverMode != ControlHandDriverMode.Movement)
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
	/// sample take over. Used by the gRPC SetControlMode RPC.
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

		float sinValue = -Mathf.Sin(argument);  // 0 to 1 interpolation

		for (int jointIdx = 0; jointIdx < 16; jointIdx++)
		{
			if (jointIdx >= boneNames.Length || !boneMap.ContainsKey(boneNames[jointIdx]))
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
		if (!EnableMovementControl || availableMovements == null || currentMovementIndex >= availableMovements.Length)
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

			// Get movement list based on mode
			var hardcodedMovements = Mode == MovementMode.AI ?
				MovementPoses.AIModeMovements :
				MovementPoses.ClassifierModeMovements;
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
		var thisMode = Mode == MovementMode.AI
			? MovementPoses.AIModeMovements
			: MovementPoses.ClassifierModeMovements;
		var otherMode = Mode == MovementMode.AI
			? MovementPoses.ClassifierModeMovements
			: MovementPoses.AIModeMovements;

		var otherOnly = new HashSet<string>();
		foreach (var m in otherMode) otherOnly.Add(m.ToString());
		foreach (var m in thisMode) otherOnly.Remove(m.ToString());

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
