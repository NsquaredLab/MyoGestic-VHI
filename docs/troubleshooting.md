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
[Build and export](how-to/build-and-export.md#prerequisite-a-net-8-sdk).

## The control hand ignores a discrete DOF

**Symptom** - `SetControl` comes back with the DOF named in `rejected`.

Read the reason in `ControlAck.rejected["vhi.control.gesture"]`:

- *"no movement matches state …"* - the name isn't in the current movement set. Call
  `GetRecordingSessionState` and use a name from `available_movements`; remember the
  set depends on `Mode` (`AI` vs `Classifier`).
- *"a control-pose stream is driving the control hand"* - something is publishing at
  least one `vhi.control.pose.*` stream, and a stream and a movement cannot both own the
  bones. **Any one of the nine is enough**, so look for all of them, not just the one you
  were driving. Stop publishing every one and the hand is yours again five seconds later.
  Watch for a *stale outlet left by an earlier process* - it keeps repeating its last
  sample and looks exactly like a live producer. See
  [What drives the control hand](concepts/control-hand-drivers.md).
- *"a recording trajectory is running …"* - stop it with `StopRecordingTrajectory`
  first; a recording is being aligned against it.

## The predicted hand isn't moving

- Are streams named for the DOFs you drive actually being published?
  `vhi.prediction.index` and its siblings — the **stream name is the address**, exactly
  as `GetControlManifest` reports it in `stream_name`. Check with an LSL viewer or
  `tests/test_all_streams.py`.
- Stream names are case-sensitive and matched exactly. A typo is not an error anywhere:
  VHI simply goes on looking for a name nobody publishes.
- Look for the `✅ Connected to LSL inlet <name> (N channels)` line per DOF. VHI resolves
  by name only and does *not* reject a mismatched stream, so a wide producer is read at
  channel 0 and its other channels ignored.

## Only some fingers move

That is not a fault. Each DOF is its own stream and a DOF nobody publishes **holds what
it was last commanded to** — there is no frame, so nothing waits for the DOFs that did
not deliver. Publish the missing addresses, or push `0` on them to put them at rest;
going silent is a different statement from commanding rest.

If a DOF is stuck where a *previous* run left it, its producer is likely still alive:
an LSL outlet repeats its last sample at its own rate, so a leftover process keeps that
one DOF pinned. Kill old producers before measuring anything.

## No LSL streams found at all

- Confirm the producer is running and on the same machine/subnet.
- Check the stream **name** matches an address VHI exports — read `stream_name` off
  `GetControlManifest` rather than typing it. There is no configurable inlet name any
  more; the address *is* the name.
- LSL uses multicast for discovery - a restrictive firewall or VPN can block
  it. VHI runs one resolve every ~5 s covering every name still missing (never one per
  DOF), so starting the producer late is fine once discovery works.

## The control hand cycles when you wanted it held (or vice versa)

Two different RPCs, deliberately:

- A **discrete DOF** (`SetControl`) snaps to the movement's end pose and holds it -
  for classifier outputs.
- A **recording trajectory** (`StartRecordingTrajectory`) plays the open/close loop -
  for regression recording.

See [held state, or a swept trajectory](concepts/control-hand-drivers.md#held-state-or-a-swept-trajectory).

## The hand looks mirrored / wrong-handed

Chirality (left/right mirroring) is exposed as a control-panel toggle, but the
implementation is **currently disabled** and there is no chirality RPC. Both hands
use the left-hand FBX by default.

## C# build errors

- Run `dotnet restore` first.
- Confirm a **.NET 8 SDK** is available - the project targets `net8.0`.
- Use the **.NET build of Godot 4.6**, not the standard build.
