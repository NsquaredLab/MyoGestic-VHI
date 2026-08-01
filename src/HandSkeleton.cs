using Godot;
using System.Collections.Generic;

namespace Vhi;

/// <summary>
/// The 16-joint hand rig both hands drive, and the pose read-back both publish.
/// </summary>
/// <remarks>
/// Two hands, one FBX, one bone map, one set of rotations. This existed twice, member for
/// member, and the copies were the reason a direction bug could be fixed in one hand and
/// not the other. What differs is only what drives the joints — a movement state machine
/// on one, a set of per-DOF LSL inlets on the other — and which outlet the read-back goes to.
/// </remarks>
public abstract partial class HandSkeleton : Node3D
{
	/// <summary>Joint index -> bone name in the FBX (WaveBone naming).</summary>
	/// <remarks>
	/// 0 is the wrist, then thumb, index, middle, ring and pinky, proximal to distal. The
	/// gaps are the model's, not ours: `WaveBone_2`, `_6`, `_10` and so on are the digits'
	/// tip bones, which nothing animates.
	/// </remarks>
	protected static readonly string[] BoneNames =
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

	protected Skeleton3D skeleton;
	protected readonly Dictionary<string, int> boneMap = [];

	/// <summary>Which joints each pose channel drives.</summary>
	/// <remarks>
	/// The nine channels of a hand pose: 0-1 the thumb's two axes, 2-5 the four single-axis
	/// digits, 6-8 the wrist's three. A channel is an <i>internal</i> slot now — a DOF arrives
	/// on a stream of its own and lands here — so nothing outside VHI indexes by these numbers.
	/// </remarks>
	protected static readonly Dictionary<int, int[]> JointsByChannel = new()
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

	/// <summary>The nine standard values this hand was last commanded to.</summary>
	/// <remarks>
	/// Standard: <c>+1</c> is the direction the DOF's name denotes, <c>0</c> is rest. It stays
	/// standard right up to <see cref="RenderPose"/>, which multiplies by
	/// <see cref="StandardPose.AtPlusOne"/> — no sign is applied anywhere else.
	/// <para>It is a <i>held</i> pose, not a frame: each channel keeps its last commanded value
	/// until something commands that channel again. Nine DOFs arriving on nine streams at nine
	/// rates is the normal case, and a hand whose index has moved and whose thumb has not is a
	/// real pose rather than a half-delivered one.</para>
	/// </remarks>
	protected readonly List<float> pose = [0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];

	/// <summary>Put <see cref="pose"/> on the rig, snapping to it.</summary>
	protected void RenderPose()
	{
		if (skeleton == null || boneMap.Count == 0)
			return;

		// Wrist (channels 6, 7, 8: flexion, abduction, rotation). Joint 0 parents every
		// digit, so the whole hand turns with it.
		SetBoneRotation(0, pose[6] * StandardPose.AtPlusOne[0][0], pose[8] * StandardPose.AtPlusOne[0][1], pose[7] * StandardPose.AtPlusOne[0][2]);

		// Thumb (channels 0 and 1: flexion and abduction).
		SetBoneRotation(1, pose[0] * StandardPose.AtPlusOne[1][0], 0, pose[1] * StandardPose.AtPlusOne[1][2]);
		SetBoneRotation(2, pose[0] * StandardPose.AtPlusOne[2][0], 0, pose[1] * StandardPose.AtPlusOne[2][2]);
		SetBoneRotation(3, pose[0] * StandardPose.AtPlusOne[3][0], 0, pose[1] * StandardPose.AtPlusOne[3][2]);

		// Index (channel 2)
		SetBoneRotation(4, pose[2] * StandardPose.AtPlusOne[4][0], 0, 0);
		SetBoneRotation(5, pose[2] * StandardPose.AtPlusOne[5][0], 0, 0);
		SetBoneRotation(6, pose[2] * StandardPose.AtPlusOne[6][0], 0, 0);

		// Middle (channel 3)
		SetBoneRotation(7, pose[3] * StandardPose.AtPlusOne[7][0], 0, 0);
		SetBoneRotation(8, pose[3] * StandardPose.AtPlusOne[8][0], 0, 0);
		SetBoneRotation(9, pose[3] * StandardPose.AtPlusOne[9][0], 0, 0);

		// Ring (channel 4)
		SetBoneRotation(10, pose[4] * StandardPose.AtPlusOne[10][0], 0, 0);
		SetBoneRotation(11, pose[4] * StandardPose.AtPlusOne[11][0], 0, 0);
		SetBoneRotation(12, pose[4] * StandardPose.AtPlusOne[12][0], 0, 0);

		// Pinky (channel 5)
		SetBoneRotation(13, pose[5] * StandardPose.AtPlusOne[13][0], 0, 0);
		SetBoneRotation(14, pose[5] * StandardPose.AtPlusOne[14][0], 0, 0);
		SetBoneRotation(15, pose[5] * StandardPose.AtPlusOne[15][0], 0, 0);
	}

	/// <summary>Show the commanded pose. Overridden by a hand that interpolates toward it.</summary>
	/// <remarks>Not <c>Show</c>: that is <see cref="Node3D"/>'s, and it makes the node visible.</remarks>
	protected virtual void ShowPose() => RenderPose();

	/// <summary>
	/// Command one channel and show it, leaving every other channel where it was.
	/// </summary>
	/// <remarks>
	/// The only way a value reaches either hand: one DOF, applied when it arrives. Its
	/// callers are the per-DOF LSL inlets and <c>VhiControlService.SetControl</c>, and a
	/// client driving both will fight itself over whichever channels they share.
	/// </remarks>
	/// <param name="channel">A pose channel, 0-8. Anything else is ignored.</param>
	/// <param name="standard">A standard value; clamped to <c>[-1, 1]</c> before it is stored.</param>
	public void SetStandardValue(int channel, float standard)
	{
		if (skeleton == null || boneMap.Count == 0 || !JointsByChannel.ContainsKey(channel))
			return;
		pose[channel] = StandardPose.Clamp(standard);
		ShowPose();
	}

	/// <summary>Every channel back to standard rest, snapped to immediately.</summary>
	/// <remarks>
	/// Also the neutraliser: a zero pose renders every animated joint to identity, so this
	/// is what a hand released back to its own movements is put through. It has to clear
	/// <see cref="pose"/> and not just the rig — otherwise the next single DOF to arrive
	/// would call <see cref="RenderPose"/> and bring a departed producer's other eight
	/// values back with it.
	/// </remarks>
	public void RestStandardPose()
	{
		for (int i = 0; i < pose.Count; i++)
			pose[i] = 0f;
		RenderPose();
	}

	/// <summary>Find the rig inside the FBX child and map the 16 animated bones.</summary>
	protected void FindAndMapSkeleton()
	{
		skeleton = FindSkeletonRecursive(this);
		if (skeleton == null)
		{
			GD.PrintErr("⚠️ No Skeleton3D found! Hand won't animate.");
			return;
		}
		GD.Print($"✅ Found Skeleton3D with {skeleton.GetBoneCount()} bones");
		boneMap.Clear();
		foreach (string name in BoneNames)
		{
			int boneIdx = skeleton.FindBone(name);
			if (boneIdx != -1)
				boneMap[name] = boneIdx;
			else
				GD.PrintErr($"  ⚠️ Bone '{name}' not found in skeleton!");
		}
		GD.Print($"  Mapped {boneMap.Count}/{BoneNames.Length} bones");
	}

	private static Skeleton3D FindSkeletonRecursive(Node node)
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

	/// <summary>Set one joint's local rotation, in degrees.</summary>
	protected void SetBoneRotation(int jointIndex, float xDeg, float yDeg, float zDeg)
	{
		if (jointIndex < 0 || jointIndex >= BoneNames.Length
			|| !boneMap.TryGetValue(BoneNames[jointIndex], out int boneIdx))
			return;
		Vector3 eulerRadians = new(Mathf.DegToRad(xDeg), Mathf.DegToRad(yDeg), Mathf.DegToRad(zDeg));
		skeleton.SetBonePoseRotation(boneIdx, new Quaternion(Basis.FromEuler(eulerRadians)));
	}

	/// <summary>One joint's local rotation, in degrees, read back off the rig.</summary>
	protected Vector3 GetBoneRotationDegrees(int jointIndex)
	{
		if (jointIndex < 0 || jointIndex >= BoneNames.Length
			|| !boneMap.TryGetValue(BoneNames[jointIndex], out int boneIdx))
			return Vector3.Zero;
		return skeleton.GetBonePoseRotation(boneIdx).GetEuler() * (180.0f / Mathf.Pi);
	}

	/// <summary>The rig's current pose as the nine standard values that would produce it.</summary>
	/// <remarks>
	/// The inverse of the conversion that rendered it, so a round-trip through either hand
	/// is the identity. That is also its limit: an inverse agrees with its forward whichever
	/// way the pair points, so this cannot catch a direction error on its own. The anchor
	/// for that is the contract suite, against the control hand's named movements.
	/// </remarks>
	protected List<float> ReadStandardPose()
	{
		var thumb = GetBoneRotationDegrees(1);
		var wrist = GetBoneRotationDegrees(0);
		return [
			StandardPose.Standard(1, 0, thumb.X),                       // thumb flexion
			StandardPose.Standard(1, 2, thumb.Z),                       // thumb abduction
			StandardPose.Standard(4, 0, GetBoneRotationDegrees(4).X),   // index
			StandardPose.Standard(7, 0, GetBoneRotationDegrees(7).X),   // middle
			StandardPose.Standard(10, 0, GetBoneRotationDegrees(10).X), // ring
			StandardPose.Standard(13, 0, GetBoneRotationDegrees(13).X), // little
			StandardPose.Standard(0, 0, wrist.X),                       // wrist flexion
			StandardPose.Standard(0, 2, wrist.Z),                       // wrist abduction
			StandardPose.Standard(0, 1, wrist.Y),                       // wrist rotation
		];
	}

	/// <summary>The model's own name for a joint — the rig's identity claim, not a label.</summary>
	public static string BoneNameForJoint(int jointIndex) =>
		jointIndex >= 0 && jointIndex < BoneNames.Length ? BoneNames[jointIndex] : $"joint {jointIndex}";
}
