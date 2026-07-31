using System;
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
/// How the control hand is driven. The default, Movement, preserves the prior
/// behaviour — nothing changes unless a caller switches the mode.
/// </summary>
public enum ControlHandDriverMode
{
	Movement,  // predefined-movement state machine + local keyboard
	Stream     // continuous pose from the MyoGestic_ControlPose LSL inlet
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
		poses[Movements.Thumb][1][0] = [45, 0, 0];  // Thumb proximal max
		poses[Movements.Thumb][2][0] = [55, 0, 0];  // Thumb middle max
		poses[Movements.Thumb][3][0] = [0, 0, 0];    // Thumb distal max (0 to allow thumb to reach)

		poses[Movements.Thumb][1][1] = [0, 0, 0];
		poses[Movements.Thumb][2][1] = [10, 0, 0];
		poses[Movements.Thumb][3][1] = [0, 0, 0];
	}

	private static void DefineIndexMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Index finger flexion
		poses[Movements.Index][4][0] = [85, 0, 0];  // Index proximal max
		poses[Movements.Index][5][0] = [75, 0, 0];  // Index middle max
		poses[Movements.Index][6][0] = [60, 0, 0];  // Index distal max

		poses[Movements.Index][4][1] = [0, 0, 0];
		poses[Movements.Index][5][1] = [0, 0, 0];
		poses[Movements.Index][6][1] = [0, 0, 0];
	}

	private static void DefineMiddleMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Middle finger flexion
		poses[Movements.Middle][7][0] = [85, 0, 0];
		poses[Movements.Middle][8][0] = [85, 0, 0];
		poses[Movements.Middle][9][0] = [60, 0, 0];

		poses[Movements.Middle][7][1] = [0, 0, 0];
		poses[Movements.Middle][8][1] = [0, 0, 0];
		poses[Movements.Middle][9][1] = [0, 0, 0];
	}

	private static void DefineRingMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Ring finger flexion
		poses[Movements.Ring][10][0] = [85, 0, 0];
		poses[Movements.Ring][11][0] = [85, 0, 0];
		poses[Movements.Ring][12][0] = [60, 0, 0];

		poses[Movements.Ring][10][1] = [0, 0, 0];
		poses[Movements.Ring][11][1] = [0, 0, 0];
		poses[Movements.Ring][12][1] = [0, 0, 0];
	}

	private static void DefinePinkyMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Pinky finger flexion
		poses[Movements.Pinky][13][0] = [85, 0, 0];
		poses[Movements.Pinky][14][0] = [85, 0, 0];
		poses[Movements.Pinky][15][0] = [60, 0, 0];

		poses[Movements.Pinky][13][1] = [0, 0, 0];
		poses[Movements.Pinky][14][1] = [0, 0, 0];
		poses[Movements.Pinky][15][1] = [0, 0, 0];
	}

	private static void DefineFistMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Fist: All fingers closed
		// Thumb
		poses[Movements.Fist][1][0] = [45, 0, -30];
		poses[Movements.Fist][2][0] = [55, 0, 35];
		poses[Movements.Fist][3][0] = [80, 0, 0];
		// Index
		poses[Movements.Fist][4][0] = [85, 0, 0];
		poses[Movements.Fist][5][0] = [75, 0, 0];
		poses[Movements.Fist][6][0] = [60, 0, 0];
		// Middle
		poses[Movements.Fist][7][0] = [85, 0, 0];
		poses[Movements.Fist][8][0] = [85, 0, 0];
		poses[Movements.Fist][9][0] = [60, 0, 0];
		// Ring
		poses[Movements.Fist][10][0] = [85, 0, 0];
		poses[Movements.Fist][11][0] = [85, 0, 0];
		poses[Movements.Fist][12][0] = [60, 0, 0];
		// Pinky
		poses[Movements.Fist][13][0] = [85, 0, 0];
		poses[Movements.Fist][14][0] = [85, 0, 0];
		poses[Movements.Fist][15][0] = [60, 0, 0];
	}

	private static void DefineTwoFingerPinch(Dictionary<Movements, float[][][]> poses)
	{
		// Two-finger pinch: Thumb + Index
		poses[Movements.TwoFingerPinch][1][0] = [30, 0, -20];
		poses[Movements.TwoFingerPinch][2][0] = [40, 0, 20];
		poses[Movements.TwoFingerPinch][3][0] = [60, 0, 0];
		poses[Movements.TwoFingerPinch][4][0] = [60, 0, 0];
		poses[Movements.TwoFingerPinch][5][0] = [50, 0, 0];
		poses[Movements.TwoFingerPinch][6][0] = [40, 0, 0];
	}

	private static void DefineThreeFingerPinch(Dictionary<Movements, float[][][]> poses)
	{
		// Three-finger pinch: Thumb + Index + Middle
		poses[Movements.ThreeFingerPinch][1][0] = [30, 0, -20];
		poses[Movements.ThreeFingerPinch][2][0] = [40, 0, 20];
		poses[Movements.ThreeFingerPinch][3][0] = [60, 0, 0];
		poses[Movements.ThreeFingerPinch][4][0] = [60, 0, 0];
		poses[Movements.ThreeFingerPinch][5][0] = [50, 0, 0];
		poses[Movements.ThreeFingerPinch][6][0] = [40, 0, 0];
		poses[Movements.ThreeFingerPinch][7][0] = [60, 0, 0];
		poses[Movements.ThreeFingerPinch][8][0] = [50, 0, 0];
		poses[Movements.ThreeFingerPinch][9][0] = [40, 0, 0];
	}

	private static void DefinePointingMovement(Dictionary<Movements, float[][][]> poses)
	{
		// Pointing: Index extended, other fingers closed
		poses[Movements.Pointing][1][0] = [45, 0, -30];
		poses[Movements.Pointing][2][0] = [55, 0, 35];
		poses[Movements.Pointing][3][0] = [80, 0, 0];
		// Index stays extended (at rest)
		poses[Movements.Pointing][7][0] = [85, 0, 0];
		poses[Movements.Pointing][8][0] = [85, 0, 0];
		poses[Movements.Pointing][9][0] = [60, 0, 0];
		poses[Movements.Pointing][10][0] = [85, 0, 0];
		poses[Movements.Pointing][11][0] = [85, 0, 0];
		poses[Movements.Pointing][12][0] = [60, 0, 0];
		poses[Movements.Pointing][13][0] = [85, 0, 0];
		poses[Movements.Pointing][14][0] = [85, 0, 0];
		poses[Movements.Pointing][15][0] = [60, 0, 0];
	}

	private static void DefineExtensionMovements(Dictionary<Movements, float[][][]> poses)
	{
		// Extension movements (fingers extended backward)
		// Thumb extension
		poses[Movements.ThumbExtension][1][0] = [-30, 0, 0];
		poses[Movements.ThumbExtension][2][0] = [-20, 0, 0];
		poses[Movements.ThumbExtension][3][0] = [-10, 0, 0];

		// Index extension
		poses[Movements.IndexExtension][4][0] = [-20, 0, 0];
		poses[Movements.IndexExtension][5][0] = [-15, 0, 0];
		poses[Movements.IndexExtension][6][0] = [-10, 0, 0];

		// Middle extension
		poses[Movements.MiddleExtension][7][0] = [-20, 0, 0];
		poses[Movements.MiddleExtension][8][0] = [-15, 0, 0];
		poses[Movements.MiddleExtension][9][0] = [-10, 0, 0];

		// Ring extension
		poses[Movements.RingExtension][10][0] = [-20, 0, 0];
		poses[Movements.RingExtension][11][0] = [-15, 0, 0];
		poses[Movements.RingExtension][12][0] = [-10, 0, 0];

		// Pinky extension
		poses[Movements.PinkyExtension][13][0] = [-20, 0, 0];
		poses[Movements.PinkyExtension][14][0] = [-15, 0, 0];
		poses[Movements.PinkyExtension][15][0] = [-10, 0, 0];
	}

	private static void DefineWristMovements(Dictionary<Movements, float[][][]> poses)
	{
		// Wrist up/down
		poses[Movements.WristUpDown][0][0] = [-30, 0, 0];
		poses[Movements.WristUpDown][0][1] = [30, 0, 0];

		// Wrist left/right
		poses[Movements.WristLeftRight][0][0] = [0, 0, -20];
		poses[Movements.WristLeftRight][0][1] = [0, 0, 20];
	}

	private static void DefineComplexGestures(Dictionary<Movements, float[][][]> poses)
	{
		// Precision sphere grip
		poses[Movements.PrecisionSphere][1][0] = [40, 0, -25];
		poses[Movements.PrecisionSphere][2][0] = [50, 0, 30];
		poses[Movements.PrecisionSphere][3][0] = [70, 0, 0];
		poses[Movements.PrecisionSphere][4][0] = [70, 0, 0];
		poses[Movements.PrecisionSphere][5][0] = [60, 0, 0];
		poses[Movements.PrecisionSphere][6][0] = [50, 0, 0];
		poses[Movements.PrecisionSphere][7][0] = [70, 0, 0];
		poses[Movements.PrecisionSphere][8][0] = [60, 0, 0];
		poses[Movements.PrecisionSphere][9][0] = [50, 0, 0];
		poses[Movements.PrecisionSphere][10][0] = [70, 0, 0];
		poses[Movements.PrecisionSphere][11][0] = [60, 0, 0];
		poses[Movements.PrecisionSphere][12][0] = [50, 0, 0];
		poses[Movements.PrecisionSphere][13][0] = [70, 0, 0];
		poses[Movements.PrecisionSphere][14][0] = [60, 0, 0];
		poses[Movements.PrecisionSphere][15][0] = [50, 0, 0];

		// Rock and roll gesture (index + pinky extended)
		poses[Movements.RockNRoll][1][0] = [45, 0, -30];
		poses[Movements.RockNRoll][2][0] = [55, 0, 35];
		poses[Movements.RockNRoll][3][0] = [80, 0, 0];
		poses[Movements.RockNRoll][7][0] = [85, 0, 0];
		poses[Movements.RockNRoll][8][0] = [85, 0, 0];
		poses[Movements.RockNRoll][9][0] = [60, 0, 0];
		poses[Movements.RockNRoll][10][0] = [85, 0, 0];
		poses[Movements.RockNRoll][11][0] = [85, 0, 0];
		poses[Movements.RockNRoll][12][0] = [60, 0, 0];

		// Hook grip
		poses[Movements.Hook][4][0] = [85, 0, 0];
		poses[Movements.Hook][5][0] = [75, 0, 0];
		poses[Movements.Hook][6][0] = [60, 0, 0];
		poses[Movements.Hook][7][0] = [85, 0, 0];
		poses[Movements.Hook][8][0] = [85, 0, 0];
		poses[Movements.Hook][9][0] = [60, 0, 0];
		poses[Movements.Hook][10][0] = [85, 0, 0];
		poses[Movements.Hook][11][0] = [85, 0, 0];
		poses[Movements.Hook][12][0] = [60, 0, 0];
		poses[Movements.Hook][13][0] = [85, 0, 0];
		poses[Movements.Hook][14][0] = [85, 0, 0];
		poses[Movements.Hook][15][0] = [60, 0, 0];

		// Peace sign (index + middle extended)
		poses[Movements.PeaceSign][1][0] = [45, 0, -30];
		poses[Movements.PeaceSign][2][0] = [55, 0, 35];
		poses[Movements.PeaceSign][3][0] = [80, 0, 0];
		poses[Movements.PeaceSign][10][0] = [85, 0, 0];
		poses[Movements.PeaceSign][11][0] = [85, 0, 0];
		poses[Movements.PeaceSign][12][0] = [60, 0, 0];
		poses[Movements.PeaceSign][13][0] = [85, 0, 0];
		poses[Movements.PeaceSign][14][0] = [85, 0, 0];
		poses[Movements.PeaceSign][15][0] = [60, 0, 0];

		// Pistol gesture (thumb up, index extended)
		poses[Movements.Pistol][7][0] = [85, 0, 0];
		poses[Movements.Pistol][8][0] = [85, 0, 0];
		poses[Movements.Pistol][9][0] = [60, 0, 0];
		poses[Movements.Pistol][10][0] = [85, 0, 0];
		poses[Movements.Pistol][11][0] = [85, 0, 0];
		poses[Movements.Pistol][12][0] = [60, 0, 0];
		poses[Movements.Pistol][13][0] = [85, 0, 0];
		poses[Movements.Pistol][14][0] = [85, 0, 0];
		poses[Movements.Pistol][15][0] = [60, 0, 0];

		// Extended hand (all fingers extended)
		// Already at rest position, no changes needed
	}
}

/// <summary>
/// The one place that knows what a standard value means in rig degrees.
/// </summary>
/// <remarks>
/// <para>A standard <c>+1</c> is the direction the DOF's name denotes, <c>0</c> is rest, and
/// the domain is <c>[-1, 1]</c>. <see cref="AtPlusOne"/> states, per joint and axis, the
/// rotation that <c>+1</c> produces — so a renderer multiplies, a reader divides, and
/// neither carries a sign of its own.</para>
/// <para><b>Positive X is flexion on this rig.</b> That is not what
/// <see cref="MovementPoses"/> appears to say, and the appearance is what this class exists
/// to end. Those rows were authored in Unity's convention and reached the skeleton through
/// <c>ApplyMovementPose</c>, which interpolated with a <i>negated</i> sine and so rendered
/// their opposite: a held <see cref="Movements.Fist"/> put bone 4 at <c>+85°</c>, never the
/// tabulated <c>-85°</c>. Reading the table alone gave the wrong answer, and everything that
/// did — both skeletons' gain tables, this class's own sign vector, the contract suite's
/// direction gate — agreed with each other and disagreed with the hand. The table is now
/// stated in rig degrees and the animation interpolates plainly, so there is one convention
/// and it is this one.</para>
/// <para>Channel 1 is abduction and the fist's thumb is <i>ad</i>ducted, so a standard fist
/// is <c>[+1, -1, +1, +1, +1, +1, …]</c> — the thumb comes across the fingers as it curls.
/// That is a fact about hands, not a quirk of the rig, and it is why the thumb is the digit
/// that exposes a direction error: it is the only one whose flexion is not symmetric
/// front-to-back, so a hand bending entirely the wrong way still looks plausible until you
/// watch the thumb.</para>
/// </remarks>
public static class StandardPose
{
	/// <summary>Joint 0's rotation at standard <c>+1</c>, in degrees: (X, Y, Z).</summary>
	/// <remarks>
	/// <para>The wrist. Joint 0 is <c>WaveBone_1</c>, the common ancestor of all five digit
	/// chains, so rotating it turns the whole hand — which is what a wrist does.</para>
	/// <para><b>Every number here is a calibration, not a derivation.</b> An earlier version of
	/// this comment derived X from the finger bones — <see cref="Movements.Fist"/> against
	/// <see cref="Movements.IndexExtension"/> — and that reasoning was wrong twice over: it
	/// read the pose table without the animation's negation, and a bone's local basis is its
	/// own. Nothing about the fingers constrains which way joint 0 turns.</para>
	/// <para><b>X = +30</b> — <see cref="Movements.WristUpDown"/> gives the magnitude as
	/// <c>±30</c>; the sign says standard <c>+1</c> flexes, matching the digits.</para>
	/// <para><b>Z = +20</b> — <see cref="Movements.WristLeftRight"/> gives <c>±20</c> and names
	/// neither side: "left/right" says which axis, not which is abduction.</para>
	/// <para><b>Y = +179</b> has no rig-side evidence at all. No movement in the library
	/// touches joint 0's Y, so both magnitude and sign are picked: <c>179°</c> each way to stay
	/// clear of the <c>±180°</c> Euler ambiguity, positive so that <c>+1</c> is pronation.
	/// There is no forearm, so what twists is the hand about its own long axis.</para>
	/// <para>All three want confirming against the hand rather than by reading, and X now has
	/// the digits to check against. Flip a sign here and nothing else needs to change.</para>
	/// </remarks>
	public static readonly float[] Wrist = [30f, 179f, 20f];

	/// <summary>Rig degrees produced by standard <c>+1</c>, per joint: (X, Y, Z).</summary>
	/// <remarks>
	/// Keyed by the joint indices both skeletons use — 0 wrist, 1-3 thumb, 4-6 index, 7-9
	/// middle, 10-12 ring, 13-15 pinky. X is flexion, Z abduction; a zero means the channel
	/// does not move that joint on that axis, which is not the same as it moving by nothing
	/// (see <c>JointsMovableOnAxis</c>). Declared after <see cref="Wrist"/> because it uses
	/// it — a static initializer runs in declaration order, and the other way round joint 0
	/// silently becomes null and the wrist stops moving.
	/// </remarks>
	public static readonly Dictionary<int, float[]> AtPlusOne = new()
	{
		[0] = Wrist,

		// Thumb. X flexes, Z abducts. The fist adducts the thumb — Z of -30 and +35 — so
		// abduction, being the direction the name denotes, is the other way.
		[1] = [45, 0, 30],
		[2] = [55, 0, -35],
		[3] = [80, 0, 0],

		[4] = [85, 0, 0],
		[5] = [75, 0, 0],
		[6] = [60, 0, 0],

		[7] = [85, 0, 0],
		[8] = [85, 0, 0],
		[9] = [60, 0, 0],

		[10] = [85, 0, 0],
		[11] = [85, 0, 0],
		[12] = [60, 0, 0],

		[13] = [85, 0, 0],
		[14] = [85, 0, 0],
		[15] = [60, 0, 0],
	};

	/// <summary>Rendered degrees → the standard value that produced them. The inverse of
	/// <see cref="Degrees"/> in range, so a read-back round-trips rather than flipping.
	/// </summary>
	public static float Standard(int joint, int axis, float degrees) =>
		!AtPlusOne.TryGetValue(joint, out float[] at) || axis < 0 || axis >= at.Length
			|| at[axis] == 0f
			? 0f
			: degrees / at[axis];

	/// <summary>A standard value clamped to its domain, for storing in a pose vector.</summary>
	public static float Clamp(float standard) => Math.Clamp(standard, -1f, 1f);

	/// <summary>Clamp a whole pose in place, for as many channels as are present.</summary>
	public static void Clamp(List<float> pose)
	{
		for (int i = 0; i < pose.Count; i++)
			pose[i] = Clamp(pose[i]);
	}
}
