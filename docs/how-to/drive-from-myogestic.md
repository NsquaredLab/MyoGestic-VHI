# Drive VHI from MyoGestic

MyoGestic ships a small client for VHI's [gRPC control plane](../concepts/grpc-control.md).
This guide shows the whole loop: launch VHI, command the control hand, read
its state back.

## Setup

The client lives behind the `[grpc]` optional dependency:

```bash
uv sync --extra grpc          # adds grpcio, grpcio-tools, protobuf
```

The `virtual_hand()` registry entry knows where VHI lives and which port its
gRPC server is on. It resolves, in order: explicit arguments → environment
variables → defaults.

| What | Argument | Env var | Default |
|---|---|---|---|
| Godot binary | `godot_bin=` | `GODOT_BIN` | `/Applications/Godot.app/Contents/MacOS/Godot` |
| VHI project path | `vhi_path=` | `VHI_PATH` | `<repo>/tools/MyoGestic-VHI` |
| gRPC host | `grpc_host=` | `VHI_GRPC_HOST` | `127.0.0.1` |
| gRPC port | `grpc_port=` | `VHI_GRPC_PORT` | `50051` |

!!! tip "Point VHI_PATH at your checkout"
    The default `tools/MyoGestic-VHI` is git-ignored. Symlink your
    VHI checkout there, or set `VHI_PATH`.

## Launch VHI and command it

```python
import pathlib
import tomllib

from myogestic.controls import ControlBus, load_control_map, resolve
from myogestic.renderer import RendererTarget
from myogestic.vhi import virtual_hand
from myogestic.widgets import ProcessLauncher

vhi = virtual_hand()

# 1. Launch VHI as a managed subprocess (godot --path ...).
processes = ProcessLauncher([*vhi.launcher()])       # call .ui() each frame

# 2. Open the control client. Fire-and-forget; never blocks the GUI.
client = vhi.control_client()
recording = vhi.recording_client()

# 3. Say what you control, in your own names, pointed at addresses VHI publishes.
control_map = load_control_map({
    "dofs": {
        "my_index": "vhi.prediction.index",
        "gesture": {"target": "vhi.control.gesture", "debounce_s": 0.1},
    }
})

# 4. Resolve against what VHI reports — this is where the semantics arrive, so it
#    needs VHI *running*. An app that launches it from a button resolves in the
#    handler, not at import.
controls = resolve(control_map, client.capabilities())
bus = ControlBus(controls, targets=[RendererTarget(client=client, interface=vhi)], hz=32)

# 5. Command by your own names.
bus.push({"my_index": 0.8})              # a number, onto the predicted hand
bus.select("gesture", "Fist")            # a held state: snap to the pose, hold it
recording.start_trajectory("Index")      # a trajectory, for recording data
recording.set_recording_session(True)    # recording live — VHI ignores its keyboard
```

Commands are **fire-and-forget**: each call enqueues onto a daemon thread and
returns immediately, so a 60 fps GUI never stalls on the network. The worker
issues the unary RPC and logs the acknowledgement.

!!! warning "VHI 2.0 or newer"
    There is no fallback. `RendererTarget` asks the renderer which controls it exports and
    refuses to guess, so a pre-2.0 build — which has no manifest — is reported as
    unsupported rather than driven. MyoGestic's installer and launcher both check the
    version; run a checkout from source with `$VHI_PATH` and `$GODOT_BIN` if you have
    no 2.x release.

    `RendererTarget` also reads `vocabulary_version` off the manifest and **refuses anything
    below `2`**, loudly, at bind. Vocabulary 2 is the one in which every DOF is its own
    single-channel stream named for its address; a renderer still reporting `1` expects
    the retired `stream_name`/`channel` manifest, and driving it would leave the hand
    still with nothing anywhere logging why.

## Discover what VHI exports

`capabilities()` is the whole vocabulary, and it is VHI's to declare: every address,
its kind, its range, its states. Nothing on the MyoGestic side hard-codes any of it,
so a build that grows a control needs no client change.

```python
for cap in client.capabilities() or ():
    print(cap.address, cap.kind, cap.lo, cap.hi, cap.rest)
```

A capability describes what it renders and nothing about the wire, because the wire needs
no describing: a continuous control's **address is the name of its LSL stream**, one
`float32` channel wide, and a discrete one is a held state that drives no stream at all.
There is no `stream_name` and no `channel` field to read — `cap.kind` is the whole of that
distinction.

## Query control-hand state

`recording.state()` is the one **synchronous** call — use it on connect or an
explicit refresh, not every frame:

```python
state = recording.state()
if state is not None:                       # None == VHI not reachable
    print(list(state.available_movements))  # names a discrete state may resolve to
    print(state.current_movement)
    print(state.trajectory_running)
```

Discovering `available_movements` this way means you never hard-code the
movement set - see [Movements](../concepts/movements.md).

## Clean up

```python
bus.stop()         # rest the controls and flush, so the hand releases
client.stop()      # stop the worker thread, close the channel
```

## Worked examples

Two runnable examples in the MyoGestic repo wire this into a full GUI:

- `examples/synthetic/vhi_playground.py` - **start here.** A slider per control in a
  TOML map, straight to the hand, next to an editor for the file. No model, no EMG.
- `examples/synthetic/emg_classification_grpc.py` - a classifier whose output drives
  the control hand with a discrete DOF, plus a live **movement palette**.
- `examples/synthetic/emg_regression.py` - continuous regression onto the predicted
  hand, with a training program driving the control hand for data collection.

Run one with:

```bash
uv run --extra grpc python examples/synthetic/vhi_playground.py
```
