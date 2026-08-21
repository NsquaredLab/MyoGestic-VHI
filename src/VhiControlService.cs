using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Grpc.Core;
using Myogestic.Remote;

namespace Vhi;

/// <summary>
/// The one gRPC service VHI serves: a manifest of every control it exports, per-frame
/// commands for the predicted and control hands, plus the recording-session
/// coordination a capture pipeline drives them through.
/// </summary>
/// <remarks>
/// <para>
/// A client calls <see cref="GetControlManifest"/> once, unconditionally, and gets back
/// every <b>address</b> VHI exports ("vhi.prediction.index") with what it can drive —
/// no per-client negotiation, no declared subset. <see cref="Renderable"/> is the only
/// table in VHI that knows both the manifest's addresses and the rig they resolve to,
/// and it exists so that nothing outside this file has to.
/// </para>
/// <para>
/// <b>Which hand renders what.</b> Continuous DOFs drive the <i>predicted</i> hand;
/// discrete DOFs drive the <i>control</i> hand's movements. The two never contend, so
/// an application can hold a continuous grip and a discrete grasp state at once. A
/// discrete DOF is refused by name when a control-pose stream is driving the control
/// hand instead — see <see cref="SetControl"/>.
/// </para>
/// <para>
/// <b>Recording-session coordination.</b> <see cref="SetRecordingSession"/>,
/// <see cref="StartRecordingTrajectory"/>, <see cref="StopRecordingTrajectory"/> and
/// <see cref="GetRecordingSessionState"/> are a second, unrelated concern that lives
/// here because both drive the same control hand through the same state machine —
/// splitting them into a second service implied an independence the target does not
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
public class VhiControlService : RemoteControl.RemoteControlBase
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
	/// Standard DOF name -> the pose channel that renders it, the joint whose
	/// rotation reports it back, and the axis it turns.
	/// </summary>
	/// <remarks>
	/// The channel is where the value lands in the hand's nine-slot pose, and it is
	/// <i>internal</i>: each of these DOFs arrives on a stream of its own, one channel
	/// wide, so a producer only ever writes channel 0. Every slot is claimed here: 0-5
	/// are the five digits and 6-8 are the wrist's three axes. A DOF missing from
	/// this table is reported as not renderable rather than ignored — an ignored
	/// joint looks exactly like a joint that is working and holding still.
	/// </remarks>
	private static readonly Dictionary<string, (int Channel, int Joint, Axis Axis)> Renderable = new()
	{
		// One spelling per control, and it is the spelling the manifest advertises: this
		// table and <see cref="GetControlManifest"/> are the same vocabulary, so a name
		// that resolves here is a name a client can discover.
		//
		// The suffix appears exactly where it carries information: these four digits bend
		// one way, so `index` cannot mean anything else, while the thumb and the wrist
		// name their axes.
		["vhi.prediction.index"] = (2, 4, Axis.X),
		["vhi.prediction.middle"] = (3, 7, Axis.X),
		["vhi.prediction.ring"] = (4, 10, Axis.X),
		["vhi.prediction.little"] = (5, 13, Axis.X),
		["vhi.prediction.thumb.flexion"] = (0, 1, Axis.X),
		["vhi.prediction.thumb.abduction"] = (1, 1, Axis.Z),

		// The wrist: one joint, three axes, so all three are named for the same reason the
		// thumb's two are.
		["vhi.prediction.wrist.flexion"] = (6, 0, Axis.X),
		["vhi.prediction.wrist.abduction"] = (7, 0, Axis.Z),
		["vhi.prediction.wrist.rotation"] = (8, 0, Axis.Y),
	};

	/// <summary>
	/// Control-hand pose addresses, and the pose channel each occupies.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Same one-name-per-control shape as <see cref="Renderable"/>: the bare digit names
	/// for the four single-axis digits, and an explicit axis on the thumb and the wrist.
	/// </para>
	/// <para>
	/// A deliberately separate namespace from <c>vhi.prediction.*</c>, because these are a
	/// separate <b>hand</b> on a separate stream serving a separate purpose. The prediction
	/// controls carry model output; these drive the control hand an operator sets up and
	/// records against. Sharing one namespace would let a configuration route a model's
	/// output into the thing that hand is supposed to be the ground truth *for*.
	/// </para>
	/// <para>
	/// The channels below are internal, exactly as <see cref="Renderable"/>'s are: they say
	/// where a value lands in the control hand's nine-slot pose, not what a client indexes.
	/// A client writes channel 0 of the stream named for the address.
	/// </para>
	/// </remarks>
	private static readonly Dictionary<string, int> ControlPoseRenderable = new()
	{
		["vhi.control.pose.thumb.flexion"] = 0,
		["vhi.control.pose.thumb.abduction"] = 1,
		["vhi.control.pose.index"] = 2,
		["vhi.control.pose.middle"] = 3,
		["vhi.control.pose.ring"] = 4,
		["vhi.control.pose.little"] = 5,
		["vhi.control.pose.wrist.flexion"] = 6,
		["vhi.control.pose.wrist.abduction"] = 7,
		["vhi.control.pose.wrist.rotation"] = 8,
	};

	/// <summary>
	/// Every pose stream this build subscribes to: the stream's name, which hand it drives,
	/// and the pose channel its one value lands on.
	/// </summary>
	/// <remarks>
	/// The name <i>is</i> the address — one stream per DOF, one channel wide — which is why
	/// the two tables above are the only place either fact is written down.
	/// <see cref="LSLCommunicationController"/> reads this to know what to resolve, so a
	/// control added to those tables gets an inlet and a manifest entry together and cannot
	/// get one without the other.
	/// </remarks>
	public static IEnumerable<(string Name, bool Control, int Channel)> PoseStreams() =>
		Renderable
			.Select(entry => (entry.Key, false, entry.Value.Channel))
			.Concat(ControlPoseRenderable.Select(entry => (entry.Key, true, entry.Value)));

	/// <summary>What each address renders, for the manifest's description field.</summary>
	private static readonly Dictionary<string, string> Describes = new()
	{
		["vhi.prediction.index"] = "index flexion (bones 4-6)",
		["vhi.prediction.middle"] = "middle flexion (bones 7-9)",
		["vhi.prediction.ring"] = "ring flexion (bones 10-12)",
		["vhi.prediction.little"] = "little flexion (bones 13-15)",
		["vhi.prediction.thumb.flexion"] = "thumb flexion (bones 1-3, X axis)",
		["vhi.prediction.thumb.abduction"] = "thumb abduction (bones 1-3, Z axis). The distal bone's Z gain is 0, so two of the three thumb bones move.",
		["vhi.prediction.wrist.flexion"] = "wrist flexion (bone 0, X axis). Bone 0 parents every digit, so the whole hand turns with it.",
		["vhi.prediction.wrist.abduction"] = "wrist abduction (bone 0, Z axis). Bone 0 parents every digit, so the whole hand turns with it.",
		["vhi.prediction.wrist.rotation"] = "wrist rotation \u2014 pronation/supination (bone 0, Y axis). The hand twists about its own long axis; there is no forearm to carry the motion.",
	};

	/// <summary>Why an address does not resolve, naming a near spelling when one exists.</summary>
	/// <remarks>
	/// <para>
	/// VHI used to accept a second spelling of five controls — <c>vhi.prediction.index.flexion</c>
	/// for <c>vhi.prediction.index</c>, a bare <c>vhi.prediction.thumb</c> for its two axes —
	/// without advertising any of them. Two vocabularies for one set of controls is one more
	/// than a target can keep in step, so there is now exactly one: what the manifest says.
	/// </para>
	/// <para>
	/// Which makes the refusal the migration path, and it has to carry the new spelling. The
	/// candidates are derived from <see cref="Renderable"/> — a retired name differs from a
	/// live one by one trailing segment, in either direction — rather than kept in a table of
	/// old names, which would be the second vocabulary again under another name.
	/// </para>
	/// </remarks>
	private static string NotRenderable(string name)
	{
		List<string> near = Renderable.Keys
			.Where(address => address.StartsWith(name + ".", StringComparison.Ordinal)
				|| name.StartsWith(address + ".", StringComparison.Ordinal))
			.OrderBy(address => address, StringComparer.Ordinal)
			.ToList();
		// Both, when both are near: a bare `thumb` had to pick one of two axes, and saying
		// which two exist is the answer that stops the client from having to guess again.
		return near.Count > 0
			? $"not renderable — did you mean {string.Join(" or ", near)}? See GetControlManifest"
			: "not renderable — see GetControlManifest";
	}

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

	/// <summary>The vocabulary this build serves. A decimal integer, compared numerically.</summary>
	/// <remarks>
	/// <para>Bumped when the addresses or the transport change in a way a client cannot absorb
	/// silently. A client declares the oldest vocabulary it can drive and refuses anything
	/// below it, by name, when it binds — which is the only thing that makes a version-skewed
	/// pair of these two <i>separately installed</i> applications say so out loud. Before that
	/// gate existed the mismatch was silent in the worst way: an old target listening for a
	/// whole-pose stream a new client no longer publishes logs nothing at all, and the hand
	/// simply never moves.</para>
	/// <para><c>1</c> — a manifest carrying <c>stream_name</c> and <c>channel</c>, several
	/// controls able to share one wider stream. Retired.<br/>
	/// <c>2</c> — one stream per DOF, named for the address, one channel wide.</para>
	/// </remarks>
	private const string VocabularyVersion = "2";

	/// <summary>This build's own release version — the numeric part of its release tag.</summary>
	/// <remarks>
	/// Reported in the manifest as <c>target_version</c>, so a client that knows this
	/// target's release history can refuse a build whose protocol is current but whose
	/// behaviour is known-bad — the case <see cref="VocabularyVersion"/> cannot see
	/// (v1.0.0 served vocabulary 2 correctly and still dropped gesture edges under
	/// load). Bump with the release, alongside the tag.
	/// </remarks>
	private const string BuildVersion = "2.1.0";

	/// <summary>The one discrete control this build exports, by address.</summary>
	/// <remarks>
	/// Named once and used by both halves of the contract — the manifest that advertises it
	/// and the <see cref="SetControl"/> that accepts commands for it — so the two cannot
	/// drift apart. Its <i>states</i> are discovered per call from the movement mode; only
	/// the address is fixed.
	/// </remarks>
	private const string GestureAddress = "vhi.control.gesture";

	/// <summary>
	/// Every control this build exports, with the semantics VHI itself declares.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the target-owned half of the contract. A client maps its own arbitrary
	/// model-output names onto these addresses; it does not invent addresses and it does
	/// not hard-code what they mean. Everything needed to send a value correctly is
	/// declared here, by the side that carries it out.
	/// </para>
	/// <para>
	/// One absence is deliberate and worth stating, because it would otherwise be
	/// discovered as a joint that silently does nothing: there is no bare
	/// <c>vhi.prediction.thumb</c>. The thumb has two axes, so both are addressed
	/// explicitly and neither answers to the short name. Silently picking one of two axes
	/// is exactly the guesswork the manifest exists to remove — and a name the manifest
	/// does not carry is refused, with the axis spellings named in the refusal.
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
		foreach ((string address, (int _, int _, Axis _)) in Renderable)
		{
			caps.Add(new ControlCapability
			{
				Address = address,
				Kind = Kind.Continuous,
				Lo = -1.0f,
				Hi = 1.0f,
				Rest = 0.0f,
				// No stream name and no channel: a DOF is its own stream, named for this
				// very address and one channel wide, so both fields only ever repeated what
				// `Address` already says. The DOFs this target exports are independently
				// actuated, may come from different producers and may update at different
				// rates, so nothing links them and there is no frame for a client to fill in.
				Description = Describes.TryGetValue(address, out string what) ? what : "",
			});
		}
		foreach (string address in ControlPoseRenderable.Keys)
		{
			caps.Add(new ControlCapability
			{
				Address = address,
				Kind = Kind.Continuous,
				Lo = -1.0f,
				Hi = 1.0f,
				Rest = 0.0f,
				Description = "control-hand pose, driven by an operator or a setup script "
					+ "rather than by a model. Nothing to request: the control hand follows "
					+ "these streams while any of them is delivering, and gives itself back "
					+ "to its own movements once the last one goes stale.",
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
				TargetVersion = BuildVersion,
			};
			manifest.Capabilities.AddRange(BuildCapabilities());

			// The higher-level preset: one discrete control whose states are whatever
			// movements this build actually offers, discovered rather than hard-coded
			// because the movement set changes with the movement mode. This is where
			// "fist" lives as a gesture — it does not replace the individually
			// addressable prediction controls above.
			var gesture = new ControlCapability
			{
				Address = GestureAddress,
				Kind = Kind.Discrete,
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

	/// <summary>Apply one standard frame. Both maps are keyed by address.</summary>
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
					ack.Rejected[name] = NotRenderable(name);
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
			foreach ((string address, string state) in request.Discrete)
			{
				// Keyed by address, exactly like Continuous above: the key says *which*
				// control this is for, the value says which of its states. Resolving on the
				// state alone would leave two discrete controls that share a state name
				// indistinguishable — and would accept a key naming nothing at all.
				if (address != GestureAddress)
				{
					ack.Rejected[address] =
						$"'{address}' is not a discrete control this build exports — the only "
						+ $"one is '{GestureAddress}'. See GetControlManifest.";
					ack.Applied = false;
					continue;
				}
				if (controlHand.RecordingTrajectoryActive)
				{
					// The recording aid owns the control hand while a trajectory runs.
					// Refuse rather than let a control command interrupt the trajectory a
					// recording is being aligned against — and refuse *visibly*, so the
					// caller learns why instead of watching a state quietly not apply.
					ack.Rejected[address] =
						$"a recording trajectory is running ('{controlHand.RecordingTrajectoryMovement}') "
						+ "— stop it before commanding discrete DOFs";
					ack.Applied = false;
					continue;
				}
				string movement = ResolveMovement(state);
				if (movement == null)
				{
					ack.Rejected[address] = $"no movement matches state '{state}' — see GetControlManifest";
					ack.Applied = false;
					continue;
				}
				// cycle:false — snap to the movement's end pose and hold it. A standard
				// discrete DOF is a *held state*, so looping an open/close animation
				// would render something the client never asked for.
				if (!controlHand.SetMovement(movement, false))
				{
					ack.Rejected[address] =
						$"'{movement}' was refused — a control-pose stream is driving the "
						+ "control hand";
					ack.Applied = false;
				}
			}
			return ack;
		});

	/// <summary>Configure how the target blends between commanded values.</summary>
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
				Message = $"{request.Name} is {NotRenderable(request.Name)}",
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
							Element = HandSkeleton.BoneNameForJoint(joint),
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
						$"could not start '{request.Movement}' — a control-pose stream is "
						+ "driving the control hand, or that movement does not exist "
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
