using Godot;
using System;
using System.Collections.Generic;

public partial class PredictedHandSkeleton : Node3D
{
	[Export] public NodePath SkeletonPath;
	[Export] public bool EnableSmoothing = false;
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

	private void InitializeJointMovements()
	{
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

		// Thumb Flexion
		var thumb2Rot = GetBoneRotationDegrees(1);
		outputData.Add(thumb2Rot.X / jointMovements[1][0]);

		// Thumb Abduction
		outputData.Add(thumb2Rot.Z / jointMovements[1][2]);

		// Index Flexion
		var index2Rot = GetBoneRotationDegrees(4);
		outputData.Add(index2Rot.X / jointMovements[4][0]);

		// Middle Flexion
		var middle2Rot = GetBoneRotationDegrees(7);
		outputData.Add(middle2Rot.X / jointMovements[7][0]);

		// Ring Flexion
		var ring2Rot = GetBoneRotationDegrees(10);
		outputData.Add(ring2Rot.X / jointMovements[10][0]);

		// Pinky Flexion
		var pinky2Rot = GetBoneRotationDegrees(13);
		outputData.Add(pinky2Rot.X / jointMovements[13][0]);

		// Wrist (not used, but needed for compatibility)
		outputData.Add(0); // Wrist Flexion
		outputData.Add(0); // Wrist Abduction
		outputData.Add(0); // Wrist Rotation

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
}
