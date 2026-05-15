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
from myogestic.interfaces import virtual_hand
from myogestic.widgets import process_launcher

vhi = virtual_hand()

# 1. Launch VHI as a managed subprocess (godot --path ...).
#    In an app, wire vhi.launcher() into a process_launcher widget.
process_launcher(vhi.launcher())

# 2. Open the gRPC control client (fire-and-forget; never blocks the GUI).
client = vhi.control_client()

# 3. Command the control hand.
client.set_movement("Fist")              # snap to the Fist end pose, hold it
client.set_movement("Index", cycle=True) # play the open/close cycle instead
client.freeze(True)                      # freeze at the current pose
client.set_session_active(True)          # recording live - VHI ignores its keyboard
```

Commands are **fire-and-forget**: each call enqueues onto a daemon thread and
returns immediately, so a 60 fps GUI never stalls on the network. The worker
issues the unary RPC and logs the acknowledgement.

## Query state

`get_state()` is the one **synchronous** call - use it on connect or an
explicit refresh, not every frame:

```python
state = client.get_state()
if state is not None:                    # None == VHI not reachable
    print(state.mode)                    # "AI" | "Classifier"
    print(list(state.available_movements))  # valid SetMovement names
    print(state.control_mode)            # MOVEMENT | STREAM | IDLE
```

Discovering `available_movements` this way means you never hard-code the
movement set - see [Movements](../concepts/movements.md).

## Clean up

```python
client.stop()      # stop the worker thread, close the channel
```

## Worked examples

Two runnable examples in the MyoGestic repo wire this into a full GUI:

- `examples/synthetic/emg_classification_grpc.py` - a classifier whose output
  drives the control hand with discrete `SetMovement` commands, plus a live
  **movement palette** of every VHI movement.
- `examples/synthetic/emg_regression.py` - uses `cycle=True` so the control
  hand sweeps a continuous range for the regression target.

Run one with:

```bash
uv run --extra examples --extra grpc python examples/synthetic/emg_classification_grpc.py
```
