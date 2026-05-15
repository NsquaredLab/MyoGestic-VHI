using Godot;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vhi;

/// <summary>
/// Hosts the in-process gRPC control server (Grpc.AspNetCore / Kestrel) that
/// MyoGestic connects to. Discrete commands (set movement, freeze, speed, ...)
/// arrive here; continuous time-series stays on LSL.
///
/// Loading the ASP.NET Core shared framework inside Godot relies on
/// SharedFrameworkAssemblyLoader (the godot#112701 workaround), which registers
/// from a [ModuleInitializer] before this node's _Ready() runs.
/// </summary>
public partial class GrpcControlServer : Node
{
    /// <summary>TCP port the in-process Kestrel server binds to on
    /// <c>127.0.0.1</c>. Default <c>50051</c>.</summary>
    [Export] public int GrpcPort = 50051;

    private WebApplication app;
    private readonly ConcurrentQueue<Action> mainThreadQueue = new();
    private volatile bool isShuttingDown = false;

    public override void _Ready()
    {
        GD.Print("=== gRPC Control Server _Ready() START ===");

        var controlHand = GetNode<ControlHandSkeleton>("/root/Main/ControlHand");
        var predictedHand = GetNode<PredictedHandSkeleton>("/root/Main/PredictedHand");

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();  // keep Godot's console clean
            builder.WebHost.ConfigureKestrel(o =>
                o.ListenLocalhost(GrpcPort, lo => lo.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(controlHand);
            builder.Services.AddSingleton(predictedHand);
            builder.Services.AddSingleton(this);

            app = builder.Build();
            app.MapGrpcService<VhiControlService>();
            app.StartAsync().Wait(5000);

            GD.Print($"✅ gRPC control server listening on 127.0.0.1:{GrpcPort}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"❌ Failed to start gRPC control server: {e.GetType().Name}: {e.Message}");
            app = null;
        }

        GD.Print("=== gRPC Control Server _Ready() COMPLETE ===");
    }

    public override void _Process(double delta)
    {
        // Drain work marshalled from gRPC handler threads onto the main thread.
        while (mainThreadQueue.TryDequeue(out var action))
            action();
    }

    /// <summary>
    /// Run <paramref name="fn"/> on Godot's main thread and return its result.
    /// Called from gRPC handler threads, which await the returned task.
    /// </summary>
    /// <typeparam name="T">Return type of <paramref name="fn"/>.</typeparam>
    /// <param name="fn">The closure to execute. Runs synchronously on Godot's
    /// main thread on the next <c>_Process</c> tick; any exception it throws
    /// is surfaced through the returned task.</param>
    /// <returns>A task that completes with <paramref name="fn"/>'s return
    /// value once it has run on the main thread.</returns>
    public Task<T> InvokeOnMainThread<T>(Func<T> fn)
    {
        if (isShuttingDown)
            return Task.FromException<T>(new InvalidOperationException("VHI is shutting down"));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(fn()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public override void _ExitTree()
    {
        // Do NOT call app.StopAsync() or block waiting for Kestrel to drain.
        //
        // On macOS, gracefully stopping the ASP.NET Core host while Godot's
        // SceneTree teardown is in flight triggers a fatal race: a thread-pool
        // worker spawned by Kestrel's drain logic ends up in Environment.Exit
        // while Godot's main thread is mid-_propagate_exit_tree, and the two
        // simultaneous destructor paths fight over Godot's global StringName
        // mutex - throwing std::system_error and aborting the process.
        //
        // The pragmatic fix is to skip the graceful shutdown entirely:
        // macOS's process reaper will reclaim port 50051 + the listener
        // threads when this process dies. We just drop the reference so the
        // GC and the runtime exit handlers have less to coordinate.
        isShuttingDown = true;
        app = null;
        GD.Print("gRPC control server: skipping graceful StopAsync (process exit will reclaim)");
    }
}
