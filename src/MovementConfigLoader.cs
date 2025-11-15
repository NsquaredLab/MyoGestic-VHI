using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Loads hand movement configurations from TOML files
/// </summary>
public static class MovementConfigLoader
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

	private static readonly Dictionary<string, int> JointNameToIndex = new()
	{
		{ "wrist", 0 },
		{ "thumb_proximal", 1 },
		{ "thumb_middle", 2 },
		{ "thumb_distal", 3 },
		{ "index_proximal", 4 },
		{ "index_middle", 5 },
		{ "index_distal", 6 },
		{ "middle_proximal", 7 },
		{ "middle_middle", 8 },
		{ "middle_distal", 9 },
		{ "ring_proximal", 10 },
		{ "ring_middle", 11 },
		{ "ring_distal", 12 },
		{ "pinky_proximal", 13 },
		{ "pinky_middle", 14 },
		{ "pinky_distal", 15 }
	};

	/// <summary>
	/// Load movements from TOML config file
	/// Returns Dictionary<movementName, float[16][2][3]>
	/// </summary>
	public static Dictionary<string, float[][][]> LoadConfig(string filePath)
	{
		try
		{
			// Read file
			using var file = FileAccess.Open(filePath, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				GD.PrintErr($"❌ Failed to open config file: {filePath}");
				return null;
			}

			string tomlContent = file.GetAsText();

			// Parse TOML
			var model = Toml.ToModel(tomlContent);

			if (!model.ContainsKey("movements"))
			{
				GD.PrintErr("❌ Config file missing 'movements' section");
				return null;
			}

			var movementsTable = model["movements"] as TomlTable;
			if (movementsTable == null)
			{
				GD.PrintErr("❌ Invalid 'movements' section format");
				return null;
			}

			// Convert to pose dictionary
			var result = new Dictionary<string, float[][][]>();

			foreach (var movement in movementsTable)
			{
				string movementName = movement.Key;
				var jointTable = movement.Value as TomlTable;

				if (jointTable == null)
				{
					GD.PrintErr($"⚠️ Skipping invalid movement: {movementName}");
					continue;
				}

				// Initialize pose array [16 joints][2 states][3 axes]
				float[][][] pose = new float[16][][];
				for (int i = 0; i < 16; i++)
				{
					pose[i] = new float[2][];
					pose[i][0] = [0, 0, 0]; // Max pose
					pose[i][1] = [0, 0, 0]; // Rest pose (always neutral)
				}

				// Parse each joint
				foreach (var joint in jointTable)
				{
					string jointName = joint.Key;

					if (!JointNameToIndex.ContainsKey(jointName))
					{
						GD.PrintErr($"⚠️ Unknown joint name '{jointName}' in movement '{movementName}'");
						continue;
					}

					int jointIdx = JointNameToIndex[jointName];

					// Parse rotation array [x, y, z]
					var rotationArray = joint.Value as TomlArray;
					if (rotationArray == null || rotationArray.Count != 3)
					{
						GD.PrintErr($"⚠️ Invalid rotation format for '{jointName}' in '{movementName}'");
						continue;
					}

					try
					{
						float x = Convert.ToSingle(rotationArray[0]);
						float y = Convert.ToSingle(rotationArray[1]);
						float z = Convert.ToSingle(rotationArray[2]);

						pose[jointIdx][0] = [x, y, z]; // Max pose
						// Rest pose stays [0, 0, 0]
					}
					catch (Exception e)
					{
						GD.PrintErr($"⚠️ Error parsing rotation for '{jointName}': {e.Message}");
					}
				}

				result[movementName] = pose;
			}

			GD.Print($"✅ Loaded {result.Count} movements from config");
			return result;
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Error loading config: {e.Message}");
			GD.PrintErr($"   Stack trace: {e.StackTrace}");
			return null;
		}
	}

	/// <summary>
	/// Get list of available movement names from config
	/// </summary>
	public static string[] GetMovementNames(Dictionary<string, float[][][]> poses)
	{
		return poses?.Keys.ToArray() ?? [];
	}
}
