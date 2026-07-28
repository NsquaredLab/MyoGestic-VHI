using System.Threading.Tasks;
using Godot;
using Grpc.Core;
using Myogestic.Vhi.V2;

namespace Vhi;

/// <summary>
/// The v2 recording / label-acquisition aid, served alongside
/// <see cref="VhiCanonicalControlService"/> and <see cref="VhiControlService"/>.
/// </summary>
/// <remarks>
/// <para>
/// A separate service on purpose. Nothing here is a canonical DOF, and none of it may
/// change what one means. A canonical discrete DOF is a <i>held state</i>: an
/// application asks for a grip and the hand holds a grip. Collecting regression
/// training data wants the opposite — a control hand that keeps moving, so the
/// recorded <c>VHI_Control</c> stream sweeps a continuous kinematic range for EMG
/// windows to be aligned against. Folding that into the discrete vocabulary would
/// have made "grip" mean "grip, unless someone is recording", which is how a control
/// standard rots.
/// </para>
/// <para>
/// Both RPC groups here are properties of a recording <i>session</i> rather than of
/// the thing being controlled: the session gate keeps VHI's local keyboard from
/// competing with MyoGestic as a movement source, and the training program cycles the
/// control hand.
/// </para>
/// <para>
/// A program names a VHI movement, which is fine precisely <i>because</i> this is not
/// canonical: a recording aid is allowed to be application-specific, so the canonical
/// vocabulary never has to grow a concept no application controls.
/// </para>
/// </remarks>
public class VhiTrainingAidService : VhiTrainingAid.VhiTrainingAidBase
{
	private readonly ControlHandSkeleton controlHand;
	private readonly GrpcControlServer server;

	public VhiTrainingAidService(ControlHandSkeleton controlHand, GrpcControlServer server)
	{
		this.controlHand = controlHand;
		this.server = server;
	}

	/// <summary>Mark a recording session active or finished.</summary>
	public override Task<TrainingAck> SetRecordingSession(
		SetRecordingSessionRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			controlHand.SessionActive = request.Active;
			GD.Print($"  v2 recording session active={request.Active}");
			return new TrainingAck { Applied = true };
		});

	/// <summary>Start cycling the control hand to generate a training trajectory.</summary>
	public override Task<TrainingAck> StartTrainingProgram(
		StartTrainingProgramRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			if (controlHand.TrainingProgramActive)
			{
				// Refuse rather than silently switch: a recording in progress is being
				// aligned against this trajectory, and changing it underneath would
				// corrupt the labels for everything already captured.
				return new TrainingAck
				{
					Applied = false,
					Message =
						$"a training program is already running ('{controlHand.TrainingProgramMovement}') "
						+ "— stop it before starting another",
				};
			}
			if (!controlHand.StartTrainingProgram(
					request.Movement, request.FrequencyHz, request.HoldTimeS, request.RestTimeS))
			{
				return new TrainingAck
				{
					Applied = false,
					Message =
						$"could not start '{request.Movement}' — the control hand is in "
						+ $"{controlHand.DriverMode} mode, or that movement does not exist "
						+ $"(offers [{string.Join(", ", controlHand.GetAvailableMovements())}])",
				};
			}
			return new TrainingAck { Applied = true };
		});

	/// <summary>Stop the program and rest the hand. Succeeds even if none was running.</summary>
	public override Task<TrainingAck> StopTrainingProgram(
		StopTrainingProgramRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			// Idempotent by design: teardown paths call this without knowing whether a
			// program is running, and a failure there would mask the real exit.
			controlHand.StopTrainingProgram();
			return new TrainingAck { Applied = true };
		});

	/// <summary>Report the aid's state and the movements a program may name.</summary>
	public override Task<TrainingState> GetTrainingState(
		GetTrainingStateRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var state = new TrainingState
			{
				RecordingSessionActive = controlHand.SessionActive,
				ProgramRunning = controlHand.TrainingProgramActive,
				ProgramMovement = controlHand.TrainingProgramMovement,
				AnimationState = controlHand.GetAnimationState(),
			};
			state.AvailableMovements.AddRange(controlHand.GetAvailableMovements());
			return state;
		});
}
