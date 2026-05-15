using System.Collections.Generic;

namespace Vhi;

/// <summary>
/// Hand movement definitions from Unity VirtualHandClassifier
/// Contains all movement types and their corresponding joint poses
/// </summary>
public enum Movements
{
	Rest = 0,
	Thumb = 1,
	Index = 2,
	Middle = 3,
	Ring = 4,
	Pinky = 5,
	Fist = 6,
	TwoFingerPinch = 7,
	ThreeFingerPinch = 8,
	Pointing = 9,
	ThumbExtension = 10,
	IndexExtension = 11,
	MiddleExtension = 12,
	RingExtension = 13,
	PinkyExtension = 14,
	WristUpDown = 15,
	WristLeftRight = 16,
	PrecisionSphere = 17,
	RockNRoll = 18,
	Hook = 19,
	PeaceSign = 20,
	Pistol = 21,
	ExtendedHand = 22
}

/// <summary>
/// Movement mode types
/// </summary>
public enum MovementMode
{
	AI,          // 17 movements
	Classifier   // 15 movements
}

/// <summary>
/// How the control hand is driven. The default, Movement, preserves the prior
/// behaviour — nothing changes unless a caller switches the mode.
/// </summary>
public enum ControlHandDriverMode
{
	Movement,  // predefined-movement state machine + local keyboard
	Stream,    // continuous pose from the MyoGestic_ControlPose LSL inlet
	Idle       // hold rest pose; ignore keyboard, stream, and movement commands
}

/// <summary>
/// Static class containing all movement pose definitions
/// Data structure: [16 joints][2 states (max/min)][3 axes (x,y,z)]
/// </summary>
public static class MovementPoses
{
	// AI Mode movements (17 total)
	public static readonly Movements[] AIModeMovements =
    [
        Movements.Rest, Movements.Thumb, Movements.Index, Movements.Middle, Movements.Ring, Movements.Pinky,
		Movements.Fist, Movements.TwoFingerPinch, Movements.ThreeFingerPinch, Movements.Pointing,
		Movements.ThumbExtension, Movements.IndexExtension, Movements.MiddleExtension,
		Movements.RingExtension, Movements.PinkyExtension, Movements.WristUpDown, Movements.WristLeftRight
	];

	// Classifier Mode movements (15 total)
	public static readonly Movements[] ClassifierModeMovements =
    [
        Movements.Rest, Movements.Thumb, Movements.Index, Movements.Middle, Movements.Ring, Movements.Pinky,
		Movements.Fist, Movements.TwoFingerPinch, Movements.ThreeFingerPinch, Movements.PrecisionSphere,
		Movements.RockNRoll, Movements.Hook, Movements.PeaceSign, Movements.Pistol, Movements.ExtendedHand
	];

	// Dictionary storing all movement poses
	// Key: Movement type
	// Value: [16 joints][2 states][3 axes] - joint rotations in degrees
	public static Dictionary<Movements, float[][][]> GetMovementPoses()
	{
		var poses = new Dictionary<Movements, float[][][]>();

		// Initialize all movements with 16 joints, 2 states, 3 axes
		foreach (Movements movement in System.Enum.GetValues(typeof(Movements)))
		{
			poses[movement] = new float[16][][];
			for (int i = 0; i < 16; i++)
			{
				poses[movement][i] = new float[2][];
				poses[movement][i][0] = [0, 0, 0]; // Max flexion state
				poses[movement][i][1] = [0, 0, 0]; // Min/rest state
			}
		}

		// Define all movement poses based on Unity CalibrationManagerAI.cs
		DefineRestPose(poses);
		DefineThumbMovement(poses);
		DefineIndexMovement(poses);
		DefineMiddleMovement(poses);
		DefineRingMovement(poses);
		DefinePinkyMovement(poses);
		DefineFistMovement(poses);
		DefineTwoFingerPinch(poses);
		DefineThreeFingerPinch(poses);
		DefinePointingMovement(poses);
		DefineExtensionMovements(poses);
		DefineWristMovements(poses);
		DefineComplexGestures(poses);

		return poses;
	}

	private static void DefineRestPose(Dictionary<Movements, float[][][]> poses)
	{
		// Rest: All joints at neutral position
		// Already initialized to zeros, no changes needed
	}

	private static void DefineThumbMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Thumb flexion (distal joint stays at 0 to match Unity behavior)
		poses[Movements.Thumb][1][0] = [-45, 0, 0];  // Thumb proximal max
		poses[Movements.Thumb][2][0] = [-55, 0, 0];  // Thumb middle max
		poses[Movements.Thumb][3][0] = [0, 0, 0];    // Thumb distal max (0 to allow thumb to reach)

		poses[Movements.Thumb][1][1] = [0, 0, 0];
		poses[Movements.Thumb][2][1] = [-10, 0, 0];
		poses[Movements.Thumb][3][1] = [0, 0, 0];
	}

	private static void DefineIndexMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Index finger flexion
		poses[Movements.Index][4][0] = [-85, 0, 0];  // Index proximal max
		poses[Movements.Index][5][0] = [-75, 0, 0];  // Index middle max
		poses[Movements.Index][6][0] = [-60, 0, 0];  // Index distal max

		poses[Movements.Index][4][1] = [0, 0, 0];
		poses[Movements.Index][5][1] = [0, 0, 0];
		poses[Movements.Index][6][1] = [0, 0, 0];
	}

	private static void DefineMiddleMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Middle finger flexion
		poses[Movements.Middle][7][0] = [-85, 0, 0];
		poses[Movements.Middle][8][0] = [-85, 0, 0];
		poses[Movements.Middle][9][0] = [-60, 0, 0];

		poses[Movements.Middle][7][1] = [0, 0, 0];
		poses[Movements.Middle][8][1] = [0, 0, 0];
		poses[Movements.Middle][9][1] = [0, 0, 0];
	}

	private static void DefineRingMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Ring finger flexion
		poses[Movements.Ring][10][0] = [-85, 0, 0];
		poses[Movements.Ring][11][0] = [-85, 0, 0];
		poses[Movements.Ring][12][0] = [-60, 0, 0];

		poses[Movements.Ring][10][1] = [0, 0, 0];
		poses[Movements.Ring][11][1] = [0, 0, 0];
		poses[Movements.Ring][12][1] = [0, 0, 0];
	}

	private static void DefinePinkyMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Pinky finger flexion
		poses[Movements.Pinky][13][0] = [-85, 0, 0];
		poses[Movements.Pinky][14][0] = [-85, 0, 0];
		poses[Movements.Pinky][15][0] = [-60, 0, 0];

		poses[Movements.Pinky][13][1] = [0, 0, 0];
		poses[Movements.Pinky][14][1] = [0, 0, 0];
		poses[Movements.Pinky][15][1] = [0, 0, 0];
	}

	private static void DefineFistMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Fist: All fingers closed
		// Thumb
		poses[Movements.Fist][1][0] = [-45, 0, 30];
		poses[Movements.Fist][2][0] = [-55, 0, -35];
		poses[Movements.Fist][3][0] = [-80, 0, 0];
		// Index
		poses[Movements.Fist][4][0] = [-85, 0, 0];
		poses[Movements.Fist][5][0] = [-75, 0, 0];
		poses[Movements.Fist][6][0] = [-60, 0, 0];
		// Middle
		poses[Movements.Fist][7][0] = [-85, 0, 0];
		poses[Movements.Fist][8][0] = [-85, 0, 0];
		poses[Movements.Fist][9][0] = [-60, 0, 0];
		// Ring
		poses[Movements.Fist][10][0] = [-85, 0, 0];
		poses[Movements.Fist][11][0] = [-85, 0, 0];
		poses[Movements.Fist][12][0] = [-60, 0, 0];
		// Pinky
		poses[Movements.Fist][13][0] = [-85, 0, 0];
		poses[Movements.Fist][14][0] = [-85, 0, 0];
		poses[Movements.Fist][15][0] = [-60, 0, 0];
	}

	private static void DefineTwoFingerPinch(Dictionary<Movements, float[][][]> poses)
	{
		// Two-finger pinch: Thumb + Index
		poses[Movements.TwoFingerPinch][1][0] = [-30, 0, 20];
		poses[Movements.TwoFingerPinch][2][0] = [-40, 0, -20];
		poses[Movements.TwoFingerPinch][3][0] = [-60, 0, 0];
		poses[Movements.TwoFingerPinch][4][0] = [-60, 0, 0];
		poses[Movements.TwoFingerPinch][5][0] = [-50, 0, 0];
		poses[Movements.TwoFingerPinch][6][0] = [-40, 0, 0];
	}

	private static void DefineThreeFingerPinch(Dictionary<Movements, float[][][]> poses)
	{
		// Three-finger pinch: Thumb + Index + Middle
		poses[Movements.ThreeFingerPinch][1][0] = [-30, 0, 20];
		poses[Movements.ThreeFingerPinch][2][0] = [-40, 0, -20];
		poses[Movements.ThreeFingerPinch][3][0] = [-60, 0, 0];
		poses[Movements.ThreeFingerPinch][4][0] = [-60, 0, 0];
		poses[Movements.ThreeFingerPinch][5][0] = [-50, 0, 0];
		poses[Movements.ThreeFingerPinch][6][0] = [-40, 0, 0];
		poses[Movements.ThreeFingerPinch][7][0] = [-60, 0, 0];
		poses[Movements.ThreeFingerPinch][8][0] = [-50, 0, 0];
		poses[Movements.ThreeFingerPinch][9][0] = [-40, 0, 0];
	}

	private static void DefinePointingMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Pointing: Index extended, other fingers closed
		poses[Movements.Pointing][1][0] = [-45, 0, 30];
		poses[Movements.Pointing][2][0] = [-55, 0, -35];
		poses[Movements.Pointing][3][0] = [-80, 0, 0];
		// Index stays extended (at rest)
		poses[Movements.Pointing][7][0] = [-85, 0, 0];
		poses[Movements.Pointing][8][0] = [-85, 0, 0];
		poses[Movements.Pointing][9][0] = [-60, 0, 0];
		poses[Movements.Pointing][10][0] = [-85, 0, 0];
		poses[Movements.Pointing][11][0] = [-85, 0, 0];
		poses[Movements.Pointing][12][0] = [-60, 0, 0];
		poses[Movements.Pointing][13][0] = [-85, 0, 0];
		poses[Movements.Pointing][14][0] = [-85, 0, 0];
		poses[Movements.Pointing][15][0] = [-60, 0, 0];
	}

	private static void DefineExtensionMovements(Dictionary<Movements, float[][][]> poses)
	{
		// Extension movements (fingers extended backward)
		// Thumb extension
		poses[Movements.ThumbExtension][1][0] = [30, 0, 0];
		poses[Movements.ThumbExtension][2][0] = [20, 0, 0];
		poses[Movements.ThumbExtension][3][0] = [10, 0, 0];

		// Index extension
		poses[Movements.IndexExtension][4][0] = [20, 0, 0];
		poses[Movements.IndexExtension][5][0] = [15, 0, 0];
		poses[Movements.IndexExtension][6][0] = [10, 0, 0];

		// Middle extension
		poses[Movements.MiddleExtension][7][0] = [20, 0, 0];
		poses[Movements.MiddleExtension][8][0] = [15, 0, 0];
		poses[Movements.MiddleExtension][9][0] = [10, 0, 0];

		// Ring extension
		poses[Movements.RingExtension][10][0] = [20, 0, 0];
		poses[Movements.RingExtension][11][0] = [15, 0, 0];
		poses[Movements.RingExtension][12][0] = [10, 0, 0];

		// Pinky extension
		poses[Movements.PinkyExtension][13][0] = [20, 0, 0];
		poses[Movements.PinkyExtension][14][0] = [15, 0, 0];
		poses[Movements.PinkyExtension][15][0] = [10, 0, 0];
	}

	private static void DefineWristMovements(Dictionary<Movements, float[][][]> poses)
	{
		// Wrist up/down
		poses[Movements.WristUpDown][0][0] = [30, 0, 0];
		poses[Movements.WristUpDown][0][1] = [-30, 0, 0];

		// Wrist left/right
		poses[Movements.WristLeftRight][0][0] = [0, 0, 20];
		poses[Movements.WristLeftRight][0][1] = [0, 0, -20];
	}

	private static void DefineComplexGestures(Dictionary<Movements, float[][][]> poses)
	{
		// Precision sphere grip
		poses[Movements.PrecisionSphere][1][0] = [-40, 0, 25];
		poses[Movements.PrecisionSphere][2][0] = [-50, 0, -30];
		poses[Movements.PrecisionSphere][3][0] = [-70, 0, 0];
		poses[Movements.PrecisionSphere][4][0] = [-70, 0, 0];
		poses[Movements.PrecisionSphere][5][0] = [-60, 0, 0];
		poses[Movements.PrecisionSphere][6][0] = [-50, 0, 0];
		poses[Movements.PrecisionSphere][7][0] = [-70, 0, 0];
		poses[Movements.PrecisionSphere][8][0] = [-60, 0, 0];
		poses[Movements.PrecisionSphere][9][0] = [-50, 0, 0];
		poses[Movements.PrecisionSphere][10][0] = [-70, 0, 0];
		poses[Movements.PrecisionSphere][11][0] = [-60, 0, 0];
		poses[Movements.PrecisionSphere][12][0] = [-50, 0, 0];
		poses[Movements.PrecisionSphere][13][0] = [-70, 0, 0];
		poses[Movements.PrecisionSphere][14][0] = [-60, 0, 0];
		poses[Movements.PrecisionSphere][15][0] = [-50, 0, 0];

		// Rock and roll gesture (index + pinky extended)
		poses[Movements.RockNRoll][1][0] = [-45, 0, 30];
		poses[Movements.RockNRoll][2][0] = [-55, 0, -35];
		poses[Movements.RockNRoll][3][0] = [-80, 0, 0];
		poses[Movements.RockNRoll][7][0] = [-85, 0, 0];
		poses[Movements.RockNRoll][8][0] = [-85, 0, 0];
		poses[Movements.RockNRoll][9][0] = [-60, 0, 0];
		poses[Movements.RockNRoll][10][0] = [-85, 0, 0];
		poses[Movements.RockNRoll][11][0] = [-85, 0, 0];
		poses[Movements.RockNRoll][12][0] = [-60, 0, 0];

		// Hook grip
		poses[Movements.Hook][4][0] = [-85, 0, 0];
		poses[Movements.Hook][5][0] = [-75, 0, 0];
		poses[Movements.Hook][6][0] = [-60, 0, 0];
		poses[Movements.Hook][7][0] = [-85, 0, 0];
		poses[Movements.Hook][8][0] = [-85, 0, 0];
		poses[Movements.Hook][9][0] = [-60, 0, 0];
		poses[Movements.Hook][10][0] = [-85, 0, 0];
		poses[Movements.Hook][11][0] = [-85, 0, 0];
		poses[Movements.Hook][12][0] = [-60, 0, 0];
		poses[Movements.Hook][13][0] = [-85, 0, 0];
		poses[Movements.Hook][14][0] = [-85, 0, 0];
		poses[Movements.Hook][15][0] = [-60, 0, 0];

		// Peace sign (index + middle extended)
		poses[Movements.PeaceSign][1][0] = [-45, 0, 30];
		poses[Movements.PeaceSign][2][0] = [-55, 0, -35];
		poses[Movements.PeaceSign][3][0] = [-80, 0, 0];
		poses[Movements.PeaceSign][10][0] = [-85, 0, 0];
		poses[Movements.PeaceSign][11][0] = [-85, 0, 0];
		poses[Movements.PeaceSign][12][0] = [-60, 0, 0];
		poses[Movements.PeaceSign][13][0] = [-85, 0, 0];
		poses[Movements.PeaceSign][14][0] = [-85, 0, 0];
		poses[Movements.PeaceSign][15][0] = [-60, 0, 0];

		// Pistol gesture (thumb up, index extended)
		poses[Movements.Pistol][7][0] = [-85, 0, 0];
		poses[Movements.Pistol][8][0] = [-85, 0, 0];
		poses[Movements.Pistol][9][0] = [-60, 0, 0];
		poses[Movements.Pistol][10][0] = [-85, 0, 0];
		poses[Movements.Pistol][11][0] = [-85, 0, 0];
		poses[Movements.Pistol][12][0] = [-60, 0, 0];
		poses[Movements.Pistol][13][0] = [-85, 0, 0];
		poses[Movements.Pistol][14][0] = [-85, 0, 0];
		poses[Movements.Pistol][15][0] = [-60, 0, 0];

		// Extended hand (all fingers extended)
		// Already at rest position, no changes needed
	}
}
