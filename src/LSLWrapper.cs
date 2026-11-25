using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Vhi;

/// <summary>
/// Wrapper for SharpLSL that avoids static initialization hang in Godot.
/// Uses reflection to dynamically load and call LSL methods.
/// </summary>
public static class LSLWrapper
{
	private static Assembly lslAssembly;
	private static Type lslType;
	private static Type streamInfoType;
	private static Type streamInletType;
	private static Type streamOutletType;
	private static Type channelFormatType;
	private static Type transportOptionsType;

	private static bool initialized = false;

	/// <summary>The environment variable liblsl reads its config path from.</summary>
	private const string LiblslConfigEnv = "LSLAPICFG";

	/// <summary>libc's <c>setenv</c>: the native environment block, which liblsl reads.</summary>
	/// <remarks>On macOS and Linux, .NET's <c>Environment.SetEnvironmentVariable</c> only
	/// updates the runtime's own copy of the environment; a native library's <c>getenv</c>
	/// never sees it, and liblsl then falls back to <c>./lsl_api.cfg</c> — the first build of
	/// this fix loaded the repo's file from the working directory instead of ours.</remarks>
	[DllImport("libc", SetLastError = true)]
	private static extern int setenv(string name, string value, int overwrite);

	private static void SetNativeEnv(string name, string value)
	{
		System.Environment.SetEnvironmentVariable(name, value);  // for managed readers
		if (setenv(name, value, 1) != 0)
			throw new InvalidOperationException($"setenv({name}) failed, errno {Marshal.GetLastWin32Error()}");
	}

	/// <summary>
	/// Initialize LSL types via reflection. Call this once before using LSL.
	/// </summary>
	public static void Initialize()
	{
		if (initialized) return;

		// Before the first liblsl call: liblsl reads its config once, on first use.
		ConfigureLiblsl();

		try
		{
			lslAssembly = Assembly.Load("SharpLSL");
			lslType = lslAssembly.GetType("SharpLSL.LSL");
			streamInfoType = lslAssembly.GetType("SharpLSL.StreamInfo");
			streamInletType = lslAssembly.GetType("SharpLSL.StreamInlet");
			streamOutletType = lslAssembly.GetType("SharpLSL.StreamOutlet");
			channelFormatType = lslAssembly.GetType("SharpLSL.ChannelFormat");
			transportOptionsType = lslAssembly.GetType("SharpLSL.TransportOptions");

			initialized = true;
			GD.Print("✅ LSLWrapper initialized successfully");
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to initialize LSLWrapper: {e.Message}");
			throw;
		}
	}

	/// <summary>A launcher's own liblsl config for VHI. Wins over the Connection setting.</summary>
	/// <remarks>Not <c>LSLAPICFG</c>: MyoGestic sets that at import, to a config that only
	/// quiets liblsl's logging, and VHI inherits it when MyoGestic launches it. Honouring it
	/// left VHI on liblsl's defaults in every MyoGestic session — IPv6 on, discovery on every
	/// interface — which is the setup the crashes below came from.</remarks>
	private const string OverrideEnv = "VHI_LSLAPICFG";

	/// <summary>Where VHI looks for its pose streams.</summary>
	public enum ConnectionMode { ThisComputer, Peer, Network }

	/// <summary>The Connection setting this process applied. liblsl reads its config once,
	/// at first use, so a changed setting only takes effect in the next process.</summary>
	public static (ConnectionMode Mode, string Peer) Connection { get; private set; }

	private const string ConnectionPath = "user://connection.cfg";

	/// <summary>The saved Connection setting, or this platform's default.</summary>
	/// <remarks>A phone searches the network: MyoGestic never runs on one. A computer looks
	/// only at itself, and is never offered the network search — that is the traffic the
	/// crashes below came from — so a stored <c>Network</c> falls back to it there.</remarks>
	public static (ConnectionMode Mode, string Peer) LoadConnection()
	{
		bool phone = OS.GetName() == "Android";
		var fallback = phone ? ConnectionMode.Network : ConnectionMode.ThisComputer;
		var cfg = new ConfigFile();
		if (cfg.Load(ConnectionPath) != Error.Ok)
			return (fallback, "");
		string peer = cfg.GetValue("connection", "peer", "").AsString();
		if (!Enum.TryParse(cfg.GetValue("connection", "mode", "").AsString(), out ConnectionMode mode)
			|| (mode == ConnectionMode.Peer && !IsValidPeer(peer))
			|| (mode == ConnectionMode.Network && !phone))
			mode = fallback;
		return (mode, peer);
	}

	public static void SaveConnection(ConnectionMode mode, string peer)
	{
		var cfg = new ConfigFile();
		cfg.SetValue("connection", "mode", mode.ToString());
		cfg.SetValue("connection", "peer", peer);
		cfg.Save(ConnectionPath);
	}

	/// <summary>An IPv4 address or a host name: what liblsl's <c>KnownPeers</c> can reach with
	/// IPv6 off, and nothing that could break out of the config line it is written into.</summary>
	public static bool IsValidPeer(string peer) =>
		Uri.CheckHostName(peer) is UriHostNameType.IPv4 or UriHostNameType.Dns;

	/// <summary>
	/// Point liblsl at a config this process wrote from the Connection setting, unless
	/// <c>$VHI_LSLAPICFG</c> names one.
	/// </summary>
	/// <remarks>
	/// <para>liblsl enumerates the machine's interfaces once, at first use, then binds a
	/// multicast responder per group per interface address and sends every resolve out over
	/// all of them. On a lab Mac with Tailscale or eduVPN up that is half a dozen <c>utun</c>s
	/// besides Wi-Fi, and it fails three ways at once. A discovery reply can come back from a
	/// tunnel address, which the inlet then never connects to — measured 2026-09-12: loopback
	/// and Wi-Fi accept in milliseconds, every tunnel address hangs. A tunnel going down leaves
	/// sockets on a dead interface, and every resolve after that throws ("internal error")
	/// until the process restarts. And the per-interface fan-out under a resolve loop is what
	/// wedges configd: the machine panicked on 2026-07-31 and again on 2026-09-12. The repo's
	/// <c>lsl_api.cfg</c> holds the first investigation, including the Wi-Fi driver's part.</para>
	/// <para>Pinning discovery to the one physical interface (this code until 2.2) kept it off the
	/// tunnels but not off Wi-Fi, and a Mac panicked in its Wi-Fi driver with that pin on
	/// (2026-10-29, VHI idle-resolving). So a computer now looks only at itself:
	/// <c>ResolveScope = machine</c> keeps every query on loopback, and
	/// <c>KnownPeers = {127.0.0.1}</c> asks each local stream on its own port. Machine scope
	/// alone finds one stream per machine on liblsl 1.16/1.17: every outlet listens on the one
	/// shared port 16571, and the OS hands a unicast datagram to only one of them. Another
	/// computer is reached the same way, by unicast to its address. Only a phone searches the
	/// network, by multicast.</para>
	/// <para>The repo's <c>lsl_api.cfg</c> holds the first investigation. liblsl reads that
	/// file from the working directory, which an exported app never has, so this writes the
	/// config where the app can always find it and points liblsl at it itself.</para>
	/// </remarks>
	private static void ConfigureLiblsl()
	{
		if (OperatingSystem.IsWindows())
		{
			// Not verified on Windows, and a managed SetEnvironmentVariable does not reach the
			// CRT environment liblsl's getenv reads. The problem was diagnosed on macOS; on
			// Windows liblsl keeps its defaults unless $LSLAPICFG is set from outside.
			Connection = (ConnectionMode.Network, "");
			GD.Print("LSL: liblsl defaults (Windows); set $LSLAPICFG to override");
			return;
		}
		Connection = LoadConnection();
		string preset = System.Environment.GetEnvironmentVariable(OverrideEnv);
		string path = string.IsNullOrEmpty(preset) ? ProjectSettings.GlobalizePath("user://lsl_api.cfg") : preset;
		try
		{
			if (string.IsNullOrEmpty(preset))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, LiblslConfig(Connection.Mode, Connection.Peer));
			}
			SetNativeEnv(LiblslConfigEnv, path);
			GD.Print(string.IsNullOrEmpty(preset)
				? $"LSL: connection {Connection.Mode} {Connection.Peer} ({path})"
				: $"LSL: config from ${OverrideEnv} = {path}");
		}
		catch (Exception e)
		{
			GD.PrintErr($"⚠️ LSL: could not configure liblsl ({e.Message}) — it keeps its defaults");
		}
	}

	private static string LiblslConfig(ConnectionMode mode, string peer) => mode switch
	{
		ConnectionMode.Network => "[ports]\nIPv6 = disable\n",
		_ => "[ports]\nIPv6 = disable\n\n[multicast]\nResolveScope = machine\n\n[lab]\nKnownPeers = {"
			+ (mode == ConnectionMode.Peer ? peer : "127.0.0.1") + "}\n",
	};

	/// <summary>
	/// Every LSL stream visible on the network right now.
	/// </summary>
	/// <remarks>
	/// One network resolve, whatever the caller is after. This used to filter by name here,
	/// which read as "resolve one stream" and was not — liblsl resolved everything either
	/// way, so a caller wanting several streams paid a full resolve per name. That matters
	/// now that VHI subscribes to one stream per DOF: concurrent liblsl resolves kernel-
	/// panicked a machine here, so the caller matches names against one answer instead.
	/// </remarks>
	public static object[] ResolveAll(double timeout = 1.0)
	{
		if (!initialized) Initialize();

		try
		{
			// LSL.Resolve(int maxCount, double waitTime) - the simple method that works.
			var resolveMethod = lslType.GetMethod("Resolve", [typeof(int), typeof(double)]);
			if (resolveMethod == null)
			{
				GD.PrintErr("❌ Resolve method not found!");
				return [];
			}

			object result = resolveMethod.Invoke(null, [1024, timeout]);
			return result == null ? [] : (object[])result;
		}
		catch (TargetInvocationException e)
		{
			// Get the inner exception for better error info
			if (e.InnerException != null)
			{
				GD.PrintErr($"❌ LSL Resolve failed: {e.InnerException.Message}");
				if (e.InnerException.InnerException != null)
				{
					GD.PrintErr($"   Root cause: {e.InnerException.InnerException.Message}");
				}
			}
			else
			{
				GD.PrintErr($"❌ LSL Resolve failed: {e.Message}");
			}
			return [];
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ LSL Resolve failed: {e.Message}");
			GD.PrintErr($"   Exception type: {e.GetType().Name}");
			return [];
		}
	}

	/// <summary>
	/// Create a StreamInfo object
	/// </summary>
	public static object CreateStreamInfo(string name, string type, int channelCount,
		double nominalSrate, string channelFormat, string sourceId)
	{
		if (!initialized) Initialize();

		try
		{
			// Get the ChannelFormat enum value
			object formatValue = Enum.Parse(channelFormatType, channelFormat);

			// Create StreamInfo
			var constructor = streamInfoType.GetConstructor([typeof(string), typeof(string), typeof(int), typeof(double), channelFormatType, typeof(string)]);

			return constructor.Invoke([name, type, channelCount, nominalSrate, formatValue, sourceId]);
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to create StreamInfo: {e.Message}");
			throw;
		}
	}

	/// <summary>
	/// Create a StreamInlet from a StreamInfo
	/// </summary>
	public static object CreateStreamInlet(object streamInfo, int maxChunkLength = 0, int maxBufferLength = 360, bool recover = true)
	{
		if (!initialized) Initialize();

		try
		{
			// Use the constructor: StreamInlet(StreamInfo streamInfo, Int32 maxChunkLength, Int32 maxBufferLength, Boolean recover, TransportOptions transportOptions)
			var constructor = streamInletType.GetConstructor([streamInfoType, typeof(int), typeof(int), typeof(bool), transportOptionsType]);

			if (constructor == null)
			{
				GD.PrintErr("❌ StreamInlet constructor not found!");
				throw new Exception("StreamInlet constructor not found");
			}

			// Create a default TransportOptions object
			object transportOptions = null;
			try
			{
				// Try to create TransportOptions with default constructor
				var transportOptionsConstructor = transportOptionsType.GetConstructor(Type.EmptyTypes);
				if (transportOptionsConstructor != null)
				{
					transportOptions = transportOptionsConstructor.Invoke([]);
				}
			}
			catch
			{
				// If we can't create it, pass null
			}

			return constructor.Invoke([streamInfo, maxChunkLength, maxBufferLength, recover, transportOptions]);
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to create StreamInlet: {e.Message}");
			throw;
		}
	}

	/// <summary>
	/// Create a StreamOutlet from a StreamInfo
	/// </summary>
	public static object CreateStreamOutlet(object streamInfo, int chunkSize = 0, int maxBuffered = 360)
	{
		if (!initialized) Initialize();

		try
		{
			// Use the constructor: StreamOutlet(StreamInfo streamInfo, Int32 chunkSize, Int32 maxBuffered, TransportOptions transportOptions)
			var constructor = streamOutletType.GetConstructor([streamInfoType, typeof(int), typeof(int), transportOptionsType]);

			if (constructor == null)
			{
				GD.PrintErr("❌ StreamOutlet constructor not found!");
				throw new Exception("StreamOutlet constructor not found");
			}

			// Create a default TransportOptions object
			object transportOptions = null;
			try
			{
				// Try to create TransportOptions with default constructor
				var transportOptionsConstructor = transportOptionsType.GetConstructor(Type.EmptyTypes);
				if (transportOptionsConstructor != null)
				{
					transportOptions = transportOptionsConstructor.Invoke([]);
					GD.Print("  Created default TransportOptions");
				}
			}
			catch (Exception ex)
			{
				// If we can't create it, pass null
				GD.Print($"  Using null for TransportOptions (couldn't create: {ex.Message})");
			}

			GD.Print($"  Creating StreamOutlet with chunkSize={chunkSize}, maxBuffered={maxBuffered}");
			return constructor.Invoke([streamInfo, chunkSize, maxBuffered, transportOptions]);
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to create StreamOutlet: {e.Message}");
			throw;
		}
	}

	// Resolved once. This is called every frame for every inlet, so a GetMethod lookup here
	// is a per-frame reflection search that returns the same MethodInfo every time.
	private static MethodInfo pullSampleMethod;

	// A failing inlet fails on every frame, and it fails until something re-resolves it. One
	// console write per failure measured 220 a second and ran for as long as the app did —
	// the same message, forever, drowning the log it was meant to inform. So: say it once,
	// then at most once per interval with a count of what was skipped.
	private static readonly TimeSpan PullFailureLogInterval = TimeSpan.FromSeconds(10);
	private static DateTime lastPullFailureLog = DateTime.MinValue;
	private static long pullFailuresSinceLog;

	/// <summary>
	/// Pull a sample from a StreamInlet (non-blocking)
	/// </summary>
	/// <remarks>Returns 0.0 on failure, which ends the caller's drain loop for this frame.
	/// Failures are rate-limited rather than silenced: an inlet whose producer has gone keeps
	/// failing until the staleness clock re-resolves it, and printing each one buries
	/// everything else.</remarks>
	public static double PullSample(object inlet, float[] buffer, double timeout = 0.0)
	{
		try
		{
			pullSampleMethod ??= streamInletType.GetMethod(
				"PullSample", [typeof(float[]), typeof(double)]);
			object result = pullSampleMethod.Invoke(inlet, [buffer, timeout]);
			return (double)result;
		}
		catch (Exception e)
		{
			pullFailuresSinceLog++;
			DateTime now = DateTime.Now;
			if (now - lastPullFailureLog >= PullFailureLogInterval)
			{
				long skipped = pullFailuresSinceLog - 1;
				string also = skipped > 0 ? $" ({skipped} more since the last message)" : "";
				GD.PrintErr($"❌ PullSample failed: {e.Message}{also}");
				lastPullFailureLog = now;
				pullFailuresSinceLog = 0;
			}
			return 0.0;
		}
	}

	/// <summary>
	/// Push a sample to a StreamOutlet
	/// </summary>
	public static void PushSample(object outlet, float[] sample)
	{
		try
		{
			var pushMethod = streamOutletType.GetMethod("PushSample", [typeof(float[])]);
			pushMethod.Invoke(outlet, [sample]);
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ PushSample failed: {e.Message}");
		}
	}

	/// <summary>
	/// Get StreamInfo name
	/// </summary>
	public static string GetStreamInfoName(object streamInfo)
	{
		try
		{
			var nameProperty = streamInfoType.GetProperty("Name");
			return (string)nameProperty.GetValue(streamInfo);
		}
		catch
		{
			return "Unknown";
		}
	}

	/// <summary>
	/// Get StreamInfo channel count
	/// </summary>
	public static int GetStreamInfoChannelCount(object streamInfo)
	{
		try
		{
			var channelCountProperty = streamInfoType.GetProperty("ChannelCount");
			return (int)channelCountProperty.GetValue(streamInfo);
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	/// Set channel labels in StreamInfo description XML
	/// </summary>
	public static void SetChannelLabels(object streamInfo, string[] labels)
	{
		try
		{
			var descMethod = streamInfoType.GetMethod("get_Description");
			if (descMethod == null)
			{
				GD.PrintErr("SetChannelLabels: get_Description method not found");
				return;
			}

			object xmlElement = descMethod.Invoke(streamInfo, null);
			if (xmlElement == null)
			{
				GD.PrintErr("SetChannelLabels: Description is null");
				return;
			}

			var appendChildValue = xmlElement.GetType().GetMethod("AppendChild", [typeof(string), typeof(string)]);
			var appendChildEmpty = xmlElement.GetType().GetMethod("AppendChild", [typeof(string)]);

			if (appendChildEmpty == null)
			{
				GD.PrintErr("SetChannelLabels: AppendChild method not found");
				return;
			}

			// Create <channels> element
			object channelsElement = appendChildEmpty.Invoke(xmlElement, ["channels"]);

			// For each label, create <channel><label>Name</label></channel>
			var channelAppendEmpty = channelsElement.GetType().GetMethod("AppendChild", [typeof(string)]);
			var channelAppendValue = channelsElement.GetType().GetMethod("AppendChild", [typeof(string), typeof(string)]);

			foreach (string label in labels)
			{
				object channelElement = channelAppendEmpty.Invoke(channelsElement, ["channel"]);
				var labelAppend = channelElement.GetType().GetMethod("AppendChild", [typeof(string), typeof(string)]);
				labelAppend?.Invoke(channelElement, ["label", label]);
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to set channel labels: {e.Message}");
		}
	}

	/// <summary>
	/// Add initial state/schema to StreamInfo metadata
	/// </summary>
	public static void SetStreamMetadata(object streamInfo, string fieldName, string value)
	{
		try
		{
			var descMethod = streamInfoType.GetMethod("get_Description");
			if (descMethod == null)
			{
				GD.PrintErr("SetStreamMetadata: get_Description method not found");
				return;
			}

			object xmlElement = descMethod.Invoke(streamInfo, null);
			if (xmlElement == null)
			{
				GD.PrintErr("SetStreamMetadata: Description is null");
				return;
			}

			var appendMethod = xmlElement.GetType().GetMethod("AppendChild", [typeof(string), typeof(string)]);
			if (appendMethod != null)
			{
				appendMethod.Invoke(xmlElement, [fieldName, value]);
			}
			else
			{
				GD.PrintErr("SetStreamMetadata: AppendChild method not found");
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Failed to set stream metadata: {e.Message}");
		}
	}

	/// <summary>
	/// Dispose an LSL object (StreamInlet or StreamOutlet)
	/// </summary>
	public static void Dispose(object lslObject)
	{
		if (lslObject == null) return;

		try
		{
			var disposeMethod = lslObject.GetType().GetMethod("Dispose", Type.EmptyTypes);
			if (disposeMethod != null)
			{
				disposeMethod.Invoke(lslObject, null);
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"❌ Dispose failed: {e.Message}");
		}
	}
}
