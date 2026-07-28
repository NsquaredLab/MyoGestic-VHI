using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Grpc.Core;
using Myogestic.Vhi.V2;

namespace Vhi;

/// <summary>
/// The canonical control service (v2), served alongside <see cref="VhiTrainingAidService"/>.
/// </summary>
/// <remarks>
/// <para>
/// v1 speaks in movement names and in a nine-float pose whose channel meaning lives
/// nowhere. This service speaks the canonical control standard instead: a client
/// declares what it controls by <b>name</b> ("index.flexion"), and VHI answers with
/// what it can render. <see cref="Renderable"/> is the only table in VHI that knows
/// both vocabularies, and it exists so that nothing outside this file has to.
/// </para>
/// <para>
/// <b>Which hand renders what.</b> Continuous DOFs drive the <i>predicted</i> hand;
/// discrete DOFs drive the <i>control</i> hand's movements. That split is what
/// dissolves v1's mutual exclusivity rather than working around it: v1's
/// <c>ControlMode</c> forced a choice because a streamed pose and a movement both
/// drove the <i>same</i> hand. Here they never contend, so an application can hold a
/// continuous grip and a discrete grasp state at once — the thing v1 could not
/// express. A client that wants a discrete DOF rendered still needs the control hand
/// in Movement mode, and gets told so by name when it is not.
/// </para>
/// <para>
/// Threading follows v1 exactly: every RPC body is a closure handed to
/// <see cref="GrpcControlServer.InvokeOnMainThread{T}"/>, so scene mutation happens on
/// Godot's main thread. <see cref="SweepControl"/> is the one exception and the one
/// <c>async</c> method in src/ — it has to span frames, because a pose set inside a
/// single main-thread closure is never rendered before the closure returns.
/// </para>
/// </remarks>
public class VhiCanonicalControlService : VhiCanonicalControl.VhiCanonicalControlBase
{
	/// <summary>Which rotation axis of a joint a canonical DOF drives.</summary>
	private enum Axis { X, Z }

	/// <summary>
	/// Canonical DOF name -> the legacy channel that renders it, the joint whose
	/// rotation reports it back, and the axis it turns.
	/// </summary>
	/// <remarks>
	/// The channel indices are the legacy nine-float layout, kept because the LSL
	/// transport still carries that many floats. Channels 6-8 are absent on purpose:
	/// no consumer in VHI reads them, so no canonical name may claim them. A DOF
	/// missing from this table is reported as not renderable rather than ignored —
	/// an ignored joint looks exactly like a joint that is working and holding still.
	/// </remarks>
	private static readonly Dictionary<string, (int Channel, int Joint, Axis Axis)> Renderable = new()
	{
		["thumb.flexion"] = (0, 1, Axis.X),
		["thumb.abduction"] = (1, 1, Axis.Z),
		["index.flexion"] = (2, 4, Axis.X),
		["middle.flexion"] = (3, 7, Axis.X),
		["ring.flexion"] = (4, 10, Axis.X),
		["little.flexion"] = (5, 13, Axis.X),
	};

	/// <summary>
	/// The continuous channel order VHI reports from <see cref="Declare"/>: index i of
	/// this array is channel i of the LSL stream.
	/// </summary>
	/// <remarks>
	/// Six entries, not nine. The stream is wider than this because the legacy
	/// transport is nine floats, and a client may leave the tail at rest — but VHI
	/// will not name a channel it does not read, because naming a dead channel is how
	/// the four wrong pose tables in MyoGestic came to exist.
	/// </remarks>
	private static readonly string[] ChannelOrder =
	[
		"thumb.flexion",
		"thumb.abduction",
		"index.flexion",
		"middle.flexion",
		"ring.flexion",
		"little.flexion",
	];

	/// <summary>The standard vocabulary version this build implements.</summary>
	private const string StandardVersion = "1";

	/// <summary>
	/// Resolve a canonical discrete state to one of this hand's movement names, or
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

	public VhiCanonicalControlService(
		ControlHandSkeleton controlHand,
		PredictedHandSkeleton predictedHand,
		GrpcControlServer server)
	{
		this.controlHand = controlHand;
		this.predictedHand = predictedHand;
		this.server = server;
	}

	/// <summary>Negotiate a control space: per-DOF verdicts plus the channel layout.</summary>
	public override Task<DeclareReply> Declare(DeclareRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var reply = new DeclareReply
			{
				StandardVersion = StandardVersion,
				ContinuousStreamName = "MyoGestic_Output",
				// The continuous inlet takes canonical values: PredictedHandSkeleton
				// negates once on ingest, so +1 means the direction the DOF name denotes.
				// Announcing it is not decoration — the first end-to-end v2 run inverted
				// every joint precisely because the handshake agreed on names and left
				// units implied, and a client reading ENCODING_UNSPECIFIED is required to
				// fall back rather than guess.
				//
				// VHI's own *outlets* (VHI_Control / VHI_Predict) deliberately stay in the
				// rig's units, so sessions recorded before this switch remain readable by
				// the same decoder. Changing those is a separate decision about recorded
				// data, not part of this one.
				ContinuousEncoding = ContinuousEncoding.Canonical,
				// Layer 3 of three, reported so a client can see it — never so it can
				// mistake it for chatter protection. See SetPresentation.
				BlendsPresentation = predictedHand.EnableSmoothing,
				Accepted = true,
			};
			reply.ContinuousChannelOrder.AddRange(ChannelOrder);

			// A declaration is accepted when everything in it is renderable. "Nothing in
			// it" is not acceptance — except when the client declared a control-pose
			// stream instead of DOFs, which is a legitimate thing to negotiate alone.
			bool all = request.Dofs.Count > 0
				|| request.ControlPoseEncoding != ContinuousEncoding.EncodingUnspecified;
			foreach (DofDeclaration dof in request.Dofs)
			{
				var verdict = new DofVerdict { Name = dof.Name };
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
				else if (Renderable.TryGetValue(dof.Name, out var slot))
				{
					verdict.Renderable = true;
					verdict.RendersAs = $"{predictedHand.BoneNameForJoint(slot.Joint)} {slot.Axis} axis";
				}
				else
				{
					verdict.Renderable = false;
					verdict.Message =
						$"this hand has no {dof.Name} — it renders exactly "
						+ $"[{string.Join(", ", ChannelOrder)}]";
				}
				all &= verdict.Renderable;
				reply.Verdicts.Add(verdict);
			}

			// A declared control-pose stream drives the *control* hand's bones, and so does
			// a discrete DOF (as a movement). Refuse the combination rather than arbitrate
			// it per command: two drivers for one hand is exactly what v1's ControlMode
			// existed to referee, and a client told "no" at handshake time can fix its
			// configuration, where one told "no" per command just sees things not happen.
			if (request.ControlPoseEncoding == ContinuousEncoding.EncodingUnspecified)
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
					bool canonical = request.ControlPoseEncoding == ContinuousEncoding.Canonical;
					controlHand.AcceptControlPoseStream(canonical);
					reply.ControlPoseStreamName = "MyoGestic_ControlPose";
					reply.ControlPoseChannelOrder.AddRange(ChannelOrder);
					// Echo what was actually applied, not what was asked for.
					reply.ControlPoseEncoding = canonical
						? ContinuousEncoding.Canonical
						: ContinuousEncoding.LegacyNegated;
					GD.Print($"  v2 control-pose stream accepted as "
						+ $"{reply.ControlPoseEncoding}; control hand -> Stream mode");
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

	/// <summary>Apply one canonical frame. Continuous only, for now.</summary>
	public override Task<ControlAck> SetControl(SetControlRequest request, ServerCallContext context) =>
		server.InvokeOnMainThread(() =>
		{
			var ack = new ControlAck { Applied = true };
			foreach ((string name, float value) in request.Continuous)
			{
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
				predictedHand.SetCanonicalValue(slot.Channel, Math.Clamp(value, -1f, 1f));
			}
			foreach ((string name, string state) in request.Discrete)
			{
				if (controlHand.TrainingProgramActive)
				{
					// The recording aid owns the control hand while a program runs. Refuse
					// rather than let a control command interrupt the trajectory a
					// recording is being aligned against — and refuse *visibly*, so the
					// caller learns why instead of watching a state quietly not apply.
					ack.Rejected[name] =
						$"a training program is running ('{controlHand.TrainingProgramMovement}') "
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
				// cycle:false — snap to the movement's end pose and hold it. A canonical
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
			await server.InvokeOnMainThread(() => { predictedHand.RestCanonicalPose(); return true; });
			await Task.Delay(hopMs);

			foreach (float canonical in excursions)
			{
				await server.InvokeOnMainThread(() =>
				{
					predictedHand.SetCanonicalValue(slot.Channel, canonical);
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
					float value = slot.Axis == Axis.Z ? degrees.Z : degrees.X;
					if (Math.Abs(value) < 0.5f && Math.Abs(degrees.X) < 0.5f && Math.Abs(degrees.Z) < 0.5f)
						continue;
					if (!observed.TryGetValue(joint, out SweepObservation entry))
					{
						entry = new SweepObservation
						{
							Element = predictedHand.BoneNameForJoint(joint),
						};
						observed[joint] = entry;
					}
					float signed = slot.Axis == Axis.Z ? degrees.Z : degrees.X;
					if (canonical > 0f)
						entry.DegreesAtHi = signed;
					else
						entry.DegreesAtLo = signed;
				}

				await server.InvokeOnMainThread(() => { predictedHand.RestCanonicalPose(); return true; });
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
			slot.Channel, slot.Axis == Axis.Z ? 2 : 0);
		reply.MatchedExpectation =
			observed.Count == expected.Length && Array.TrueForAll(expected, observed.ContainsKey);
		var moved = new List<string>();
		foreach (SweepObservation entry in observed.Values)
			moved.Add(entry.Element);
		GD.Print($"  v2 SweepControl {request.Name}: moved [{string.Join(", ", moved)}] "
			+ $"matched={reply.MatchedExpectation}");
		return reply;
	}
}
