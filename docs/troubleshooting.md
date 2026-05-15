# Troubleshooting

Common failures and what they mean.

## The gRPC server didn't start

**Symptom** - no `✅ gRPC control server listening on 127.0.0.1:50051` line;
instead a `FileNotFoundException` for `Microsoft.AspNetCore…` or a Kestrel
error.

VHI's gRPC server needs the ASP.NET Core shared framework, which Godot's host
doesn't probe by default - `src/SharedFrameworkAssemblyLoader.cs` is the
workaround. Check that file still exists and its `[ModuleInitializer]` is
intact. Also confirm a .NET runtime is installed and discoverable.

## Exported app opens but nothing works

**Symptom** - the exported macOS `.app` runs, the window appears, but the
hands don't respond, the gRPC server never starts, and there's no console
output.

The macOS export is ad-hoc signed *with the hardened runtime*, which breaks
Godot's embedded .NET host. Re-sign without it:

```bash
codesign --force --deep --sign - VHI.app
```

Full detail in [Build and export](how-to/build-and-export.md).

## Export fails before it starts

**Symptom** - `godot --headless --export-release` fails with a
`System.Runtime` / `GodotTools` assembly-load error.

The export needs an active **.NET 8 SDK**. If your default `dotnet` is a newer
major version, pin .NET 8 with a `global.json` - see
[Build and export](how-to/build-and-export.md#prerequisite-an-active-net-8-sdk).

## The control hand ignores `SetMovement`

**Symptom** - `SetMovement` returns `applied = false`.

Check the `message` in the `CommandAck`:

- *"unknown movement"* - the name isn't in the current movement set. Call
  `GetState` and use a name from `available_movements`; remember the set
  depends on `Mode` (`AI` vs `Classifier`).
- *"control hand is in Stream/Idle mode"* - movement commands only apply in
  `Movement` mode. Call `SetControlMode("MOVEMENT")` first. See
  [Control-hand modes](concepts/control-modes.md).

## The predicted hand isn't moving

- Is a stream named `MyoGestic_Output` actually being published? Check with an
  LSL viewer or `tests/test_all_streams.py`.
- Does it have **9 float channels**? VHI sizes its sample buffer to
  `ExpectedChannels` (9) and logs the channel count on connect - it does *not*
  reject a mismatched stream, so the wrong shape just yields wrong or no
  motion. Check the `✅ Connected to LSL inlet … (N channels)` log line.
- Stream names are case-sensitive and matched exactly.

## No LSL streams found at all

- Confirm the producer is running and on the same machine/subnet.
- Check the stream **name** matches what VHI resolves (`PredictionStreamName`
  / `ControlPoseStreamName`).
- LSL uses multicast for discovery - a restrictive firewall or VPN can block
  it. VHI retries resolution every few seconds, so starting the producer late
  is fine once discovery works.

## The control hand cycles when you wanted it held (or vice versa)

That's the `cycle` flag on `SetMovement`:

- `cycle = false` (default) → snap to the end pose and hold - for classifier
  outputs.
- `cycle = true` → play the open/close loop - for regression recording.

See [Control-hand modes](concepts/control-modes.md#cycle-hold-the-end-pose-or-play-the-movement).

## The hand looks mirrored / wrong-handed

Chirality (left/right mirroring) is exposed as a control-panel toggle and a
`SetChirality` RPC, but the implementation is **currently disabled** - the
RPC returns `applied = false`. Both hands use the left-hand FBX by default.

## C# build errors

- Run `dotnet restore` first.
- Confirm a **.NET 8 SDK** is available - the project targets `net8.0`.
- Use the **.NET build of Godot 4.6**, not the standard build.
