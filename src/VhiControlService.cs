using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Grpc.Core;
using Myogestic.Vhi;

namespace Vhi;

/// <summary>
/// The one gRPC service VHI serves: control-space negotiation and per-frame commands
/// for the predicted and control hands, plus the recording-session coordination a
/// capture pipeline drives them through.
/// </summary>
/// <remarks>
/// <para>
/// A client declares which of VHI's <b>addresses</b> it drives ("vhi.prediction.index"),
/// under whatever names its own configuration uses, and VHI answers with what it can
/// render. <see cref="Renderable"/> is the only table in VHI that knows both
/// vocabularies, and it exists so that nothing outside this file has to.
/// </para>
/// <para>
/// <b>Which hand renders what.</b> Continuous DOFs drive the <i>predicted</i> hand;
/// discrete DOFs drive the <i>control</i> hand's movements. The two never contend, so
/// an application can hold a continuous grip and a discrete grasp state at once. A
/// client that wants a discrete DOF rendered still needs the control hand in Movement
/// mode, and gets told so by name when it is not.
/// </para>
/// <para>
/// <b>Recording-session coordination.</b> <see cref="SetRecordingSession"/>,
/// <see cref="StartRecordingTrajectory"/>, <see cref="StopRecordingTrajectory"/> and
/// <see cref="GetRecordingSessionState"/> are a second, unrelated concern that lives
/// here because both drive the same control hand through the same state machine —
/// splitting them into a second service implied an independence the renderer does not
/// have. Nothing here is a standard DOF and none of it may change what one means: a
/// standard discrete DOF is a held state, while a recording trajectory keeps the
/// control hand cycling so the recorded pose stream sweeps a continuous range for EMG
/// windows to be aligned against. A trajectory names a VHI movement, which is fine
/// precisely because this is not standard — a recording aid is allowed to be
/// application-specific.
/// </para>
/// <para>
/// Threading: every RPC body is a closure handed to
/// <see cref="GrpcControlServer.InvokeOnMainThread{T}"/>, so scene mutation happens on
/// Godot's main thread. <see cref="SweepControl"/> is the one exception and the one
/// <c>async</c> method in src/ — it has to span frames, because a pose set inside a
/// single main-thread closure is never rendered before the closure returns.
/// </para>
/// </remarks>
public class VhiControlService : VhiControl.VhiControlBase
{
	/// <summary>Which rotation axis of a joint a standard DOF drives.</summary>
	private enum Axis { X, Y, Z }

	/// <summary>The component index a rotation axis reads out of a Vector3.</summary>
	private static int ComponentOf(Axis axis) => axis switch
	{
		Axis.X => 0,
		Axis.Y => 1,
		_ => 2,
	};

	/// <summary>One component of a joint's rotation, by axis.</summary>
	private static float Component(Vector3 degrees, Axis axis) => axis switch
	{
		Axis.X => degrees.X,
		Axis.Y => degrees.Y,
		_ => degrees.Z,
	};

	/// <summary>
	/// Standard DOF name -> the legacy channel that renders it, the joint whose
	/// rotation reports it back, and the axis it turns.
	/// </summary>
	/// <remarks>
	/// The channel indices are the legacy nine-float layout, kept because the LSL
	/// transport still carries that many floats. Every channel is claimed here: 0-5
	/// are the five digits and 6-8 are the wrist's three axes. A DOF missing from
	/// this table is reported as not renderable rather than ignored — an ignored
	/// joint looks exactly like a joint that is working and holding still.
	/// </remarks>
	private static readonly Dictionary<string, (int Channel, int Joint, Axis Axis)> Renderable = new()
	{
		// Short forms: what a configuration reads most naturally. For the thumb the short
		// address is DECLARED to mean flexion (its primary axis, legacy channel 0);
		// abduction is addressed explicitly, because silently picking one of two axes is
		// the guesswork a manifest exists to remove.
		["vhi.prediction.thumb"] = (0, 1, Axis.X),
		["vhi.prediction.index"] = (2, 4, Axis.X),
		["vhi.prediction.middle"] = (3, 7, Axis.X),
		["vhi.prediction.ring"] = (4, 10, Axis.X),
		["vhi.prediction.little"] = (5, 13, Axis.X),

		// Explicit axis forms. Same channels — two addresses naming one control is why
		// the channel is published per capability rather than inferred from a list.
		["vhi.prediction.thumb.flexion"] = (0, 1, Axis.X),
		["vhi.prediction.thumb.abduction"] = (1, 1, Axis.Z),
		["vhi.prediction.index.flexion"] = (2, 4, Axis.X),
		["vhi.prediction.middle.flexion"] = (3, 7, Axis.X),
		["vhi.prediction.ring.flexion"] = (4, 10, Axis.X),
		["vhi.prediction.little.flexion"] = (5, 13, Axis.X),

		// The wrist: one joint, three axes, so all three are named for the same reason the
		// thumb's two are.
		["vhi.prediction.wrist.flexion"] = (6, 0, Axis.X),
		["vhi.prediction.wrist.abduction"] = (7, 0, Axis.Z),
		["vhi.prediction.wrist.rotation"] = (8, 0, Axis.Y),
	};

	/// <summary>
	/// Control-hand pose addresses, and the <c>MyoGestic_ControlPose</c> channel each
	/// occupies.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A deliberately separate namespace from <c>vhi.prediction.*</c>, because these are a
	/// separate <b>hand</b> on a separate stream serving a separate purpose. The prediction
	/// controls carry model output; these drive the control hand an operator sets up and
	/// records against. Sharing one namespace would let a configuration route a model's
	/// output into the thing that hand is supposed to be the ground truth *for*.
	/// </para>
	/// <para>
	/// The channels below index <c>MyoGestic_ControlPose</c>, not
	/// <c>MyoGestic_Output</c> — which is why every capability publishes its
	/// <c>stream_name</c> alongside its channel. A client must match both; a channel
	/// number alone is meaningless across two streams.
	/// </para>
	/// </remarks>
	private static readonly Dictionary<string, int> ControlPoseRenderable = new()
	{
		["vhi.control.pose.thumb"] = 0,
		["vhi.control.pose.thumb.flexion"] = 0,
		["vhi.control.pose.thumb.abduction"] = 1,
		["vhi.control.pose.index"] = 2,
		["vhi.control.pose.index.flexion"] = 2,
		["vhi.control.pose.middle"] = 3,
		["vhi.control.pose.middle.flexion"] = 3,
		["vhi.control.pose.ring"] = 4,
		["vhi.control.pose.ring.flexion"] = 4,
		["vhi.control.pose.little"] = 5,
		["vhi.control.pose.little.flexion"] = 5,
		["vhi.control.pose.wrist.flexion"] = 6,
		["vhi.control.pose.wrist.abduction"] = 7,
		["vhi.control.pose.wrist.rotation"] = 8,
	};

	/// <summary>What each address renders, for the manifest's description field.</summary>
	private static readonly Dictionary<string, string> Describes = new()
	{
		["vhi.prediction.thumb"] = "thumb flexion (bones 1-3, X axis) — the primary axis, and what the short address means",
		["vhi.prediction.index"] = "index flexion (bones 4-6)",
		["vhi.prediction.middle"] = "middle flexion (bones 7-9)",
		["vhi.prediction.ring"] = "ring flexion (bones 10-12)",
		["vhi.prediction.little"] = "little flexion (bones 13-15)",
		["vhi.prediction.thumb.flexion"] = "thumb flexion (bones 1-3, X axis)",
		["vhi.prediction.thumb.abduction"] = "thumb abduction (bones 1-3, Z axis). The distal bone's Z gain is 0, so two of the three thumb bones move.",
		["vhi.prediction.index.flexion"] = "index flexion (bones 4-6)",
		["vhi.prediction.middle.flexion"] = "middle flexion (bones 7-9)",
		["vhi.prediction.ring.flexion"] = "ring flexion (bones 10-12)",
		["vhi.prediction.little.flexion"] = "little flexion (bones 13-15)",
		["vhi.prediction.wrist.flexion"] = "wrist flexion (bone 0, X axis). Bone 0 parents every digit, so the whole hand turns with it.",
		["vhi.prediction.wrist.abduction"] = "wrist abduction (bone 0, Z axis). Bone 0 parents every digit, so the whole hand turns with it.",
		["vhi.prediction.wrist.rotation"] = "wrist rotation \u2014 pronation/supination (bone 0, Y axis). The hand twists about its own long axis; there is no forearm to carry the motion.",
	};

	/// <summary>Names that are <b>accepted but not advertised</b>.</summary>
	/// <remarks>
	/// <para>
	/// Every control here is already reachable under a shorter address on the same channel:
	/// <c>vhi.prediction.index.flexion</c> is <c>vhi.prediction.index</c>. Publishing both put
	/// eleven capabilities in the manifest for six controls, which forced every client that
	/// lists them to explain the duplication — MyoGestic's map editor had to print a channel
	/// number in each row so a reader could tell which two rows meant one finger.
	/// </para>
	/// <para>
	/// So the manifest names each control once. <see cref="Renderable"/> still resolves these,
	/// because <c>Declare</c>, <c>SetControl</c> and <c>SweepControl</c> read that table
	/// directly: a client that already sends an axis form keeps working on the wire.
	/// </para>
	/// <para>
	/// <b>Extension is not in here, and is not missing.</b> A continuous control is signed —
	/// <c>+1</c> flexes and <c>-1</c> extends the same control — so there is no separate
	/// extension address to advertise or hide. The <c>ThumbExtension</c> that exists is a
	/// <i>movement preset</i> on <c>vhi.control.gesture</c>, which is a held state rather than
	/// a number. <c>thumb.abduction</c> stays advertised because it is a genuinely different
	/// control: its own channel, its own axis.
	/// </para>
	/// </remarks>
	/// <remarks>
	/// <para>The thumb is the exception in the other direction: <c>thumb</c> alone is the
	/// alias and <c>thumb.flexion</c> is advertised. A digit with one axis needs no suffix —
	/// <c>index</c> cannot mean anything but flexion — but the thumb has two, and a bare
	/// <c>thumb</c> does not say which. The suffix appears exactly where it carries
	/// information.</para>
	/// </remarks>
	private static readonly HashSet<string> Aliases =
	[
		"vhi.prediction.thumb",
		"vhi.prediction.index.flexion",
		"vhi.prediction.middle.flexion",
		"vhi.prediction.ring.flexion",
		"vhi.prediction.little.flexion",
		"vhi.control.pose.thumb",
		"vhi.control.pose.index.flexion",
		"vhi.control.pose.middle.flexion",
		"vhi.control.pose.ring.flexion",
		"vhi.control.pose.little.flexion",
	];

	/// <summary>The pose channel an address occupies on one stream, or <c>-1</c>.</summary>
	/// <remarks>
	/// Exposed for <see cref="LSLCommunicationController"/>, which reads a producer's channel
	/// labels and needs to know where each labelled address belongs in this renderer's own
	/// pose order. That is the same address table <c>Declare</c>, <c>SetControl</c> and
	/// <c>SweepControl</c> resolve against, so a labelled stream and a declaration cannot
	/// disagree about where a control lives — there is one table.
	/// </remarks>
	public static int ChannelForAddress(string address, bool controlPose)
	{
		if (address == null)
			return -1;
		if (controlPose)
			return ControlPoseRenderable.TryGetValue(address, out int channel) ? channel : -1;
		return Renderable.TryGetValue(address, out var slot) ? slot.Channel : -1;
	}

	/// <summary>Advertised addresses in pose-channel order, for one stream.</summary>
	/// <remarks>
	/// <para>Derived from the same tables the manifest is built from, minus
	/// <see cref="Aliases"/>, rather than restated as a literal. It was a literal, and it
	/// drifted: after the aliases were trimmed the reply still named five controls the
	/// manifest no longer advertised, so a client that resolved what <c>Declare</c> told it
	/// would have been refused by its own loader.</para>
	/// <para>Per stream, too. Both orders used to come from the prediction table, so a client
	/// declaring a control-pose stream was handed <c>vhi.prediction.*</c> names for it.</para>
	/// </remarks>
	private static string[] AdvertisedOrder(bool controlPose)
	{
		var pairs = controlPose
			? ControlPoseRenderable.Select(e => (e.Key, Channel: e.Value))
			: Renderable.Select(e => (e.Key, e.Value.Channel));
		return [.. pairs
			.Where(e => e.Channel >= 0 && !Aliases.Contains(e.Key))
			.OrderBy(e => e.Channel)
			.Select(e => e.Key)];
	}

	/// <summary>
	/// The continuous channel order VHI reports from <see cref="Declare"/>: index i is
	/// channel i of the LSL stream, named by its ADDRESS.
	/// </summary>
	/// <remarks>
	/// Addresses rather than bare names, so a client can resolve its own alias to an
	/// address and the address to a channel without inventing a convention.
	/// <para>
	/// Nine entries, not six: every channel either hand exports is rendered, including
	/// 6-8, which carry wrist flexion, abduction and rotation. There is no dead channel
	/// left for a name to claim.
	/// </para>
	/// </remarks>
	private static readonly string[] PredictionOrder = AdvertisedOrder(controlPose: false);
	private static readonly string[] ControlPoseOrder = AdvertisedOrder(controlPose: true);

	/// <summary>The standard vocabulary version this build implements.</summary>
	private const string StandardVersion = "1";

	/// <summary>
	/// Resolve a standard discrete state to one of this hand's movement names, or
	/// <see langword="null"/> when it has none.
	/// </summary>
	/// <remarks>
	/// Matched case-insensitively against <see cref="ControlHandSkeleton.GetAvailableMovements"/>
	/// rather than against a table baked in here: the movement set changes with VHI's
	/// movement mode, and a hard-coded vocabulary would be wrong for half of them. This
	/// is deliberately recomputed per call so the service stays stateless — grpc-dotnet
	/// constructs it per request, and a cached negotiation would silently go stale.
	/// </remarks>
	private string ResolveMovement(string state)
	{
		foreach (string movement in controlHand.GetAvailableMovements())
		{
			if (string.Equals(movement, state, StringComparison.OrdinalIgnoreCase))
				return movement;
		}
		return null;
	}

	private readonly ControlHandSkeleton controlHand;
	private readonly PredictedHandSkeleton predictedHand;
	private readonly GrpcControlServer server;

	public VhiControlService(
		ControlHandSkeleton controlHand,
		PredictedHandSkeleton predictedHand,
		GrpcControlServer server)
	{
		this.controlHand = controlHand;
		this.predictedHand = predictedHand;
		this.server = server;
	}

	/// <summary>The vocabulary version, bumped when addresses or their semantics change.</summary>
	private const string VocabularyVersion = "1";

	/// <summary>
	/// Every control this build exports, with the semantics VHI itself declares.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the target-owned half of the contract. A client maps its own arbitrary
	/// model-output names onto these addresses; it does not invent addresses and it does
	/// not hard-code what they mean. Everything needed to send a value correctly is
	/// declared here, by the side that renders it.
	/// </para>
	/// <para>
	/// One absence is deliberate and worth stating, because it would otherwise be
	/// discovered as a joint that silently does nothing: there is no bare
	/// <c>vhi.prediction.thumb</c> beyond flexion. The thumb has two axes, so the short
	/// address is declared to mean flexion (its primary axis, and legacy channel 0) and
	/// abduction is addressed explicitly. Silently picking one of two axes is exactly the
	/// guesswork the manifest exists to remove.
	/// </para>
	/// <para>
	/// The rig does have a wrist prediction control: bone 0 parents every digit, and
	/// legacy channels 6-8 render its flexion, abduction and rotation — see
	/// <c>vhi.prediction.wrist.flexion</c> and its two siblings above. Wrist *movements*
	/// also exist as gesture presets on <c>vhi.control.gesture</c>, which is a held state
	/// rather than a number and does not replace these.
	/// </para>
	/// </remarks>
	private static List<ControlCapability> BuildCapabilities()
	{
		var caps = new List<ControlCapability>();
		foreach ((string address, (int channel, int _, Axis _)) in Renderable)
		{
			if (Aliases.Contains(address))
			{
				continue;   // accepted, not advertised — see Aliases
			}
			caps.Add(new ControlCapability
			{
				Address = address,
				Kind = Kind.Continuous,
				Lo = -1.0f,
				Hi = 1.0f,
				Rest = 0.0f,
				StreamName = "MyoGestic_Output",
				Channel = channel,
				Description = Describes.TryGetValue(address, out string what) ? what : "",
			});
		}
		foreach ((string address, int channel) in ControlPoseRenderable)
		{
			if (Aliases.Contains(address))
			{
				continue;   // accepted, not advertised — see Aliases
			}
			caps.Add(new ControlCapability
			{
				Address = address,
				Kind = Kind.Continuous,
				Lo = -1.0f,
				Hi = 1.0f,
				Rest = 0.0f,
				StreamName = "MyoGestic_ControlPose",
				Channel = channel,
				Description = "control-hand pose, driven by an operator or a setup script "
					+ "rather than by a model. Requires the control hand in Stream mode, "
					+ "which declaring control_pose requests.",
			});
		}
		return caps;
	}

	/// <summary>Report every control this build exports, and how to send each one.</summary>
	public override Task<ControlManifest> GetControlManifest(
		GetControlManifestRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var manifest = new ControlManifest
			{
				TargetName = "Virtual Hand Interface",
				VocabularyVersion = VocabularyVersion,
			};
			manifest.Capabilities.AddRange(BuildCapabilities());

			// The higher-level preset: one discrete control whose states are whatever
			// movements this build actually offers, discovered rather than hard-coded
			// because the movement set changes with the movement mode. This is where
			// "fist" lives as a gesture — it does not replace the individually
			// addressable prediction controls above.
			var gesture = new ControlCapability
			{
				Address = "vhi.control.gesture",
				Kind = Kind.Discrete,
				// -1, not left at proto3's default of 0: a held state travels over gRPC and
				// occupies no pose channel, and an unset 0 is indistinguishable from
				// channel 0 — which read as "the same control as the thumb" on the client.
				Channel = -1,
				RestState = "Rest",
				Description = "a control-hand movement preset, held until changed. Includes "
					+ "whole-hand gestures (Fist, pinches, Pointing) and the wrist movements "
					+ "this rig can render, which the prediction controls cannot.",
			};
			// This hand animates to a movement over ~a second, so a client thresholding a
			// probability into a state should want more than a coin flip before committing.
			gesture.ActivationThreshold = 0.6f;
			gesture.States.AddRange(controlHand.GetAvailableMovements());
			manifest.Capabilities.Add(gesture);

			GD.Print($"  v2 manifest: {manifest.Capabilities.Count} capabilities, "
				+ $"vocabulary {VocabularyVersion}");
			return manifest;
		});

	/// <summary>Negotiate a control space: per-DOF verdicts plus the channel layout.</summary>
	public override Task<DeclareReply> Declare(DeclareRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var reply = new DeclareReply
			{
				StandardVersion = StandardVersion,
				ContinuousStreamName = "MyoGestic_Output",
				// The continuous inlet takes standard values, and nothing here takes anything
				// else: both inlets clamp and pass them straight to StandardPose.AtPlusOne.
				// There is no legacy-signed mode to negotiate into. The first end-to-end v2
				// run inverted every joint precisely because a handshake agreed on names and
				// left units implied, which is why this stays documented rather than assumed.
				//
				// Both outlets publish standard values too — VHI_Predict and, since the
				// direction fix, VHI_Control. They now agree, which is the whole point: a
				// model trained on the ground-truth stream can be fed back to the predicted
				// hand without its weights being flipped by hand. Recordings made before that
				// are in the rig's old units and stay readable through
				// myogestic.vhi.legacy.decode_pose; the outlets advertise `pose_convention`
				// so the two cannot be confused.
				//
				// Layer 3 of three, reported so a client can see it — never so it can
				// mistake it for chatter protection. See SetPresentation.
				BlendsPresentation = predictedHand.EnableSmoothing,
				Accepted = true,
			};
			reply.ContinuousChannelOrder.AddRange(PredictionOrder);

			// A declaration is accepted when everything in it is renderable. "Nothing in
			// it" is not acceptance — except when the client declared a control-pose
			// stream instead of DOFs, which is a legitimate thing to negotiate alone.
			bool all = request.Dofs.Count > 0 || request.ControlPose;
			foreach (DofDeclaration dof in request.Dofs)
			{
				// The alias is the client's; the address is ours. An empty address means a
				// client written before the manifest existed, which sent the address as
				// the name — honour that rather than breaking it.
				string address = string.IsNullOrEmpty(dof.Address) ? dof.Name : dof.Address;
				var verdict = new DofVerdict { Name = dof.Name, Address = address };
				if (dof.Kind == Kind.Discrete)
				{
					// A discrete DOF renders as a control-hand movement. Every declared
					// state must resolve to one: a DOF where three of four states work
					// is not partially renderable, it is a DOF that silently does
					// nothing a quarter of the time.
					var unresolved = new List<string>();
					var mapping = new List<string>();
					foreach (string state in dof.States)
					{
						string movement = ResolveMovement(state);
						if (movement == null)
							unresolved.Add(state);
						else
							mapping.Add($"{state}->{movement}");
					}
					if (dof.States.Count == 0)
					{
						verdict.Renderable = false;
						verdict.Message = "a discrete DOF must declare at least one state";
					}
					else if (unresolved.Count > 0)
					{
						verdict.Renderable = false;
						verdict.Message =
							$"no movement matches [{string.Join(", ", unresolved)}] — this "
							+ $"hand offers [{string.Join(", ", controlHand.GetAvailableMovements())}]";
					}
					else
					{
						verdict.Renderable = true;
						verdict.RendersAs = $"control-hand movements: {string.Join(", ", mapping)}";
					}
				}
				else if (Renderable.TryGetValue(address, out var slot))
				{
					verdict.Renderable = true;
					verdict.RendersAs =
						$"predicted hand: {predictedHand.BoneNameForJoint(slot.Joint)} "
						+ $"{slot.Axis} axis (MyoGestic_Output channel {slot.Channel})";
				}
				else if (ControlPoseRenderable.TryGetValue(address, out int poseChannel))
				{
					// The control hand's own namespace. Declarable so a client can route to
					// it, but note what it needs that the prediction stream does not: the
					// hand has to be in Stream mode, which declaring control_pose requests.
					// Without that the inlet is read by nobody, and an inlet nobody reads is
					// indistinguishable from a stream that is not arriving.
					verdict.Renderable = true;
					verdict.RendersAs =
						$"control hand: MyoGestic_ControlPose channel {poseChannel}"
						+ (!request.ControlPose
							? " — declare control_pose, or nothing will read it"
							: "");
				}
				else
				{
					verdict.Renderable = false;
					verdict.Message =
						$"this target does not export '{address}' — call GetControlManifest "
						+ $"for the full list. It renders: [{string.Join(", ", PredictionOrder)}]";
				}
				all &= verdict.Renderable;
				reply.Verdicts.Add(verdict);
			}

			// A declared control-pose stream drives the *control* hand's bones, and so does
			// a discrete DOF (as a movement). Refuse the combination rather than arbitrate
			// it per command: two drivers for one hand is exactly what v1's ControlMode
			// existed to referee, and a client told "no" at handshake time can fix its
			// configuration, where one told "no" per command just sees things not happen.
			if (!request.ControlPose)
			{
				// Not declaring the stream releases it. Symmetry matters here: declaring a
				// control pose is what puts this hand into Stream mode, so re-declaring
				// without one is how a client gets back to commanding discrete DOFs. Without
				// it that switch would be a one-way door for the life of the process.
				controlHand.ReleaseControlPoseStream();
			}
			else
			{
				bool anyDiscrete = false;
				foreach (DofDeclaration dof in request.Dofs)
					anyDiscrete |= dof.Kind == Kind.Discrete;

				if (anyDiscrete)
				{
					reply.Accepted = false;
					foreach (DofVerdict verdict in reply.Verdicts)
					{
						if (verdict.Renderable && FindDeclaration(request, verdict.Name) == Kind.Discrete)
						{
							verdict.Renderable = false;
							verdict.Message =
								"a discrete DOF and a control-pose stream would both drive the "
								+ "control hand — declare one or the other";
						}
					}
				}
				else
				{
					controlHand.AcceptControlPoseStream();
					reply.ControlPoseStreamName = "MyoGestic_ControlPose";
					reply.ControlPoseChannelOrder.AddRange(ControlPoseOrder);
					// Echo what was actually granted, not just what was asked for.
					reply.ControlPose = true;
					GD.Print("  v2 control-pose stream accepted; control hand -> Stream mode");
				}
			}

			reply.Accepted = all && reply.Accepted;
			GD.Print($"  v2 Declare from {request.ClientName}: accepted={reply.Accepted} "
				+ $"({request.Dofs.Count} DOFs, standard {request.StandardVersion})");
			return reply;
		});

	/// <summary>The declared kind of one DOF in a request, for cross-checking.</summary>
	private static Kind FindDeclaration(DeclareRequest request, string name)
	{
		foreach (DofDeclaration dof in request.Dofs)
		{
			if (dof.Name == name)
				return dof.Kind;
		}
		return Kind.Unspecified;
	}

	/// <summary>Apply one standard frame. Continuous only, for now.</summary>
	public override Task<ControlAck> SetControl(SetControlRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var ack = new ControlAck { Applied = true };
			foreach ((string name, float value) in request.Continuous)
			{
				// Keyed by address: SetControl carries target addresses, exactly as the
				// manifest publishes them.
				if (!Renderable.TryGetValue(name, out var slot))
				{
					ack.Rejected[name] = "not renderable — see Declare";
					ack.Applied = false;
					continue;
				}
				if (float.IsNaN(value) || float.IsInfinity(value))
				{
					// A non-finite value would become a full-scale deflection once
					// multiplied by the joint gain.
					ack.Rejected[name] = "not finite";
					ack.Applied = false;
					continue;
				}
				predictedHand.SetStandardValue(slot.Channel, Math.Clamp(value, -1f, 1f));
			}
			foreach ((string name, string state) in request.Discrete)
			{
				if (controlHand.RecordingTrajectoryActive)
				{
					// The recording aid owns the control hand while a trajectory runs.
					// Refuse rather than let a control command interrupt the trajectory a
					// recording is being aligned against — and refuse *visibly*, so the
					// caller learns why instead of watching a state quietly not apply.
					ack.Rejected[name] =
						$"a recording trajectory is running ('{controlHand.RecordingTrajectoryMovement}') "
						+ "— stop it before commanding discrete DOFs";
					ack.Applied = false;
					continue;
				}
				string movement = ResolveMovement(state);
				if (movement == null)
				{
					ack.Rejected[name] = $"no movement matches state '{state}' — see Declare";
					ack.Applied = false;
					continue;
				}
				// cycle:false — snap to the movement's end pose and hold it. A standard
				// discrete DOF is a *held state*, so looping an open/close animation
				// would render something the client never asked for.
				if (!controlHand.SetMovement(movement, false))
				{
					ack.Rejected[name] =
						$"'{movement}' was refused — the control hand is in "
						+ $"{controlHand.DriverMode} mode, not Movement";
					ack.Applied = false;
				}
			}
			return ack;
		});

	/// <summary>Configure how the renderer blends between commanded values.</summary>
	/// <remarks>
	/// <para>
	/// Appearance only. This is the third of three separate layers and the one most
	/// easily misused:
	/// </para>
	/// <list type="number">
	/// <item><description>Continuous smoothing, on the MyoGestic side inside its
	/// ControlBus, before any target sees a frame — that layer decides what value is
	/// actually commanded.</description></item>
	/// <item><description>Discrete debounce and hysteresis, also MyoGestic-side and
	/// declared on the DOF, which gates a noisy classifier before its state becomes a
	/// transition. A discrete control is never numerically filtered like an axis: that
	/// would interpolate through states nobody selected.</description></item>
	/// <item><description>This — purely visual interpolation, so the hand does not
	/// snap jarringly between poses.</description></item>
	/// </list>
	/// <para>
	/// It changes only how a commanded value <i>looks</i> on the way to being reached.
	/// It cannot make an unstable prediction stable, and a build with blending on but
	/// no debounce still jumps between states — just smoothly.
	/// </para>
	/// </remarks>
	public override Task<ControlAck> SetPresentation(
		SetPresentationRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			predictedHand.SetSmoothing(request.Blend, request.BlendSpeed);
			GD.Print($"  v2 presentation: blend={request.Blend} speed={request.BlendSpeed}");
			return new ControlAck { Applied = true };
		});

	/// <summary>
	/// Drive one DOF across its range and report which bone moved, and how far, in
	/// signed degrees read back off the skeleton.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>async</c> on purpose, and the only such method in src/. A pose set inside a
	/// single <see cref="GrpcControlServer.InvokeOnMainThread{T}"/> closure is never
	/// rendered before that closure returns, so a sweep that looped inside one would
	/// block the main thread and read every value back from the same frozen frame.
	/// Each excursion therefore gets its own main-thread hop, with the wait happening
	/// on the gRPC thread in between.
	/// </para>
	/// <para>
	/// What this does and does not prove: the read-back comes from
	/// <c>Skeleton3D.GetBonePoseRotation</c>, so it round-trips through Godot's
	/// quaternion and will catch a wrong axis, a gimbal problem, or a gain of the
	/// wrong sign. It does <b>not</b> independently verify the rig — which is why
	/// <see cref="SweepObservation.Element"/> reports the model's own bone name rather
	/// than a hand-written label. If the FBX is ever re-rigged or its bones renamed,
	/// that name changes and the caller can see it.
	/// </para>
	/// <para>
	/// A client must not be streaming poses over LSL during a sweep: the stream and
	/// the sweep would fight for the same channels, and the sweep would report
	/// whichever arrived last.
	/// </para>
	/// </remarks>
	public override async Task<SweepControlReply> SweepControl(
		SweepControlRequest request, ServerCallContext context)
	{
		if (!Renderable.TryGetValue(request.Name, out var slot))
		{
			return new SweepControlReply
			{
				Completed = false,
				Message = $"{request.Name} is not renderable — see Declare",
			};
		}

		float duration = Mathf.Clamp(request.DurationS <= 0f ? 2f : request.DurationS, 0.2f, 30f);
		float[] excursions = request.BothDirections ? [1f, -1f] : [1f];
		// Split the budget across (set, settle, read) per excursion plus a return to
		// rest after each, so the caller's duration is honoured rather than ignored.
		int hopMs = (int)(duration * 1000f / (excursions.Length * 2));

		var reply = new SweepControlReply { Completed = true };
		var observed = new Dictionary<int, SweepObservation>();
		try
		{
			await server.InvokeOnMainThread(() => { predictedHand.RestStandardPose(); return true; });
			await Task.Delay(hopMs);

			foreach (float standard in excursions)
			{
				await server.InvokeOnMainThread(() =>
				{
					predictedHand.SetStandardValue(slot.Channel, standard);
					return true;
				});
				await Task.Delay(hopMs);

				// Read every joint this hand animates, not just the expected one: a
				// sweep that moves something extra is a mapping or rigging error, and
				// only a full scan can see it.
				Dictionary<int, Vector3> pose = await server.InvokeOnMainThread(
					() => predictedHand.AnimatedJointDegrees());
				foreach ((int joint, Vector3 degrees) in pose)
				{
					float value = Component(degrees, slot.Axis);
					if (Math.Abs(value) < 0.5f && Math.Abs(degrees.X) < 0.5f
						&& Math.Abs(degrees.Y) < 0.5f && Math.Abs(degrees.Z) < 0.5f)
						continue;
					if (!observed.TryGetValue(joint, out SweepObservation entry))
					{
						entry = new SweepObservation
						{
							Element = predictedHand.BoneNameForJoint(joint),
						};
						observed[joint] = entry;
					}
					float signed = Component(degrees, slot.Axis);
					if (standard > 0f)
						entry.DegreesAtHi = signed;
					else
						entry.DegreesAtLo = signed;
				}

				await server.InvokeOnMainThread(() => { predictedHand.RestStandardPose(); return true; });
				await Task.Delay(hopMs);
			}
		}
		catch (Exception e)
		{
			return new SweepControlReply { Completed = false, Message = e.Message };
		}

		reply.Observed.AddRange(observed.Values);
		// Expectation is the joints this DOF can move on *its own axis*, not every joint
		// sharing its channel: thumb abduction drives all three thumb bones through
		// channel 1, but the distal one's Z gain is 0, so a channel-wide expectation
		// would call a correct sweep a mismatch.
		int[] expected = predictedHand.JointsMovableOnAxis(
			slot.Channel, ComponentOf(slot.Axis));
		reply.MatchedExpectation =
			observed.Count == expected.Length && Array.TrueForAll(expected, observed.ContainsKey);
		var moved = new List<string>();
		foreach (SweepObservation entry in observed.Values)
			moved.Add(entry.Element);
		GD.Print($"  v2 SweepControl {request.Name}: moved [{string.Join(", ", moved)}] "
			+ $"matched={reply.MatchedExpectation}");
		return reply;
	}

	/// <summary>Mark a recording session active or finished.</summary>
	public override Task<RecordingAck> SetRecordingSession(
		SetRecordingSessionRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			controlHand.SessionActive = request.Active;
			GD.Print($"  v2 recording session active={request.Active}");
			return new RecordingAck { Applied = true };
		});

	/// <summary>Start cycling the control hand to generate a recording trajectory.</summary>
	public override Task<RecordingAck> StartRecordingTrajectory(
		StartRecordingTrajectoryRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			if (controlHand.RecordingTrajectoryActive)
			{
				// Refuse rather than silently switch: a recording in progress is being
				// aligned against this trajectory, and changing it underneath would
				// corrupt the labels for everything already captured.
				return new RecordingAck
				{
					Applied = false,
					Message =
						$"a trajectory is already running ('{controlHand.RecordingTrajectoryMovement}') "
						+ "— stop it before starting another",
				};
			}
			if (!controlHand.StartRecordingTrajectory(
					request.Movement, request.FrequencyHz, request.HoldTimeS, request.RestTimeS))
			{
				return new RecordingAck
				{
					Applied = false,
					Message =
						$"could not start '{request.Movement}' — the control hand is in "
						+ $"{controlHand.DriverMode} mode, or that movement does not exist "
						+ $"(offers [{string.Join(", ", controlHand.GetAvailableMovements())}])",
				};
			}
			return new RecordingAck { Applied = true };
		});

	/// <summary>Stop the trajectory. Rests the hand only if one was running.</summary>
	public override Task<RecordingAck> StopRecordingTrajectory(
		StopRecordingTrajectoryRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			// Idempotent by design: teardown paths call this without knowing whether a
			// trajectory is running, and a failure there would mask the real exit.
			controlHand.StopRecordingTrajectory();
			return new RecordingAck { Applied = true };
		});

	/// <summary>Report the recording session's state and the movements a trajectory may name.</summary>
	public override Task<RecordingSessionState> GetRecordingSessionState(
		GetRecordingSessionStateRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var state = new RecordingSessionState
			{
				RecordingSessionActive = controlHand.SessionActive,
				TrajectoryRunning = controlHand.RecordingTrajectoryActive,
				TrajectoryMovement = controlHand.RecordingTrajectoryMovement,
				AnimationState = controlHand.GetAnimationState(),
				CurrentMovement = controlHand.GetCurrentMovementName(),
			};
			state.AvailableMovements.AddRange(controlHand.GetAvailableMovements());
			return state;
		});
}
