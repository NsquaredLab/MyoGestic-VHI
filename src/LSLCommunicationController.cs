using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace Vhi;

// NOTE: NOT using SharpLSL directly to avoid static initialization hang
// Instead using LSLWrapper which loads LSL via reflection

/// <summary>
/// All Lab Streaming Layer I/O for VHI - the only node that touches LSL directly
/// (and even then only through <see cref="LSLWrapper"/>, never SharpLSL).
///
/// <para><b>One stream per DOF, in.</b> Every control VHI exports is its own LSL stream,
/// named by its own address and one channel wide: <c>vhi.prediction.index</c>,
/// <c>vhi.control.pose.thumb.flexion</c>, and their siblings — the same names
/// <c>GetControlManifest</c> publishes. A sample is applied to the hand the moment it
/// arrives, and the DOFs that did not deliver hold what they were last commanded to.
/// There is no whole-pose frame and nothing waits for one: the DOFs are independently
/// actuated, may come from different producers and may update at different rates, so a
/// hand whose index has moved and whose thumb has not is a real pose.</para>
///
/// <para><b>Two nine-channel outlets, out</b>, unchanged: <c>VHI_Control</c> and
/// <c>VHI_Predict</c> publish each hand's whole pose at 60 Hz. A read-back is a
/// recording, and a recording wants one row per instant.</para>
///
/// Stream resolution blocks ~1 s so it runs on a background <c>Task.Run</c> thread;
/// results and log lines are marshalled back to Godot's main thread via
/// <c>CallDeferred</c>. Sample pulls themselves are non-blocking and happen in
/// <c>_Process</c>. Streams are resolved <i>by name only</i>, so a stream with a
/// different type or channel count won't be rejected - just yields wrong motion.
/// </summary>
public partial class LSLCommunicationController : Node
{
	/// <summary>One DOF's inlet: which stream carries it, and where its value lands.</summary>
	/// <remarks>
	/// <see cref="Name"/> is the DOF's address and the LSL stream's name — they are the same
	/// string, which is the point of the design. <see cref="Channel"/> is where the value goes
	/// in the hand's nine-slot pose and is nobody's business but this renderer's; what a
	/// producer writes is channel 0 of a stream of its own.
	/// </remarks>
	private sealed class DofInlet
	{
		public string Name;
		public HandSkeleton Hand;
		public bool Control;
		public int Channel;
		public object Inlet;
		public float[] Buffer = new float[1];

		/// <summary>When this inlet last delivered. <c>MinValue</c> until it does.</summary>
		public DateTime LastSample = DateTime.MinValue;

		/// <summary>When this inlet was opened — the grace period a fresh one is owed.</summary>
		public DateTime LastAttempt = DateTime.MinValue;
	}

	private readonly List<DofInlet> dofs = [];

	/// <summary>What <c>+1</c> means on both pose outlets, published in their metadata.</summary>
	/// <remarks>
	/// <c>standard</c>: <c>+1</c> is the direction the channel's name denotes, <c>0</c> is
	/// rest, the domain is <c>[-1, 1]</c>. Recordings made before this existed are in
	/// <c>legacy</c> — the rig's own units, where the five flexion channels ran the other way
	/// — and a reader that finds no <c>pose_convention</c> must assume that, not this.
	/// </remarks>
	public const string PoseConvention = "standard";

	/// <summary>Name of the LSL outlet that publishes the control hand's
	/// pose at 60 Hz. Consumed by MyoGestic as a regression-target source.</summary>
	[Export] public string ControlOutletName = "VHI_Control";

	/// <summary>Name of the LSL outlet that publishes the predicted hand's
	/// pose at 60 Hz. For monitoring / recording alongside the EMG.</summary>
	[Export] public string PredictedOutletName = "VHI_Predict";

	/// <summary>Channel count of the 9-DOF hand pose the two outlets publish.</summary>
	[Export] public int ExpectedChannels = 9;

	private object controlOutlet;     // StreamOutlet
	private object predictedOutlet;   // StreamOutlet

	/// <summary>Silence after which a prediction DOF's inlet is assumed dead and dropped.</summary>
	/// <remarks>
	/// Generous on purpose. A `ControlBus` outlet sends at its own rate whether or not the
	/// values changed, so real silence means the producer went away — but a script that
	/// pushes a frame and then sleeps is also legitimate, and dropping its inlet only costs
	/// a re-resolve. Long enough not to punish that; short enough that replacing an outlet
	/// (which the control-map editor does on every save) reconnects while you are still
	/// looking at the hand.
	/// </remarks>
	[Export] public float PredictionStaleAfterSeconds = 5.0f;

	/// <summary>Silence after which a control-pose DOF's inlet is assumed gone.</summary>
	/// <remarks>
	/// Presence is the only thing that hands the control hand to these streams, so "the
	/// producer stopped" has to be observable here rather than inferred from an error —
	/// <see cref="LSLWrapper.PullSample"/> catches every exception and returns <c>0.0</c>, so
	/// no error ever reaches this file and a lost producer is indistinguishable from an idle
	/// one. The clock is the whole signal.
	/// <para>It settles two things, not one: the DOF stops counting toward
	/// <see cref="ControlPoseLive"/>, and its inlet is dropped so the by-name resolve can find
	/// whoever publishes that name next. Without the second, the first producer to exit
	/// cleanly pinned that inlet to a corpse for the life of the process.</para>
	/// </remarks>
	[Export] public float ControlPoseStaleAfterSeconds = 5.0f;

	/// <summary>Whether <i>any</i> control-pose stream is delivering right now.</summary>
	/// <remarks>
	/// `ControlHandSkeleton` reads this to decide whether to render what it was commanded or
	/// run its movement state machine. It is the whole of the old `Declare(control_pose=true)`
	/// handshake: publish a stream and the hand follows it.
	/// <para><b>Any, not all.</b> There are nine control-pose streams now and a producer is
	/// under no obligation to publish more than one — driving a single DOF from a single
	/// producer is the capability this shape exists for, so requiring all nine would mean such
	/// a producer never took the hand at all. One live DOF is a client driving this hand, and
	/// the eight that are silent are DOFs nobody is driving, which is not the same as nobody
	/// driving the hand. The falling edge is therefore the <i>last</i> stream going quiet,
	/// which is when the hand is genuinely unclaimed and `StopToRest` is right.</para>
	/// </remarks>
	public bool ControlPoseLive { get; private set; }

	/// <summary>When the last resolve pass started. One pass, whatever is missing.</summary>
	private DateTime lastConnectionAttempt;
	private float connectionRetryInterval = 5.0f;

	/// <summary>A resolve pass is running. There is never more than one — see `_Process`.</summary>
	private bool isConnecting = false;
	private volatile bool isShuttingDown = false;  // Signal background tasks to stop
	private readonly object connectionLock = new();  // Lock for thread-safe connection status

	public override void _Ready()
	{
		GD.Print("=== LSL Communication Controller _Ready() START ===");

		// Initialize LSL wrapper
		GD.Print("  Initializing LSL wrapper...");
		LSLWrapper.Initialize();

		// One inlet per DOF, from the one table that knows which controls this build
		// exports. The hands are looked up here rather than looking this node up from
		// there: a sample is applied where it lands, and only this node knows when one
		// landed.
		var controlHand = GetNode<ControlHandSkeleton>("/root/Main/ControlHand");
		var predictedHand = GetNode<PredictedHandSkeleton>("/root/Main/PredictedHand");
		foreach ((string address, bool control, int channel) in VhiControlService.PoseStreams())
		{
			dofs.Add(new DofInlet
			{
				Name = address,
				Hand = control ? controlHand : predictedHand,
				Control = control,
				Channel = channel,
			});
		}
		GD.Print($"  {dofs.Count} per-DOF pose streams to resolve");

		// Allow an immediate first attempt.
		lastConnectionAttempt = DateTime.Now.AddSeconds(-connectionRetryInterval);

		// Create LSL outlets (optional, can be disabled)
		GD.Print("  Creating LSL outlets...");
		CreateOutlets();

		GD.Print("=== LSL Communication Controller _Ready() COMPLETE ===");
	}

	public override void _Process(double delta)
	{
		// --- Connect: one resolve pass, for every DOF that has no inlet ---------------
		//
		// **At most one resolve is ever in flight.** `isConnecting` is a single flag over
		// the whole set, not one per DOF, and the pass itself resolves *once* and matches
		// every missing name against that one answer — so eighteen missing streams cost
		// one resolve, not eighteen. That is not an optimisation, it is the safety
		// property: concurrent liblsl resolves kernel-panicked a machine here (configd
		// watchdog, 2026-07-31), and a design with one inlet per DOF is exactly the shape
		// that invites a resolve per DOF. There is no code path that starts a second.
		if (!isConnecting && !isShuttingDown
			&& (DateTime.Now - lastConnectionAttempt).TotalSeconds >= connectionRetryInterval
			&& dofs.Exists(d => d.Inlet == null))
		{
			lock (connectionLock)
			{
				if (!isConnecting)  // Double-check inside lock
				{
					isConnecting = true;
					lastConnectionAttempt = DateTime.Now;
					Task.Run(ResolveMissingInletsAsync);
				}
			}
		}

		bool controlLive = false;
		DateTime now = DateTime.Now;
		foreach (DofInlet dof in dofs)
		{
			float staleAfter = dof.Control ? ControlPoseStaleAfterSeconds : PredictionStaleAfterSeconds;

			// --- Pull, and apply on arrival ------------------------------------------
			//
			// Latest sample wins within a frame; the value goes straight onto the hand.
			// Channel 0 because the stream is this DOF and nothing else — a wider producer
			// is read at 0 and its extra channels ignored, which is the same tolerance the
			// buffer sizing gives a narrower manifest.
			if (dof.Inlet != null)
			{
				int got = 0;
				while (LSLWrapper.PullSample(dof.Inlet, dof.Buffer, 0.0) > 0)
					got++;
				if (got > 0)
				{
					dof.LastSample = now;
					dof.Hand.SetStandardValue(dof.Channel, dof.Buffer[0]);
				}
			}

			// --- Drop an inlet that has gone quiet, so it can be re-resolved -----------
			//
			// The clock, not an error: `LSLWrapper.PullSample` returns 0.0 on any failure,
			// so a producer that died and one that is merely idle arrive here looking
			// identical. Measured before this existed: a producer exiting cleanly left the
			// inlet held forever and the *next* producer was invisible until VHI restarted,
			// because the field never read null and the resolve above never ran again.
			//
			// `LastSample` is MinValue until this inlet actually delivers, so judging it on
			// that alone would drop every fresh inlet on its first frame. `LastAttempt` is
			// when this inlet was opened — per DOF, so a pass that opens somebody else's
			// inlet cannot extend this one's grace, which a single shared timestamp would.
			if (dof.Inlet != null && !isShuttingDown)
			{
				DateTime quietSince = dof.LastSample > dof.LastAttempt ? dof.LastSample : dof.LastAttempt;
				if ((now - quietSince).TotalSeconds >= staleAfter)
				{
					GD.Print(
						$"⏱️ No {dof.Name} samples for {staleAfter:F0}s "
						+ "— dropping the inlet and looking again.");
					DropInlet(dof);
				}
			}

			// Presence, evaluated every frame and per DOF: holding an inlet is not the
			// stream delivering.
			if (dof.Control && dof.Inlet != null
				&& (now - dof.LastSample).TotalSeconds < ControlPoseStaleAfterSeconds)
			{
				controlLive = true;
			}
		}
		ControlPoseLive = controlLive;
	}

	/// <summary>Close an inlet and clear the field, so nothing is left for the finalizer.</summary>
	/// <remarks>
	/// <para>Every path that gives up on an inlet must come through here. They used to just
	/// assign <see langword="null"/>, which does not close anything: a
	/// <c>stream_inlet</c> is a native object with its own receiver threads and open
	/// sockets, and dropping the managed reference only queues it for the GC. Teardown then
	/// happened at some arbitrary later moment on the finalizer thread — in practice while
	/// this renderer had already resolved and opened a *replacement* inlet for the same
	/// stream, because the retry loop starts as soon as the field reads null.</para>
	/// <para>That is what crashed VHI: liblsl 1.17.4's <c>cancellable_streambuf</c>
	/// destructor flushes through a socket that <c>cancel()</c> has already reset, so the
	/// abandoned inlet's <c>info_receiver</c> thread dereferenced null and took the process
	/// with it (<c>EXC_BAD_ACCESS at 0x0</c>). A renderer that reconnects on a 5s stale
	/// timer does this often — 28 times in one session here — and the suite that churns
	/// outlets made it likely enough to hit.</para>
	/// <para>The field is cleared under the lock, because the resolve pass assigns it from a
	/// task thread. The close happens *outside* the lock: it joins the inlet's threads, and
	/// holding a lock the connect path wants across a network teardown is how this renderer
	/// used to stall the app it was talking to.</para>
	/// </remarks>
	private void DropInlet(DofInlet dof)
	{
		object doomed;
		lock (connectionLock)
		{
			doomed = dof.Inlet;
			dof.Inlet = null;
		}
		if (doomed != null)
			LSLWrapper.Dispose(doomed);
	}

	/// <summary>
	/// One resolve, then an inlet for every DOF whose stream that resolve found. Runs on a
	/// background thread; only ever one at a time.
	/// </summary>
	/// <remarks>
	/// <para>The single resolve is the whole safety argument. `LSLWrapper.ResolveAll` is one
	/// liblsl network resolve however many names are wanted, so asking it once and matching
	/// locally means the number of concurrent resolves is one no matter how many DOFs this
	/// build exports — and cannot grow when it exports more.</para>
	/// <para><c>recover: false</c> is load-bearing. Recovery matches on <c>source_id</c>, so
	/// it cannot reattach when the replacement stream has a different channel count: a client
	/// that rebuilds its outlet with a different set of controls (which the control-map editor
	/// does on every save) would leave this inlet dead for the life of the process, still
	/// reporting itself as working. Off, a lost producer simply stops delivering, the
	/// staleness clock drops the inlet, and this pass picks up whoever publishes that name
	/// now. That is the recovery this renderer actually wants.</para>
	/// </remarks>
	private void ResolveMissingInletsAsync()
	{
		try
		{
			if (isShuttingDown)
				return;

			var wanted = new List<string>();
			foreach (DofInlet dof in dofs)
			{
				if (dof.Inlet == null)
					wanted.Add(dof.Name);
			}
			if (wanted.Count == 0)
				return;

			// Background thread — use CallDeferred for GD.Print.
			CallDeferred(nameof(LogMessage), $"🔍 Looking for {wanted.Count} pose stream(s)...");

			object[] found = LSLWrapper.ResolveAll(1.0);
			if (isShuttingDown || found == null || found.Length == 0)
				return;

			var byName = new Dictionary<string, object>();
			foreach (object info in found)
				byName.TryAdd(LSLWrapper.GetStreamInfoName(info), info);

			foreach (DofInlet dof in dofs)
			{
				if (isShuttingDown)
					return;
				if (dof.Inlet != null || !byName.TryGetValue(dof.Name, out object info))
					continue;

				int channelCount = LSLWrapper.GetStreamInfoChannelCount(info);
				object inlet = LSLWrapper.CreateStreamInlet(info, recover: false);
				lock (connectionLock)
				{
					if (channelCount > 0 && dof.Buffer.Length != channelCount)
						dof.Buffer = new float[channelCount];
					// A new inlet has delivered nothing, and must not inherit the previous
					// producer's clock: an inlet resolved within the stale window of the old
					// one's last sample would otherwise read live before a byte arrived, and
					// on the control hand that refuses SetMovement / SetSpeed / SetFrozen with
					// no driver present.
					dof.LastSample = DateTime.MinValue;
					dof.LastAttempt = DateTime.Now;
					// Last, and the order matters: `_Process` reads these without the lock,
					// and `Inlet` is what it gates on. Published after the clock it will be
					// judged against, never before, or a fresh inlet is dropped on the frame
					// it was opened for having a `LastAttempt` that had not landed yet.
					dof.Inlet = inlet;
				}
				CallDeferred(nameof(LogMessage),
					$"✅ Connected to LSL inlet: {dof.Name} ({channelCount} channels)");
			}
		}
		catch (Exception e)
		{
			CallDeferred(nameof(LogError), $"❌ Error resolving LSL inlets: {e.Message}");
		}
		finally
		{
			lock (connectionLock)
			{
				isConnecting = false;
			}
		}
	}

	// Helper methods for thread-safe logging
	private void LogMessage(string message)
	{
		GD.Print(message);
	}

	private void LogError(string message)
	{
		GD.PrintErr(message);
	}

	private void CreateOutlets()
	{
		GD.Print("    ENTERING CreateOutlets()...");
		try
		{
			// Get the absolute path to the movements config file
			string configPath = ProjectSettings.GlobalizePath("user://movements.toml");
			
			GD.Print("    Creating control hand StreamInfo...");
			// Create control hand outlet
			var controlInfo = LSLWrapper.CreateStreamInfo(
				name: ControlOutletName,
				type: "MyoGestic_9DVector",
				channelCount: ExpectedChannels,
				nominalSrate: 60.0,
				channelFormat: "Float",
				// Bumped from control_hand_001 with the switch to standard values. The name
				// and channel labels are unchanged, so a consumer resolving by name cannot
				// tell the conventions apart — and the two disagree by a sign on five of the
				// nine channels. A recorder that captures source_id can; one that does not
				// gets the same silent break either way, which is why the convention is also
				// stated outright below.
				sourceId: "control_hand_002_standard"
			);
			GD.Print("    StreamInfo created successfully");

			// Add channel labels and config path
			string[] channelLabels = [
				"ThumbFlexion", "ThumbAbduction", "IndexFlexion",
				"MiddleFlexion", "RingFlexion", "PinkyFlexion",
				"WristFlexion", "WristAbduction", "WristRotation"
			];
			GD.Print($"    Setting {channelLabels.Length} channel labels...");
			LSLWrapper.SetChannelLabels(controlInfo, channelLabels);
			LSLWrapper.SetStreamMetadata(controlInfo, "config_file", configPath);
			LSLWrapper.SetStreamMetadata(controlInfo, "pose_convention", PoseConvention);

			GD.Print("    Creating StreamOutlet...");
			controlOutlet = LSLWrapper.CreateStreamOutlet(controlInfo);
			GD.Print($"    \u2705 Control outlet created successfully!");

			GD.Print("    Creating predicted hand StreamInfo...");
			// Create predicted hand outlet
			var predictedInfo = LSLWrapper.CreateStreamInfo(
				name: PredictedOutletName,
				type: "MyoGestic_9DVector",
				channelCount: ExpectedChannels,
				nominalSrate: 60.0,
				channelFormat: "Float",
				sourceId: "predicted_hand_001"
			);
			GD.Print("    StreamInfo created successfully");

			LSLWrapper.SetChannelLabels(predictedInfo, channelLabels);
			LSLWrapper.SetStreamMetadata(predictedInfo, "config_file", configPath);
			LSLWrapper.SetStreamMetadata(predictedInfo, "pose_convention", PoseConvention);

			GD.Print("    Creating StreamOutlet...");
			predictedOutlet = LSLWrapper.CreateStreamOutlet(predictedInfo);
			GD.Print($"    \u2705 Predicted outlet created successfully!");
		}
		catch (Exception e)
		{
			GD.PrintErr($"\u274c Error creating LSL outlets: {e.Message}");
			GD.PrintErr($"    Stack trace: {e.StackTrace}");
		}
		GD.Print("    EXITING CreateOutlets()");
	}

	/// <summary>Push a 9-DOF pose sample to the <c>VHI_Control</c> outlet.
	/// The sample is silently
	/// dropped if the outlet has not been created or if its length does not
	/// match <see cref="ExpectedChannels"/>. Called by
	/// <see cref="ControlHandSkeleton"/> every <c>_PhysicsProcess</c> tick.</summary>
	/// <param name="data">9 float channels - see the LSL stream reference for
	/// the channel layout.</param>
	public void SendControlData(List<float> data)
	{
		if (controlOutlet != null && data.Count == ExpectedChannels)
		{
			try
			{
				float[] sample = [.. data];
				LSLWrapper.PushSample(controlOutlet, sample);
				}
			catch (Exception e)
			{
				GD.PrintErr($"❌ Error sending control data: {e.Message}");
			}
		}
	}

	/// <summary>Push a 9-DOF pose sample to the <c>VHI_Predict</c> outlet.
	/// Same shape and contract as
	/// <see cref="SendControlData"/>. Called by
	/// <see cref="PredictedHandSkeleton"/> every <c>_PhysicsProcess</c> tick.</summary>
	/// <param name="data">9 float channels - see the LSL stream reference for
	/// the channel layout.</param>
	public void SendPredictedData(List<float> data)
	{
		if (predictedOutlet != null && data.Count == ExpectedChannels)
		{
			try
			{
				float[] sample = [.. data];
				LSLWrapper.PushSample(predictedOutlet, sample);
				}
			catch (Exception e)
			{
				GD.PrintErr($"❌ Error sending predicted data: {e.Message}");
			}
		}
	}

	public override void _ExitTree()
	{
		// Signal background tasks to stop immediately
		isShuttingDown = true;

		// Dispose LSL resources — don't wait for network cleanup
		try
		{
			foreach (DofInlet dof in dofs)
				LSLWrapper.Dispose(dof.Inlet);
			LSLWrapper.Dispose(controlOutlet);
			LSLWrapper.Dispose(predictedOutlet);
		}
		catch (Exception) { }

		GD.Print("LSL Communication Controller stopped");
	}

	public override void _Notification(int what)
	{
		// Force exit when the window close is requested. LSL background
		// threads (and the in-process gRPC Kestrel server) keep .NET from
		// returning from Main on its own, so we have to push the process
		// out.
		if (what == NotificationWMCloseRequest)
		{
			isShuttingDown = true;
			GetTree().Quit();

			// Why _exit() and not Environment.Exit()? Environment.Exit() runs
			// C++ static destructors via __cxa_finalize. On macOS, that races
			// with Godot's main-thread SceneTree::finalize() teardown - both
			// touch Godot's global StringName mutex, the second one finds it
			// in a half-destroyed state, throws std::system_error, and the
			// process aborts with a crash report. _exit() is the POSIX
			// immediate-exit syscall: it skips destructors, the kernel
			// reaps the process directly, no race possible. We've already
			// given Godot a half-second to repaint / flush.
			Task.Run(async () =>
			{
				await Task.Delay(500);
				_exit(0);
			});
		}
	}

	[DllImport("libc", EntryPoint = "_exit")]
	private static extern void _exit(int status);
}
