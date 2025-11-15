using Godot;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Utility class to generate default movements.toml from hardcoded poses
/// </summary>
public static class MovementConfigGenerator
{
	private static readonly string[] JointNames =
	[
		"wrist",
		"thumb_proximal",
		"thumb_middle",
		"thumb_distal",
		"index_proximal",
		"index_middle",
		"index_distal",
		"middle_proximal",
		"middle_middle",
		"middle_distal",
		"ring_proximal",
		"ring_middle",
		"ring_distal",
		"pinky_proximal",
		"pinky_middle",
		"pinky_distal"
	];

	/// <summary>
	/// Generate default movements.toml file from hardcoded poses
	/// </summary>
	public static void GenerateDefaultConfig(string filePath)
	{
		var poses = MovementPoses.GetMovementPoses();
		var sb = new StringBuilder();

		// Add header comment
		sb.AppendLine("# Virtual Hand Interface - Movement Configuration");
		sb.AppendLine("# Each movement defines target joint rotations (in degrees)");
		sb.AppendLine("# Rest position is always [0, 0, 0] for all joints");
		sb.AppendLine("#");
		sb.AppendLine("# Joint rotation format: [X, Y, Z] in degrees");
		sb.AppendLine("# - X axis: Flexion/Extension (negative = flexion/bend, positive = extension)");
		sb.AppendLine("# - Y axis: Lateral movement");
		sb.AppendLine("# - Z axis: Abduction/Adduction (thumb) or rotation");
		sb.AppendLine();

		// Export each movement
		foreach (var movement in System.Enum.GetValues<Movements>())
		{
			if (!poses.ContainsKey(movement))
				continue;

			sb.AppendLine($"[movements.{movement}]");

			var movementPoses = poses[movement];

			// Check if this movement has any non-zero values
			bool hasData = false;
			for (int jointIdx = 0; jointIdx < 16; jointIdx++)
			{
				var maxPose = movementPoses[jointIdx][0];
				if (maxPose[0] != 0 || maxPose[1] != 0 || maxPose[2] != 0)
				{
					hasData = true;
					break;
				}
			}

			// Add comment for empty movements
			if (!hasData)
			{
				sb.AppendLine("# All joints at neutral position (rest)");
			}

			// Write each joint
			for (int jointIdx = 0; jointIdx < 16; jointIdx++)
			{
				var maxPose = movementPoses[jointIdx][0]; // Only export max pose (state 0)

				// Format: joint_name = [x, y, z]
				sb.AppendLine($"{JointNames[jointIdx]} = [{maxPose[0]}, {maxPose[1]}, {maxPose[2]}]");
			}

			sb.AppendLine(); // Blank line between movements
		}

		// Write to file
		try
		{
			using var file = FileAccess.Open(filePath, FileAccess.ModeFlags.Write);
			if (file == null)
			{
				GD.PrintErr($"❌ Failed to create config file: {filePath}");
				return;
			}

			file.StoreString(sb.ToString());
			GD.Print($"✅ Generated default config: {filePath}");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"❌ Error generating config: {e.Message}");
		}
	}
}
