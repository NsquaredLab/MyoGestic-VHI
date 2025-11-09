using Godot;
using System.Text.RegularExpressions;

public partial class MovementUI : Label
{
	private ControlHandSkeleton controlHand;

	public override void _Ready()
	{
		// Find the control hand
		controlHand = GetNode<ControlHandSkeleton>("/root/Main/ControlHand");
	}

	public override void _Process(double delta)
	{
		if (controlHand != null)
		{
			string movementName = controlHand.GetCurrentMovementName();
			string formattedName = FormatMovementName(movementName);
			string state = controlHand.GetAnimationState();
			string formattedState = FormatMovementName(state);
			Text = $"Movement: {formattedName}\nState: {formattedState}\n\nControls:\n← → Select Movement\n↓ Start  ↑ Stop";
		}
	}

	/// <summary>
	/// Converts PascalCase to spaced words (e.g., "TwoFingerPinch" -> "Two Finger Pinch")
	/// </summary>
	private static string FormatMovementName(string pascalCase)
	{
		if (string.IsNullOrEmpty(pascalCase))
			return pascalCase;

		// Insert space before each capital letter (except the first one)
		return MyRegex().Replace(pascalCase, " $1");
	}

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex MyRegex();

}
