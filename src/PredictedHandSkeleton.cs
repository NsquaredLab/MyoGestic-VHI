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
public partial class PredictedHandSkeleton : HandSkeleton
{
	/// <summary>Spherically interpolate (<c>Slerp</c>) toward each incoming
	/// pose at <see cref="SmoothingSpeed"/> instead of snapping. Smoother on
	/// the eye, slight latency cost. Toggle live via the control panel or
	/// the gRPC <c>SetSmoothing</c> RPC.</summary>
	[Export] public bool EnableSmoothing = false;

	/// <summary>Interpolation speed when <see cref="EnableSmoothing"/> is on.
	/// Higher is snappier. Ignored when smoothing is off.</summary>
	[Export] public float SmoothingSpeed = 5.0f;

	private LSLCommunicationController communicationController;
	private List<float> currentData = [];

	public override void _Ready()
	{
		GD.Print("=== Predicted Hand Skeleton Controller _Ready() START ===");

		// Get communication controller
		GD.Print("  Getting communication controller...");
		communicationController = GetNode<LSLCommunicationController>("/root/Main/LSLCommunicationController");
		GD.Print("  Communication controller found");

		FindAndMapSkeleton();
		GD.Print("=== Predicted Hand Skeleton Controller _Ready() COMPLETE ===");
	}

	public override void _Process(double delta)
	{
		if (communicationController != null)
		{
			currentData = communicationController.GetReceivedDataPredicted();

			// The inlet carries STANDARD values: +1 means the direction the DOF name
			// denotes. They stay standard from here — MoveBones* multiplies by
			// StandardPose.AtPlusOne, so the domain clamp is the only thing owed. Applied
			// unconditionally, not gated behind the Declare handshake, because it must not be
			// possible to render a standard +1 in two different directions depending on what
			// a client said.
			StandardPose.Clamp(currentData);

			if (currentData.Count >= 9 && skeleton != null && boneMap.Count > 0)
			{
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
		if (communicationController != null && skeleton != null && boneMap.Count > 0)
			communicationController.SendPredictedData(ReadStandardPose());
	}

	private void MoveBonesDirectly()
	{
		// Thumb (indices 0 and 1: flexion and abduction)
		SetBoneRotation(0, currentData[6] * StandardPose.AtPlusOne[0][0], currentData[8] * StandardPose.AtPlusOne[0][1], currentData[7] * StandardPose.AtPlusOne[0][2]);

		SetBoneRotation(1, currentData[0] * StandardPose.AtPlusOne[1][0], 0, currentData[1] * StandardPose.AtPlusOne[1][2]);
		SetBoneRotation(2, currentData[0] * StandardPose.AtPlusOne[2][0], 0, currentData[1] * StandardPose.AtPlusOne[2][2]);
		SetBoneRotation(3, currentData[0] * StandardPose.AtPlusOne[3][0], 0, currentData[1] * StandardPose.AtPlusOne[3][2]);

		// Index (index 2)
		SetBoneRotation(4, currentData[2] * StandardPose.AtPlusOne[4][0], 0, 0);
		SetBoneRotation(5, currentData[2] * StandardPose.AtPlusOne[5][0], 0, 0);
		SetBoneRotation(6, currentData[2] * StandardPose.AtPlusOne[6][0], 0, 0);

		// Middle (index 3)
		SetBoneRotation(7, currentData[3] * StandardPose.AtPlusOne[7][0], 0, 0);
		SetBoneRotation(8, currentData[3] * StandardPose.AtPlusOne[8][0], 0, 0);
		SetBoneRotation(9, currentData[3] * StandardPose.AtPlusOne[9][0], 0, 0);

		// Ring (index 4)
		SetBoneRotation(10, currentData[4] * StandardPose.AtPlusOne[10][0], 0, 0);
		SetBoneRotation(11, currentData[4] * StandardPose.AtPlusOne[11][0], 0, 0);
		SetBoneRotation(12, currentData[4] * StandardPose.AtPlusOne[12][0], 0, 0);

		// Pinky (index 5)
		SetBoneRotation(13, currentData[5] * StandardPose.AtPlusOne[13][0], 0, 0);
		SetBoneRotation(14, currentData[5] * StandardPose.AtPlusOne[14][0], 0, 0);
		SetBoneRotation(15, currentData[5] * StandardPose.AtPlusOne[15][0], 0, 0);
	}

	private void MoveBonesSmoothly(double delta)
	{
		float lerpFactor = (float)(SmoothingSpeed * delta);

		// Thumb
		SmoothBoneRotation(0, currentData[6] * StandardPose.AtPlusOne[0][0], currentData[8] * StandardPose.AtPlusOne[0][1], currentData[7] * StandardPose.AtPlusOne[0][2], lerpFactor);

		SmoothBoneRotation(1, currentData[0] * StandardPose.AtPlusOne[1][0], 0, currentData[1] * StandardPose.AtPlusOne[1][2], lerpFactor);
		SmoothBoneRotation(2, currentData[0] * StandardPose.AtPlusOne[2][0], 0, currentData[1] * StandardPose.AtPlusOne[2][2], lerpFactor);
		SmoothBoneRotation(3, currentData[0] * StandardPose.AtPlusOne[3][0], 0, currentData[1] * StandardPose.AtPlusOne[3][2], lerpFactor);

		// Index
		SmoothBoneRotation(4, currentData[2] * StandardPose.AtPlusOne[4][0], 0, 0, lerpFactor);
		SmoothBoneRotation(5, currentData[2] * StandardPose.AtPlusOne[5][0], 0, 0, lerpFactor);
		SmoothBoneRotation(6, currentData[2] * StandardPose.AtPlusOne[6][0], 0, 0, lerpFactor);

		// Middle
		SmoothBoneRotation(7, currentData[3] * StandardPose.AtPlusOne[7][0], 0, 0, lerpFactor);
		SmoothBoneRotation(8, currentData[3] * StandardPose.AtPlusOne[8][0], 0, 0, lerpFactor);
		SmoothBoneRotation(9, currentData[3] * StandardPose.AtPlusOne[9][0], 0, 0, lerpFactor);

		// Ring
		SmoothBoneRotation(10, currentData[4] * StandardPose.AtPlusOne[10][0], 0, 0, lerpFactor);
		SmoothBoneRotation(11, currentData[4] * StandardPose.AtPlusOne[11][0], 0, 0, lerpFactor);
		SmoothBoneRotation(12, currentData[4] * StandardPose.AtPlusOne[12][0], 0, 0, lerpFactor);

		// Pinky
		SmoothBoneRotation(13, currentData[5] * StandardPose.AtPlusOne[13][0], 0, 0, lerpFactor);
		SmoothBoneRotation(14, currentData[5] * StandardPose.AtPlusOne[14][0], 0, 0, lerpFactor);
		SmoothBoneRotation(15, currentData[5] * StandardPose.AtPlusOne[15][0], 0, 0, lerpFactor);
	}

	private void SmoothBoneRotation(int jointIndex, float targetXDeg, float targetYDeg, float targetZDeg, float lerpFactor)
	{
		if (jointIndex < 0 || jointIndex >= BoneNames.Length
			|| !boneMap.TryGetValue(BoneNames[jointIndex], out int boneIdx))
			return;

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
			if (StandardPose.AtPlusOne.TryGetValue(joint, out float[] gains)
				&& axisIndex < gains.Length
				&& gains[axisIndex] != 0f)
			{
				movable.Add(joint);
			}
		}
		return [.. movable];
	}

	/// <summary>
	/// Set one channel of the pose and render it, leaving every other channel alone.
	/// </summary>
	/// <remarks>
	/// <paramref name="standard"/> is a standard value: <c>+1</c> means the direction
	/// the DOF's name denotes. It is stored as it arrives — only clamped — because the
	/// pose vector is in standard units all the way to <c>StandardPose.AtPlusOne</c>. No
	/// sign is applied here, or anywhere outside <c>StandardPose</c>.
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
		currentData[channel] = StandardPose.Clamp(standard);
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
