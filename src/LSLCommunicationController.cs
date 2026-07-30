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
/// Resolves two inlets and publishes two outlets:
/// <list type="bullet">
///   <item><description><b>Inlet</b> <c>MyoGestic_Output</c> - 9 × <c>float32</c>,
///     drives the predicted hand. Typically ~32 Hz.</description></item>
///   <item><description><b>Inlet</b> <c>MyoGestic_ControlPose</c> (optional) - 9 ×
///     <c>float32</c>, drives the control hand only when its
///     <see cref="ControlHandSkeleton.DriverMode"/> is <c>Stream</c>.</description></item>
///   <item><description><b>Outlet</b> <c>VHI_Control</c> - the control hand's current
///     pose, 60 Hz. MyoGestic consumes this as a training-target source.</description></item>
///   <item><description><b>Outlet</b> <c>VHI_Predict</c> - the predicted hand's current
///     pose, 60 Hz. For monitoring / recording alongside EMG.</description></item>
/// </list>
///
/// Stream resolution blocks ~1 s so it runs on a background <c>Task.Run</c> thread;
/// results and log lines are marshalled back to Godot's main thread via
/// <c>CallDeferred</c>. Sample pulls themselves are non-blocking and happen in
/// <c>_Process</c>. Streams are resolved <i>by name only</i>, so a stream with a
/// different type or channel count won't be rejected - just yields wrong motion.
/// </summary>
public partial class LSLCommunicationController : Node
{
	/// <summary>Name of the LSL inlet that drives the predicted hand.
	/// Resolved by name only.</summary>
	[Export] public string PredictionStreamName = "MyoGestic_Output";

	/// <summary>LSL type advertised by the predicted-hand stream. Currently
	/// not used for resolution - inlets are matched by name only - but kept
	/// as Inspector metadata.</summary>
	[Export] public string PredictionStreamType = "MyoGestic_9DVector";

	/// <summary>Name of the optional LSL inlet that drives the control hand
	/// in <see cref="ControlHandDriverMode.Stream"/>. Resolved by name only;
	/// missing is fine - the inlet is simply skipped.</summary>
	[Export] public string ControlPoseStreamName = "MyoGestic_ControlPose";

	/// <summary>Name of the LSL outlet that publishes the control hand's
	/// pose at 60 Hz. Consumed by MyoGestic as a regression-target source.</summary>
	[Export] public string ControlOutletName = "VHI_Control";

	/// <summary>Name of the LSL outlet that publishes the predicted hand's
	/// pose at 60 Hz. For monitoring / recording alongside the EMG.</summary>
	[Export] public string PredictedOutletName = "VHI_Predict";

	/// <summary>Channel count for the 9-DOF hand pose. Sizes both inlet
	/// sample buffers and the outlets' advertised channel count. VHI does
	/// <i>not</i> reject mismatched inlet streams - it just logs the count
	/// on connect and reads into the fixed-size buffer.</summary>
	[Export] public int ExpectedChannels = 9;

	/// <summary>Publish <c>VHI_Control</c> / <c>VHI_Predict</c> when
	/// <see langword="true"/>; suppress them when <see langword="false"/>.
	/// Useful when something downstream resolves them but you don't want
	/// them participating yet.</summary>
	[Export] public bool EnableOutlets = true;

	private object predictionInlet;   // StreamInlet (MyoGestic_Output) -> predicted hand
	private object controlPoseInlet;  // StreamInlet (MyoGestic_ControlPose) -> control hand (Stream mode)
	private object controlOutlet;     // StreamOutlet
	private object predictedOutlet;   // StreamOutlet

	private List<float> receivedDataControl = [];
	private List<float> receivedDataPredicted = [];
	private float[] sampleBuffer;
	private float[] controlPoseBuffer;

	/// <summary>Wire index -> this renderer's pose channel, or <see langword="null"/>.</summary>
	/// <remarks>
	/// <para>Built from the producer's channel labels when it publishes any: each label is a
	/// control address, and <see cref="VhiControlService.ChannelForAddress"/> says
	/// where that address lives in the pose. A labelled stream may therefore be any width and
	/// in any order — two channels carrying index and middle is a complete stream, not a
	/// truncated nine.</para>
	/// <para><see langword="null"/> means the producer labelled nothing, which is every
	/// producer written before labels existed. Then the wire *is* the pose order, read
	/// positionally exactly as before.</para>
	/// </remarks>
	private int[] predictionRouting;
	private int[] controlPoseRouting;

	/// <summary>When the prediction inlet last delivered a sample.</summary>
	/// <remarks>
	/// The only reliable way this renderer learns a producer is gone. liblsl does not say:
	/// with recovery on it hides the loss, and with recovery off a zero-timeout pull on a
	/// dead stream still just returns "no samples" rather than raising. Either way the inlet
	/// looks alive forever, which left the by-name search below unreachable and the hand
	/// frozen on its last pose.
	/// </remarks>
	private DateTime lastPredictionSample = DateTime.Now;

	/// <summary>Silence after which the prediction inlet is assumed dead and dropped.</summary>
	/// <remarks>
	/// Generous on purpose. A `ControlBus` outlet sends at its own rate whether or not the
	/// values changed, so real silence means the producer went away — but a script that
	/// pushes a frame and then sleeps is also legitimate, and dropping its inlet only costs
	/// a re-resolve. Long enough not to punish that; short enough that replacing an outlet
	/// (which the control-map editor does on every save) reconnects while you are still
	/// looking at the hand.
	/// </remarks>
	[Export] public float PredictionStaleAfterSeconds = 5.0f;

	public new bool IsConnected { get; private set; } = false;  // 'new' to hide base class member
	public int LastInputFPS { get; private set; } = 0;
	public int LastOutputFPS { get; private set; } = 0;

	private DateTime lastInputTime;
	private DateTime lastOutputTime;
	private DateTime lastConnectionAttempt;
	private DateTime lastControlPoseAttempt;
	private int inputFrameCount = 0;
	private int outputFrameCount = 0;
	private float connectionRetryInterval = 5.0f;
	private bool isConnecting = false;             // prediction inlet connect in progress
	private bool isConnectingControlPose = false;  // control-pose inlet connect in progress
	private volatile bool isShuttingDown = false;  // Signal background tasks to stop
	private readonly object connectionLock = new();  // Lock for thread-safe connection status

	public override void _Ready()
	{
		GD.Print("=== LSL Communication Controller _Ready() START ===");

		// Initialize LSL wrapper
		GD.Print("  Initializing LSL wrapper...");
		LSLWrapper.Initialize();

		// Sized for an unlabelled producer, which sends this renderer's full pose layout.
		// A labelled one may be narrower, and each buffer is resized to its stream when the
		// inlet resolves — liblsl requires the buffer to match the stream's channel count.
		sampleBuffer = new float[ExpectedChannels];
		controlPoseBuffer = new float[ExpectedChannels];
		GD.Print("  Sample buffers initialized");

		// Initialize timestamps
		lastInputTime = DateTime.Now;
		lastOutputTime = DateTime.Now;
		lastConnectionAttempt = DateTime.Now.AddSeconds(-connectionRetryInterval); // Allow immediate first attempt
		lastControlPoseAttempt = lastConnectionAttempt;
		GD.Print("  Timestamps initialized");

		// Create LSL outlets (optional, can be disabled)
		if (EnableOutlets)
		{
			GD.Print("  Creating LSL outlets...");
			CreateOutlets();
		}
		else
		{
			GD.Print("  LSL outlets DISABLED");
		}
		GD.Print("=== LSL Communication Controller _Ready() COMPLETE ===");
	}

	public override void _Process(double delta)
	{
		// --- Connect: prediction inlet (MyoGestic_Output -> predicted hand) ---
		if (predictionInlet == null && !isConnecting && !isShuttingDown &&
			(DateTime.Now - lastConnectionAttempt).TotalSeconds >= connectionRetryInterval)
		{
			lock (connectionLock)
			{
				if (!isConnecting)  // Double-check inside lock
				{
					isConnecting = true;
					lastConnectionAttempt = DateTime.Now;
					Task.Run(ConnectToInletAsync);
				}
			}
		}

		// --- Connect: control-pose inlet (MyoGestic_ControlPose -> control hand) ---
		if (controlPoseInlet == null && !isConnectingControlPose && !isShuttingDown &&
			(DateTime.Now - lastControlPoseAttempt).TotalSeconds >= connectionRetryInterval)
		{
			lock (connectionLock)
			{
				if (!isConnectingControlPose)
				{
					isConnectingControlPose = true;
					lastControlPoseAttempt = DateTime.Now;
					Task.Run(ConnectControlPoseInletAsync);
				}
			}
		}

		// --- Pull: prediction inlet -> receivedDataPredicted (latest sample only) ---
		if (predictionInlet != null)
		{
			try
			{
				int samplesThisFrame = 0;
				while (LSLWrapper.PullSample(predictionInlet, sampleBuffer, 0.0) > 0)
				{
					receivedDataPredicted = ToPoseFrame(sampleBuffer, predictionRouting);
					samplesThisFrame++;
				}

				if (samplesThisFrame > 0)
				{
					lastPredictionSample = DateTime.Now;
					inputFrameCount += samplesThisFrame;
					var timeSinceLastInput = (DateTime.Now - lastInputTime).TotalSeconds;
					if (timeSinceLastInput >= 1.0)
					{
						LastInputFPS = (int)(inputFrameCount / timeSinceLastInput);
						inputFrameCount = 0;
						lastInputTime = DateTime.Now;
					}
				}
			}
			catch (Exception e)
			{
				GD.PrintErr($"Error pulling LSL prediction sample: {e.Message}");
				predictionInlet = null;
				predictionRouting = null;
				IsConnected = false;
				receivedDataPredicted.Clear();
			}
		}

		// --- Drop a prediction inlet that has gone quiet, so it can be re-resolved ---
		if (predictionInlet != null && !isShuttingDown
			&& (DateTime.Now - lastPredictionSample).TotalSeconds >= PredictionStaleAfterSeconds)
		{
			GD.Print(
				$"⏱️ No {PredictionStreamName} samples for "
				+ $"{PredictionStaleAfterSeconds:F0}s — dropping the inlet and looking again.");
			lock (connectionLock)
			{
				predictionInlet = null;
				predictionRouting = null;
				IsConnected = false;
			}
			receivedDataPredicted.Clear();
			// Reset the clock, or the next inlet is judged on this one's silence.
			lastPredictionSample = DateTime.Now;
		}

		// --- Pull: control-pose inlet -> receivedDataControl (latest sample only) ---
		if (controlPoseInlet != null)
		{
			try
			{
				while (LSLWrapper.PullSample(controlPoseInlet, controlPoseBuffer, 0.0) > 0)
					receivedDataControl = ToPoseFrame(controlPoseBuffer, controlPoseRouting);
			}
			catch (Exception e)
			{
				GD.PrintErr($"Error pulling LSL control-pose sample: {e.Message}");
				controlPoseInlet = null;
				controlPoseRouting = null;
				receivedDataControl.Clear();
			}
		}

		// --- Update output FPS ---
		var timeSinceLastOutput = (DateTime.Now - lastOutputTime).TotalSeconds;
		if (timeSinceLastOutput >= 1.0)
		{
			LastOutputFPS = (int)(outputFrameCount / timeSinceLastOutput);
			outputFrameCount = 0;
			lastOutputTime = DateTime.Now;
		}
	}

	// Resolves an LSL stream by name on the calling (background) thread.
	// Returns the created inlet, or null if not found / shutting down / on error.
	/// <summary>Channel labels -> wire-index-to-pose-channel routing, or null.</summary>
	/// <remarks>
	/// Returns <see langword="null"/> — meaning "read positionally" — when the producer
	/// labelled nothing, and also when it labelled channels this renderer does not recognise
	/// as addresses. The second case matters: labels are a general LSL feature and a producer
	/// may name its channels for a human ("IndexFlexion") without meaning them as addresses.
	/// Routing on a partial match would silently drop the channels that did not resolve, so a
	/// set of labels is taken as a routing table only if <b>every</b> one of them is an
	/// address this renderer renders.
	/// </remarks>
	private int[] BuildRouting(string[] labels, bool controlPose, string streamName)
	{
		if (labels == null || labels.Length == 0)
			return null;

		var routing = new int[labels.Length];
		for (int i = 0; i < labels.Length; i++)
		{
			routing[i] = VhiControlService.ChannelForAddress(labels[i], controlPose);
			if (routing[i] < 0)
			{
				CallDeferred(nameof(LogMessage),
					$"ℹ️ {streamName} labels channel {i} '{labels[i]}', which is not an address "
					+ "this hand renders — reading the stream positionally instead.");
				return null;
			}
		}
		return routing;
	}

	/// <summary>One wire sample as a full pose frame in this renderer's channel order.</summary>
	/// <remarks>
	/// With a routing table the frame is always the renderer's full width, so everything
	/// downstream keeps indexing the pose by channel and none of it has to know the wire was
	/// narrower or differently ordered. Channels the producer does not send stay at rest.
	/// </remarks>
	private List<float> ToPoseFrame(float[] wire, int[] routing)
	{
		if (routing == null)
			return [.. wire];

		var pose = new List<float>(ExpectedChannels);
		for (int i = 0; i < ExpectedChannels; i++)
			pose.Add(0f);
		for (int i = 0; i < routing.Length && i < wire.Length; i++)
			if (routing[i] >= 0 && routing[i] < ExpectedChannels)
				pose[routing[i]] = wire[i];
		return pose;
	}

	private object TryResolveInlet(
		string streamName, bool controlPose, out int[] routing, out int width)
	{
		routing = null;
		width = ExpectedChannels;
		try
		{
			if (isShuttingDown)
				return null;

			// Background thread — use CallDeferred for GD.Print.
			CallDeferred(nameof(LogMessage), $"🔍 Searching for LSL stream: {streamName}...");

			// Resolve by name only, so we never connect to VHI's own outlets.
			var streams = LSLWrapper.Resolve("name", streamName, 1.0);
			if (isShuttingDown || streams == null || streams.Length == 0)
				return null;

			// recover: false is load-bearing. With liblsl's recovery on, a producer that
			// goes away is hidden — the inlet keeps "working", silently delivering nothing,
			// and the catch below that nulls it and lets the by-name search run again never
			// fires. Worse, recovery matches on source_id, so it cannot reattach when the
			// replacement stream has a different channel count: a client that rebuilds its
			// outlet with a different set of controls (which the control-map editor does on
			// every save) left this inlet dead for the life of the process.
			//
			// Off, a lost producer raises, the inlet is dropped, and the resolve-by-name
			// loop picks up whatever is publishing that name now. That is the recovery this
			// renderer actually wants, and it is the one it already had code for.
			var inlet = LSLWrapper.CreateStreamInlet(streams[0], recover: false);
			int channelCount = LSLWrapper.GetStreamInfoChannelCount(streams[0]);
			width = channelCount > 0 ? channelCount : ExpectedChannels;

			// The resolved StreamInfo carries only the header, so the labels have to be
			// fetched from the sender. A producer that publishes none is the normal case.
			routing = BuildRouting(
				LSLWrapper.GetChannelLabels(LSLWrapper.GetInletStreamInfo(inlet)),
				controlPose,
				streamName);
			string layout = routing == null
				? "positional"
				: $"labelled -> pose channels [{string.Join(", ", routing)}]";
			CallDeferred(nameof(LogMessage),
				$"✅ Connected to LSL inlet: {streamName} ({channelCount} channels, {layout})");
			return inlet;
		}
		catch (Exception e)
		{
			CallDeferred(nameof(LogError), $"❌ Error connecting to LSL inlet '{streamName}': {e.Message}");
			return null;
		}
	}

	private void ConnectToInletAsync()
	{
		var inlet = TryResolveInlet(
			PredictionStreamName, controlPose: false, out int[] routing, out int width);
		lock (connectionLock)
		{
			predictionInlet = inlet;
			predictionRouting = inlet != null ? routing : null;
			if (inlet != null)
				lastPredictionSample = DateTime.Now;
			if (inlet != null && sampleBuffer.Length != width)
				sampleBuffer = new float[width];
			IsConnected = inlet != null;
			isConnecting = false;
		}
		if (inlet == null)
			CallDeferred(nameof(ClearReceivedDataPredicted));
	}

	private void ConnectControlPoseInletAsync()
	{
		var inlet = TryResolveInlet(
			ControlPoseStreamName, controlPose: true, out int[] routing, out int width);
		lock (connectionLock)
		{
			controlPoseInlet = inlet;
			controlPoseRouting = inlet != null ? routing : null;
			if (inlet != null && controlPoseBuffer.Length != width)
				controlPoseBuffer = new float[width];
			isConnectingControlPose = false;
		}
		if (inlet == null)
			CallDeferred(nameof(ClearReceivedDataControl));
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

	private void ClearReceivedDataControl()
	{
		receivedDataControl.Clear();
	}

	private void ClearReceivedDataPredicted()
	{
		receivedDataPredicted.Clear();
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
				sourceId: "control_hand_001"
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

	public List<float> GetReceivedDataControl()
	{
		var data = receivedDataControl;
		receivedDataControl = [];
		return data;
	}

	public List<float> GetReceivedDataPredicted()
	{
		var data = receivedDataPredicted;
		receivedDataPredicted = [];
		return data;
	}

	/// <summary>Push a 9-DOF pose sample to the <c>VHI_Control</c> outlet
	/// (when <see cref="EnableOutlets"/> is on). The sample is silently
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
				outputFrameCount++;
			}
			catch (Exception e)
			{
				GD.PrintErr($"❌ Error sending control data: {e.Message}");
			}
		}
	}

	/// <summary>Push a 9-DOF pose sample to the <c>VHI_Predict</c> outlet
	/// (when <see cref="EnableOutlets"/> is on). Same shape and contract as
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
				outputFrameCount++;
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
			LSLWrapper.Dispose(predictionInlet);
			LSLWrapper.Dispose(controlPoseInlet);
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
