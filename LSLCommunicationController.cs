using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
// NOTE: NOT using SharpLSL directly to avoid static initialization hang
// Instead using LSLWrapper which loads LSL via reflection

public partial class LSLCommunicationController : Node
{
	[Export] public string PredictionStreamName = "HandPredictions";
	[Export] public string PredictionStreamType = "EMG";
	[Export] public string ControlOutletName = "ControlHand";
	[Export] public string PredictedOutletName = "PredictedHand";
	[Export] public int ExpectedChannels = 9;
	[Export] public bool EnableOutlets = true;  // Can disable outlets if they cause issues

	private object predictionInlet;  // Actually StreamInlet, but using object to avoid type reference
	private object controlOutlet;    // Actually StreamOutlet
	private object predictedOutlet;  // Actually StreamOutlet

	private List<float> receivedDataControl = [];
	private List<float> receivedDataPredicted = [];
	private float[] sampleBuffer;

	public new bool IsConnected { get; private set; } = false;  // 'new' to hide base class member
	public int LastInputFPS { get; private set; } = 0;
	public int LastOutputFPS { get; private set; } = 0;

	private DateTime lastInputTime;
	private DateTime lastOutputTime;
	private DateTime lastConnectionAttempt;
	private int inputFrameCount = 0;
	private int outputFrameCount = 0;
	private float connectionRetryInterval = 5.0f;
	private bool isConnecting = false;  // Flag to track if connection attempt is in progress
	private readonly Lock connectionLock = new();  // Lock for thread-safe connection status

	public override void _Ready()
	{
		GD.Print("=== LSL Communication Controller _Ready() START ===");

		// Initialize LSL wrapper
		GD.Print("  Initializing LSL wrapper...");
		LSLWrapper.Initialize();

		// Initialize sample buffer
		sampleBuffer = new float[ExpectedChannels];
		GD.Print("  Sample buffer initialized");

		// Initialize timestamps
		lastInputTime = DateTime.Now;
		lastOutputTime = DateTime.Now;
		lastConnectionAttempt = DateTime.Now.AddSeconds(-connectionRetryInterval); // Allow immediate first attempt
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
		// Try to connect if not connected and not already trying
		if (predictionInlet == null && !isConnecting &&
			(DateTime.Now - lastConnectionAttempt).TotalSeconds >= connectionRetryInterval)
		{
			// Start connection attempt on background thread
			lock (connectionLock)
			{
				if (!isConnecting)  // Double-check inside lock
				{
					isConnecting = true;
					lastConnectionAttempt = DateTime.Now;
					Task.Run(() => ConnectToInletAsync());
				}
			}
		}

		// Pull data if connected
		if (predictionInlet != null)
		{
			try
			{
				// Non-blocking pull using wrapper
				double timestamp = LSLWrapper.PullSample(predictionInlet, sampleBuffer, 0.0);

				if (timestamp > 0)
				{
					// Successfully received data
					List<float> data = [.. sampleBuffer];
					receivedDataControl = [.. data];
					receivedDataPredicted = [.. data];

					// Update input FPS
					inputFrameCount++;
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
				GD.PrintErr($"❌ Error pulling LSL sample: {e.Message}");
				predictionInlet = null;
				IsConnected = false;
			}
		}

		// Update output FPS
		var timeSinceLastOutput = (DateTime.Now - lastOutputTime).TotalSeconds;
		if (timeSinceLastOutput >= 1.0)
		{
			LastOutputFPS = (int)(outputFrameCount / timeSinceLastOutput);
			outputFrameCount = 0;
			lastOutputTime = DateTime.Now;
		}
	}

	private void ConnectToInletAsync()
	{
		try
		{
			// This runs on a background thread, so use CallDeferred for GD.Print
			CallDeferred(nameof(LogMessage), $"🔍 Searching for LSL stream: {PredictionStreamName}...");

			// Try to resolve by name first with short timeout (1 second)
			var availableStreams = LSLWrapper.Resolve("name", PredictionStreamName, 1.0);

			// If not found by name, try by type
			if (availableStreams == null || availableStreams.Length == 0)
			{
				availableStreams = LSLWrapper.Resolve("type", PredictionStreamType, 1.0);
			}

			if (availableStreams != null && availableStreams.Length > 0)
			{
				// Use the first matching stream
				lock (connectionLock)
				{
					predictionInlet = LSLWrapper.CreateStreamInlet(availableStreams[0]);
					IsConnected = true;
				}

				string streamName = LSLWrapper.GetStreamInfoName(availableStreams[0]);
				int channelCount = LSLWrapper.GetStreamInfoChannelCount(availableStreams[0]);
				CallDeferred(nameof(LogMessage), $"✅ Connected to LSL inlet: {streamName} ({channelCount} channels)");
			}
			else
			{
				// Don't print "not found" message every time to avoid spam
				// Only print on first attempt or every 10th attempt
				lock (connectionLock)
				{
					IsConnected = false;
				}
			}
		}
		catch (Exception e)
		{
			CallDeferred(nameof(LogError), $"❌ Error connecting to LSL inlet: {e.Message}");
			lock (connectionLock)
			{
				predictionInlet = null;
				IsConnected = false;
			}
		}
		finally
		{
			// Always reset the connecting flag
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
			GD.Print("    Creating control hand StreamInfo...");
			// Create control hand outlet
			var controlInfo = LSLWrapper.CreateStreamInfo(
				name: ControlOutletName,
				type: "HandData",
				channelCount: ExpectedChannels,
				nominalSrate: 60.0,
				channelFormat: "Float",
				sourceId: "control_hand_001"
			);
			GD.Print("    StreamInfo created successfully");

			GD.Print("    Creating StreamOutlet...");
			controlOutlet = LSLWrapper.CreateStreamOutlet(controlInfo);
			GD.Print($"    ✅ Control outlet created successfully!");

			GD.Print("    Creating predicted hand StreamInfo...");
			// Create predicted hand outlet
			var predictedInfo = LSLWrapper.CreateStreamInfo(
				name: PredictedOutletName,
				type: "HandData",
				channelCount: ExpectedChannels,
				nominalSrate: 60.0,
				channelFormat: "Float",
				sourceId: "predicted_hand_001"
			);
			GD.Print("    StreamInfo created successfully");

			GD.Print("    Creating StreamOutlet...");
			predictedOutlet = LSLWrapper.CreateStreamOutlet(predictedInfo);
			GD.Print($"    ✅ Predicted outlet created successfully!");
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Error creating LSL outlets: {e.Message}");
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
		// Clean up LSL resources
		try
		{
			LSLWrapper.Dispose(predictionInlet);
			LSLWrapper.Dispose(controlOutlet);
			LSLWrapper.Dispose(predictedOutlet);
		}
		catch (Exception e)
		{
			GD.PrintErr($"Error disposing LSL resources: {e.Message}");
		}

		GD.Print("LSL Communication Controller stopped");
	}
}
