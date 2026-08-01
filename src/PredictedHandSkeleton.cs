using Godot;
using System;
using System.Collections.Generic;

namespace Vhi;

/// <summary>
/// The "predicted" hand - the model-output hand on the right of the scene.
///
/// Renders whatever its DOFs are commanded to, whenever they are commanded: each arrives
/// on a stream of its own (<c>vhi.prediction.index</c> and its eight siblings, via
/// <see cref="LSLCommunicationController"/>) and is applied on arrival, so a producer
/// driving one finger moves one finger and the rest hold. When
/// <see cref="EnableSmoothing"/> is on, the rig is spherically interpolated
/// (<c>Slerp</c>) toward the commanded pose at <see cref="SmoothingSpeed"/> instead of
/// snapping - smoother on the eye, but with a small latency cost.
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
		// Nothing to pull. Values are pushed in one DOF at a time by whoever commands them
		// — an inlet or the control service — and `Show` has already put them on the rig.
		// Interpolating is the one thing that cannot happen at command time, because it
		// takes frames: step it here instead, toward the pose as it currently stands.
		if (EnableSmoothing)
			MoveBonesSmoothly(delta);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (communicationController != null && skeleton != null && boneMap.Count > 0)
			communicationController.SendPredictedData(ReadStandardPose());
	}

	/// <summary>Snap only when nothing is interpolating; otherwise `_Process` gets there.</summary>
	protected override void ShowPose()
	{
		if (!EnableSmoothing)
			RenderPose();
	}

	private void MoveBonesSmoothly(double delta)
	{
		if (skeleton == null || boneMap.Count == 0)
			return;
		float lerpFactor = (float)(SmoothingSpeed * delta);

		// Wrist
		SmoothBoneRotation(0, pose[6] * StandardPose.AtPlusOne[0][0], pose[8] * StandardPose.AtPlusOne[0][1], pose[7] * StandardPose.AtPlusOne[0][2], lerpFactor);

		// Thumb
		SmoothBoneRotation(1, pose[0] * StandardPose.AtPlusOne[1][0], 0, pose[1] * StandardPose.AtPlusOne[1][2], lerpFactor);
		SmoothBoneRotation(2, pose[0] * StandardPose.AtPlusOne[2][0], 0, pose[1] * StandardPose.AtPlusOne[2][2], lerpFactor);
		SmoothBoneRotation(3, pose[0] * StandardPose.AtPlusOne[3][0], 0, pose[1] * StandardPose.AtPlusOne[3][2], lerpFactor);

		// Index
		SmoothBoneRotation(4, pose[2] * StandardPose.AtPlusOne[4][0], 0, 0, lerpFactor);
		SmoothBoneRotation(5, pose[2] * StandardPose.AtPlusOne[5][0], 0, 0, lerpFactor);
		SmoothBoneRotation(6, pose[2] * StandardPose.AtPlusOne[6][0], 0, 0, lerpFactor);

		// Middle
		SmoothBoneRotation(7, pose[3] * StandardPose.AtPlusOne[7][0], 0, 0, lerpFactor);
		SmoothBoneRotation(8, pose[3] * StandardPose.AtPlusOne[8][0], 0, 0, lerpFactor);
		SmoothBoneRotation(9, pose[3] * StandardPose.AtPlusOne[9][0], 0, 0, lerpFactor);

		// Ring
		SmoothBoneRotation(10, pose[4] * StandardPose.AtPlusOne[10][0], 0, 0, lerpFactor);
		SmoothBoneRotation(11, pose[4] * StandardPose.AtPlusOne[11][0], 0, 0, lerpFactor);
		SmoothBoneRotation(12, pose[4] * StandardPose.AtPlusOne[12][0], 0, 0, lerpFactor);

		// Pinky
		SmoothBoneRotation(13, pose[5] * StandardPose.AtPlusOne[13][0], 0, 0, lerpFactor);
		SmoothBoneRotation(14, pose[5] * StandardPose.AtPlusOne[14][0], 0, 0, lerpFactor);
		SmoothBoneRotation(15, pose[5] * StandardPose.AtPlusOne[15][0], 0, 0, lerpFactor);
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

	// --- what a sweep needs to read back ------------------------------------------
	//
	// The pose itself, and setting one channel of it, live on `HandSkeleton`: both hands
	// are commanded one DOF at a time now, so neither owns that. What is still only this
	// hand's business is the read-back a `SweepControl` reports — the control hand is
	// never swept.

	/// <summary>
	/// The joints a channel can actually move on one axis: those whose gain on that
	/// axis is non-zero.
	/// </summary>
	/// <remarks>
	/// Not the same as every joint the channel drives, and the difference is real.
	/// Thumb abduction drives all three thumb bones through channel 1, but the distal
	/// one has a Z gain of 0 — so only two of them can move, and an expectation built
	/// from the channel alone would report a correct sweep as a mismatch.
	/// </remarks>
	/// <param name="channel">A pose channel, 0-8.</param>
	/// <param name="axisIndex">0 for X, 2 for Z — an index into the gain triple.</param>
	public int[] JointsMovableOnAxis(int channel, int axisIndex)
	{
		var movable = new List<int>();
		if (!JointsByChannel.TryGetValue(channel, out int[] joints))
			return [];
		foreach (int joint in joints)
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
	/// Signed rotation in degrees of every joint any channel drives, read back off the
	/// skeleton so a caller can see what actually moved rather than what was asked for.
	/// </summary>
	public Dictionary<int, Vector3> AnimatedJointDegrees()
	{
		var degrees = new Dictionary<int, Vector3>();
		foreach (int[] joints in JointsByChannel.Values)
		{
			foreach (int joint in joints)
			{
				degrees.TryAdd(joint, GetBoneRotationDegrees(joint));
			}
		}
		return degrees;
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
