using Godot;
using System;
using System.Collections.Generic;

namespace Vhi;

/// <summary>
/// The "predicted" hand - the model-output hand on the right of the scene.
///
/// Pulls 9-DOF samples from the <c>MyoGestic_Output</c> LSL inlet
/// (via <see cref="LSLCommunicationController"/>) each frame and writes per-joint
/// rotations across the same 16-joint subskeleton the control hand uses. When
/// <see cref="EnableSmoothing"/> is on, the pose is spherically interpolated
/// (<c>Slerp</c>) toward the new sample at <see cref="SmoothingSpeed"/> instead
/// of snapping - smoother on the eye, but with a small latency cost.
///
/// The "right hand" appearance is achieved by a mirrored root transform on the
/// node; both this node and the control hand instance the same left-hand FBX.
/// The current pose is published to the <c>VHI_Predict</c> LSL outlet at 60 Hz.
/// </summary>
public partial class PredictedHandSkeleton : Node3D
{
	/// <summary>Path to the <c>Skeleton3D</c> to animate. Left empty, the
	/// skeleton is auto-discovered inside the FBX child.</summary>
	[Export] public NodePath SkeletonPath;

	/// <summary>Spherically interpolate (<c>Slerp</c>) toward each incoming
	/// pose at <see cref="SmoothingSpeed"/> instead of snapping. Smoother on
	/// the eye, slight latency cost. Toggle live via the control panel or
	/// the gRPC <c>SetSmoothing</c> RPC.</summary>
	[Export] public bool EnableSmoothing = false;

	/// <summary>Interpolation speed when <see cref="EnableSmoothing"/> is on.
	/// Higher is snappier. Ignored when smoothing is off.</summary>
	[Export] public float SmoothingSpeed = 5.0f;

	private Skeleton3D skeleton;
	private LSLCommunicationController communicationController;
	private List<float> currentData = [];

	// Bone name to index mapping
	private readonly Dictionary<string, int> boneMap = [];

	// Maximum movement for each joint
	private readonly Dictionary<int, float[]> jointMovements = [];

	// Bone names in the FBX model (WaveBone naming convention)
	// Based on the Unity hand structure - matches ControlHandSkeleton mapping
	private string[] boneNames =
	[
		"WaveBone_1",   // 0 - wrist
		"WaveBone_3",   // 1 - thumb2 (proximal)
		"WaveBone_4",   // 2 - thumb1 (middle)
		"WaveBone_5",   // 3 - thumb0 (distal)
		"WaveBone_7",   // 4 - index2 (proximal)
		"WaveBone_8",   // 5 - index1 (middle)
		"WaveBone_9",   // 6 - index0 (distal)
		"WaveBone_12",  // 7 - middle2 (proximal)
		"WaveBone_13",  // 8 - middle1 (middle)
		"WaveBone_14",  // 9 - middle0 (distal)
		"WaveBone_17",  // 10 - ring2 (proximal)
		"WaveBone_18",  // 11 - ring1 (middle)
		"WaveBone_19",  // 12 - ring0 (distal)
		"WaveBone_22",  // 13 - pinkie2 (proximal)
		"WaveBone_23",  // 14 - pinkie1 (middle)
		"WaveBone_24"   // 15 - pinkie0 (distal)
	];

	private DateTime lastInputTime;
	private int inputFrameCount = 0;

	public override void _Ready()
	{
		GD.Print("=== Predicted Hand Skeleton Controller _Ready() START ===");

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

		// Set up joint movement limits
		InitializeJointMovements();


		lastInputTime = DateTime.Now;
		GD.Print("=== Predicted Hand Skeleton Controller _Ready() COMPLETE ===");
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

	/// <summary>Rendered degrees → the standard value that would produce them.</summary>
	/// <remarks>The inverse of <see cref="StandardPose.ToRig(int, float)"/> for in-range
	/// values, which is what makes the VHI_Predict read-back comparable with what was sent:
	/// a round-trip through this renderer is the identity, not a sign flip.</remarks>
	private static float ToStandard(int channel, float degrees, float gain) =>
		gain == 0f ? 0f : degrees / gain * StandardPose.Sign[channel];

	private void InitializeJointMovements()
	{
		// Wrist — joint 0 turns every digit with it. X is flexion, Z abduction; see
		// StandardPose.Wrist for where the two numbers come from and which one is a choice.
		jointMovements[0] = StandardPose.Wrist;

		// Thumb
		jointMovements[1] = [-45, 0, 30];
		jointMovements[2] = [-55, 0, -35];
		jointMovements[3] = [-80, 0, 0];

		// Index
		jointMovements[4] = [-85, 0, 0];
		jointMovements[5] = [-75, 0, 0];
		jointMovements[6] = [-60, 0, 0];

		// Middle
		jointMovements[7] = [-85, 0, 0];
		jointMovements[8] = [-85, 0, 0];
		jointMovements[9] = [-60, 0, 0];

		// Ring
		jointMovements[10] = [-85, 0, 0];
		jointMovements[11] = [-85, 0, 0];
		jointMovements[12] = [-60, 0, 0];

		// Pinky
		jointMovements[13] = [-85, 0, 0];
		jointMovements[14] = [-85, 0, 0];
		jointMovements[15] = [-60, 0, 0];
	}

	public override void _Process(double delta)
	{
		if (communicationController != null)
		{
			currentData = communicationController.GetReceivedDataPredicted();

			// The inlet carries STANDARD values: +1 means the direction the DOF name
			// denotes. Convert them to this rig's multipliers the same way every other
			// entry point does — see ToRig. Unconditionally: the conversion is not gated
			// behind the Declare handshake, because it must not be possible to render a
			// standard +1 in two different directions depending on what a client said.
			StandardPose.ToRig(currentData);

			if (currentData.Count >= 9 && skeleton != null && boneMap.Count > 0)
			{
				// Update input FPS tracking
				inputFrameCount++;
				var timeSinceLastInput = (DateTime.Now - lastInputTime).TotalSeconds;
				if (timeSinceLastInput >= 1.0)
				{
					int fps = (int)(inputFrameCount / timeSinceLastInput);
					GD.Print($"Predicted Hand Input FPS: {fps}");
					inputFrameCount = 0;
					lastInputTime = DateTime.Now;
				}

				if (EnableSmoothing)
				{
					MoveBonesSmoothly(delta);
				}
				else
				{
					MoveBonesDirectly();
				}
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		SendPredictedHandData();
	}

	private void MoveBonesDirectly()
	{
		// Thumb (indices 0 and 1: flexion and abduction)
		SetBoneRotation(0, currentData[6] * jointMovements[0][0], currentData[8] * jointMovements[0][1], currentData[7] * jointMovements[0][2]);

		SetBoneRotation(1, currentData[0] * jointMovements[1][0], 0, currentData[1] * jointMovements[1][2]);
		SetBoneRotation(2, currentData[0] * jointMovements[2][0], 0, currentData[1] * jointMovements[2][2]);
		SetBoneRotation(3, currentData[0] * jointMovements[3][0], 0, currentData[1] * jointMovements[3][2]);

		// Index (index 2)
		SetBoneRotation(4, currentData[2] * jointMovements[4][0], 0, 0);
		SetBoneRotation(5, currentData[2] * jointMovements[5][0], 0, 0);
		SetBoneRotation(6, currentData[2] * jointMovements[6][0], 0, 0);

		// Middle (index 3)
		SetBoneRotation(7, currentData[3] * jointMovements[7][0], 0, 0);
		SetBoneRotation(8, currentData[3] * jointMovements[8][0], 0, 0);
		SetBoneRotation(9, currentData[3] * jointMovements[9][0], 0, 0);

		// Ring (index 4)
		SetBoneRotation(10, currentData[4] * jointMovements[10][0], 0, 0);
		SetBoneRotation(11, currentData[4] * jointMovements[11][0], 0, 0);
		SetBoneRotation(12, currentData[4] * jointMovements[12][0], 0, 0);

		// Pinky (index 5)
		SetBoneRotation(13, currentData[5] * jointMovements[13][0], 0, 0);
		SetBoneRotation(14, currentData[5] * jointMovements[14][0], 0, 0);
		SetBoneRotation(15, currentData[5] * jointMovements[15][0], 0, 0);
	}

	private void MoveBonesSmoothly(double delta)
	{
		float lerpFactor = (float)(SmoothingSpeed * delta);

		// Thumb
		SmoothBoneRotation(0, currentData[6] * jointMovements[0][0], currentData[8] * jointMovements[0][1], currentData[7] * jointMovements[0][2], lerpFactor);

		SmoothBoneRotation(1, currentData[0] * jointMovements[1][0], 0, currentData[1] * jointMovements[1][2], lerpFactor);
		SmoothBoneRotation(2, currentData[0] * jointMovements[2][0], 0, currentData[1] * jointMovements[2][2], lerpFactor);
		SmoothBoneRotation(3, currentData[0] * jointMovements[3][0], 0, currentData[1] * jointMovements[3][2], lerpFactor);

		// Index
		SmoothBoneRotation(4, currentData[2] * jointMovements[4][0], 0, 0, lerpFactor);
		SmoothBoneRotation(5, currentData[2] * jointMovements[5][0], 0, 0, lerpFactor);
		SmoothBoneRotation(6, currentData[2] * jointMovements[6][0], 0, 0, lerpFactor);

		// Middle
		SmoothBoneRotation(7, currentData[3] * jointMovements[7][0], 0, 0, lerpFactor);
		SmoothBoneRotation(8, currentData[3] * jointMovements[8][0], 0, 0, lerpFactor);
		SmoothBoneRotation(9, currentData[3] * jointMovements[9][0], 0, 0, lerpFactor);

		// Ring
		SmoothBoneRotation(10, currentData[4] * jointMovements[10][0], 0, 0, lerpFactor);
		SmoothBoneRotation(11, currentData[4] * jointMovements[11][0], 0, 0, lerpFactor);
		SmoothBoneRotation(12, currentData[4] * jointMovements[12][0], 0, 0, lerpFactor);

		// Pinky
		SmoothBoneRotation(13, currentData[5] * jointMovements[13][0], 0, 0, lerpFactor);
		SmoothBoneRotation(14, currentData[5] * jointMovements[14][0], 0, 0, lerpFactor);
		SmoothBoneRotation(15, currentData[5] * jointMovements[15][0], 0, 0, lerpFactor);
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

	private void SmoothBoneRotation(int jointIndex, float targetXDeg, float targetYDeg, float targetZDeg, float lerpFactor)
	{
		if (jointIndex < 0 || jointIndex >= boneNames.Length)
			return;

		if (!boneMap.ContainsKey(boneNames[jointIndex]))
			return;

		int boneIdx = boneMap[boneNames[jointIndex]];

		// Get current rotation and normalize
		Quaternion current = skeleton.GetBonePoseRotation(boneIdx).Normalized();

		// Create target rotation (convert to radians) and normalize
		Vector3 eulerRadians = new(Mathf.DegToRad(targetXDeg), Mathf.DegToRad(targetYDeg), Mathf.DegToRad(targetZDeg));
		Quaternion target = new Quaternion(Basis.FromEuler(eulerRadians)).Normalized();

		// Interpolate (both quaternions are now normalized)
		Quaternion newRot = current.Slerp(target, lerpFactor);

		// Set the bone pose
		skeleton.SetBonePoseRotation(boneIdx, newRot);
	}

	private void SendPredictedHandData()
	{
		if (communicationController == null || skeleton == null || boneMap.Count == 0)
			return;

		List<float> outputData = [];

		// Standard, not rig units: dividing by the gain recovers the multiplier, and the
		// channel's sign turns that back into the value a client would have had to send to
		// produce this pose. So VHI_Predict speaks the same language as the inlet, and a
		// round-trip through the renderer is the identity rather than a sign flip.
		var thumb2Rot = GetBoneRotationDegrees(1);
		outputData.Add(ToStandard(0, thumb2Rot.X, jointMovements[1][0])); // Thumb Flexion
		outputData.Add(ToStandard(1, thumb2Rot.Z, jointMovements[1][2])); // Thumb Abduction
		outputData.Add(ToStandard(2, GetBoneRotationDegrees(4).X, jointMovements[4][0]));
		outputData.Add(ToStandard(3, GetBoneRotationDegrees(7).X, jointMovements[7][0]));
		outputData.Add(ToStandard(4, GetBoneRotationDegrees(10).X, jointMovements[10][0]));
		outputData.Add(ToStandard(5, GetBoneRotationDegrees(13).X, jointMovements[13][0]));

		// Wrist: all three axes of bone 0, which parents every digit.
		var wristRot = GetBoneRotationDegrees(0);
		outputData.Add(ToStandard(6, wristRot.X, jointMovements[0][0]));
		outputData.Add(ToStandard(7, wristRot.Z, jointMovements[0][2]));
		outputData.Add(ToStandard(8, wristRot.Y, jointMovements[0][1]));

		communicationController.SendPredictedData(outputData);
	}

	private Vector3 GetBoneRotationDegrees(int jointIndex)
	{
		if (jointIndex < 0 || jointIndex >= boneNames.Length || !boneMap.ContainsKey(boneNames[jointIndex]))
			return Vector3.Zero;

		int boneIdx = boneMap[boneNames[jointIndex]];
		Quaternion rot = skeleton.GetBonePoseRotation(boneIdx);
		return rot.GetEuler() * (180.0f / Mathf.Pi);
	}

	/// <summary>Reset all 16 animated joints to their rest pose. Used to clear
	/// the hand to neutral - typically when the prediction stream stops or a
	/// fresh stream connects.</summary>
	public void ResetBones()
	{
		if (skeleton == null)
			return;

		foreach (var bone in boneMap.Values)
		{
			skeleton.SetBonePoseRotation(bone, Quaternion.Identity);
		}

		GD.Print("Predicted hand bones reset");
	}

	// --- standard control (v2) --------------------------------------------------
	//
	// The v2 service addresses DOFs by name and needs three things this class did not
	// expose: set ONE channel without disturbing the rest of the pose, read a joint's
	// signed rotation back, and know which joints a channel drives. All three are
	// deliberately thin — the name/channel mapping stays in VhiControlService,
	// because a skeleton should not know the wire vocabulary.

	/// <summary>Which joints each legacy channel drives, mirroring
	/// <c>MoveBonesDirectly</c>. Channels 6-8 drive the wrist, on joint 0.</summary>
	private static readonly Dictionary<int, int[]> jointsByChannel = new()
	{
		[0] = [1, 2, 3],
		[1] = [1, 2, 3],
		[2] = [4, 5, 6],
		[3] = [7, 8, 9],
		[4] = [10, 11, 12],
		[5] = [13, 14, 15],
		// The wrist drives one joint on three axes — flexion, abduction, rotation.
		[6] = [0],
		[7] = [0],
		[8] = [0],
	};

	/// <summary>The joints a legacy channel drives, or an empty array for a dead one.</summary>
	public int[] JointsForChannel(int channel) =>
		jointsByChannel.TryGetValue(channel, out int[] joints) ? joints : [];

	/// <summary>
	/// The joints a channel can actually move on one axis: those whose gain on that
	/// axis is non-zero.
	/// </summary>
	/// <remarks>
	/// Not the same as <see cref="JointsForChannel"/>, and the difference is real.
	/// Thumb abduction drives all three thumb bones through channel 1, but the distal
	/// one has a Z gain of 0 — so only two of them can move, and an expectation built
	/// from the channel alone would report a correct sweep as a mismatch.
	/// </remarks>
	/// <param name="channel">The legacy pose channel, 0-5.</param>
	/// <param name="axisIndex">0 for X, 2 for Z — an index into the gain triple.</param>
	public int[] JointsMovableOnAxis(int channel, int axisIndex)
	{
		var movable = new List<int>();
		foreach (int joint in JointsForChannel(channel))
		{
			if (jointMovements.TryGetValue(joint, out float[] gains)
				&& axisIndex < gains.Length
				&& gains[axisIndex] != 0f)
			{
				movable.Add(joint);
			}
		}
		return [.. movable];
	}

	/// <summary>The model's own name for a joint — the rig's identity claim, not a label.</summary>
	public string BoneNameForJoint(int jointIndex) =>
		jointIndex >= 0 && jointIndex < boneNames.Length ? boneNames[jointIndex] : $"joint {jointIndex}";

	/// <summary>
	/// Set one channel of the pose and render it, leaving every other channel alone.
	/// </summary>
	/// <remarks>
	/// <paramref name="standard"/> is a standard value: <c>+1</c> means the direction
	/// the DOF's name denotes. The negation into this hand's wire convention (its
	/// flexion gains are negative) happens here, so the standard vocabulary never has
	/// to carry a sign that belongs to one renderer.
	/// <para>
	/// Bones stay where they are put: <c>_Process</c> only re-poses them when a fresh
	/// LSL sample arrives, so a value set here persists until the next one does. A
	/// client streaming poses over LSL while calling this will fight it.
	/// </para>
	/// </remarks>
	public void SetStandardValue(int channel, float standard)
	{
		if (skeleton == null || boneMap.Count == 0 || !jointsByChannel.ContainsKey(channel))
			return;
		while (currentData.Count < 9)
			currentData.Add(0f);
		currentData[channel] = StandardPose.ToRig(channel, standard);
		MoveBonesDirectly();
	}

	/// <summary>Every animated channel back to standard rest, rendered immediately.</summary>
	public void RestStandardPose()
	{
		if (skeleton == null || boneMap.Count == 0)
			return;
		currentData = [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
		MoveBonesDirectly();
	}

	/// <summary>
	/// Signed rotation in degrees of every joint any channel drives, read back off the
	/// skeleton so a caller can see what actually moved rather than what was asked for.
	/// </summary>
	public Dictionary<int, Vector3> AnimatedJointDegrees()
	{
		var pose = new Dictionary<int, Vector3>();
		foreach (int[] joints in jointsByChannel.Values)
		{
			foreach (int joint in joints)
			{
				pose.TryAdd(joint, GetBoneRotationDegrees(joint));
			}
		}
		return pose;
	}

	/// <summary>
	/// Programmatic smoothing control. Used by the UI panel and the gRPC
	/// control service. A non-positive smoothingSpeed leaves the speed unchanged.
	/// </summary>
	/// <param name="enabled"><see langword="true"/> to spherically interpolate
	/// (<c>Slerp</c>) toward each incoming sample; <see langword="false"/> to
	/// snap to it.</param>
	/// <param name="smoothingSpeed">Interpolation speed when smoothing is on.
	/// Higher is snappier. Values &lt;= 0 leave the current speed unchanged.</param>
	public void SetSmoothing(bool enabled, float smoothingSpeed)
	{
		EnableSmoothing = enabled;
		if (smoothingSpeed > 0)
			SmoothingSpeed = smoothingSpeed;
	}
}
