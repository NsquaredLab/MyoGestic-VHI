# gRPC control plane

VHI hosts an **in-process gRPC server** that MyoGestic (or any gRPC client)
uses to *command* the control hand. It runs alongside the [LSL streams](lsl-streams.md),
not instead of them.

## Why two transports

VHI deliberately splits communication by the *kind* of data:

| | **Continuous time-series** | **Discrete commands** |
|---|---|---|
| Examples | hand poses, model predictions | "play Fist", "freeze", "switch mode" |
| Transport | **LSL** | **gRPC** |
| Why | one shared clock, lands in the recording, lossy-tolerant | typed contract, per-call ack/reject, knows if the peer is reachable |

LSL is the wrong tool for a discrete command - it is fire-and-forget, untyped,
with no acknowledgement. gRPC is the wrong tool for a 60 Hz pose stream - it
has no shared clock and doesn't land in the recording. So VHI uses each for
what it is good at.

## The server

`GrpcControlServer` is a Godot `Node` that owns a minimal Kestrel
`WebApplication` hosting the `VhiControl` service. It:

- starts in `_Ready()` on `127.0.0.1:<GrpcPort>` (default **50051**, HTTP/2
  cleartext),
- stops in `_ExitTree()`,
- marshals every RPC onto Godot's main thread (see [Architecture](architecture.md)).

VHI is the **server**; MyoGestic is the **client**. The client opens unary
calls; the return value *is* the acknowledgement.

## The contract

The contract is `proto/myogestic_vhi.proto` in this repo - the canonical
source. MyoGestic vendors a copy and regenerates its Python stubs from it.

The `VhiControl` service, at a glance:

| RPC | Purpose |
|---|---|
| `SetMovement` | select a predefined movement; hold its end pose, or play the cycle |
| `Freeze` | freeze / release the control hand at its current pose |
| `SetSpeed` | adjust the movement animation timing |
| `SetSmoothing` | toggle predicted-hand smoothing |
| `SetSessionActive` | tell VHI a recording session is live (gates the keyboard) |
| `SetControlMode` | switch the control hand's [driver mode](control-modes.md) |
| `GetState` | query current state; doubles as a connection handshake and lets the client discover valid movement names |

Every command RPC returns a `CommandAck { applied, current_state,
current_movement, message }`. A command that can't be applied - an unknown
movement name, or a movement command while the hand is in `Stream` mode -
comes back with `applied = false` and a human-readable `message`.

See the [gRPC API reference](../reference/grpc-api.md) for every message and
field, including the full `.proto`.
