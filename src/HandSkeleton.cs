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
/// on one, an LSL inlet on the other — and which outlet the read-back goes to.
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

	/// <summary>Every animated joint back to neutral.</summary>
	public void ResetBones()
	{
		if (skeleton == null)
			return;
		foreach (int bone in boneMap.Values)
			skeleton.SetBonePoseRotation(bone, Quaternion.Identity);
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
