using Godot;
using System.Collections.Generic;
using System.Text;

namespace Vhi;

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
		sb.AppendLine("# - X axis: Flexion/Extension (POSITIVE = flexion/bend, negative = extension)");
		sb.AppendLine("# - Y axis: Lateral movement");
		sb.AppendLine("# - Z axis: Abduction/Adduction (thumb) or rotation");
		sb.AppendLine("#");
		sb.AppendLine("# `convention` says which way the X signs run. Files written before it");
		sb.AppendLine("# existed are \"unity-signed\" — flexion negative — and are flipped on load.");
		sb.AppendLine("# Delete the key and the signs below will be read as their own opposite.");
		sb.AppendLine($"convention = \"{MovementConfigLoader.RigNativeConvention}\"");
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

	/// <summary>
	/// Rewrite a Unity-signed config in rig-native degrees, once. A file that already
	/// declares its convention is left untouched.
	/// </summary>
	/// <remarks>
	/// The alternative — converting on every load — leaves a file on disk whose numbers say
	/// the opposite of what they mean, forever, and the person who hand-edits it next gets a
	/// hand that bends the wrong way. Migrating negates every rotation and stamps the
	/// convention, so the shim runs once and the file is then self-describing.
	/// <para>A backup is written beside it first: this rewrites hand-tuned poses, and the
	/// migration is only correct if the file really was Unity-signed.</para>
	/// </remarks>
	public static void MigrateToRigNative(string filePath)
	{
		try
		{
			using (var read = FileAccess.Open(filePath, FileAccess.ModeFlags.Read))
			{
				if (read == null)
					return;
				string existing = read.GetAsText();
				var model = Tomlyn.Toml.ToModel(existing);
				if (MovementConfigLoader.ConventionOf(model) == MovementConfigLoader.RigNativeConvention)
					return;

				using var backup = FileAccess.Open(filePath + ".unity-signed.bak", FileAccess.ModeFlags.Write);
				backup?.StoreString(existing);
			}

			// Re-emitting from the (already rig-native) hardcoded table would discard any
			// hand-tuning in the file, so negate what is there instead.
			var migrated = NegateRotations(filePath);
			if (migrated == null)
				return;

			using var write = FileAccess.Open(filePath, FileAccess.ModeFlags.Write);
			if (write == null)
			{
				GD.PrintErr($"❌ Could not rewrite {filePath}; it stays Unity-signed.");
				return;
			}
			write.StoreString(migrated);
			GD.Print($"✅ Migrated {filePath} to rig-native degrees (backup: *.unity-signed.bak)");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"❌ Error migrating config: {e.Message}");
		}
	}

	/// <summary>Negate every `joint = [x, y, z]` and prepend the convention key.</summary>
	private static string NegateRotations(string filePath)
	{
		using var file = FileAccess.Open(filePath, FileAccess.ModeFlags.Read);
		if (file == null)
			return null;

		var sb = new StringBuilder();
		sb.AppendLine($"convention = \"{MovementConfigLoader.RigNativeConvention}\"");
		sb.AppendLine("# Migrated from Unity-signed degrees: POSITIVE X is now flexion.");
		sb.AppendLine();

		var row = new System.Text.RegularExpressions.Regex(
			@"^(\s*\w+\s*=\s*\[)\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*(\])");
		foreach (string line in file.GetAsText().Split('\n'))
		{
			string text = line.TrimEnd('\r');
			var match = row.Match(text);
			if (!match.Success)
			{
				sb.AppendLine(text);
				continue;
			}
			string Flip(int group) =>
				(-float.Parse(
					match.Groups[group].Value,
					System.Globalization.CultureInfo.InvariantCulture)
				+ 0f).ToString(System.Globalization.CultureInfo.InvariantCulture);
			sb.AppendLine($"{match.Groups[1].Value}{Flip(2)}, {Flip(3)}, {Flip(4)}{match.Groups[5].Value}");
		}
		return sb.ToString();
	}
}
