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
`WebApplication` hosting **three** services on the same port: `VhiControl`
(v1), `VhiCanonicalControl` (v2 control), and `VhiTrainingAid` (v2 recording).
It:

- starts in `_Ready()` on `127.0.0.1:<GrpcPort>` (default **50051**, HTTP/2
  cleartext),
- stops in `_ExitTree()`,
- marshals every RPC onto Godot's main thread (see [Architecture](architecture.md)).

VHI is the **server**; MyoGestic is the **client**. The client opens unary
calls; the return value *is* the acknowledgement.

## The contract

There are two contracts, both canonical here, and MyoGestic vendors copies of
both and regenerates its Python stubs from them.

`proto/myogestic_vhi.proto` (**v1**) speaks in movement names and a nine-float
pose whose channel meaning lives nowhere.
`proto/myogestic_vhi_v2.proto` (**v2**) speaks the canonical control standard:
DOFs are addressed by name, and `Declare` negotiates which ones this hand can
render instead of either side hard-coding a channel index.

Both are served for the whole migration. A client discovers which one a build
speaks by calling v2's `Declare` — an older VHI answers `UNIMPLEMENTED`, which is
how the client knows to fall back rather than guess. v1 is removed only once
nothing speaks it.

### Smoothing is three layers, not one

`SetPresentation` (on `VhiCanonicalControl`) configures the renderer's visual
blending. It is deliberately the *third* of three separate mechanisms, and treating
any two as interchangeable is a bug:

| Layer | Where | Applies to | Authoritative? |
|---|---|---|---|
| 1. Continuous smoothing | MyoGestic's `ControlBus`, before any target | continuous DOFs | **yes** — sets the commanded value |
| 2. Debounce + hysteresis | MyoGestic, declared on the DOF | discrete DOFs | **yes** — sets *when* a state changes |
| 3. Presentation blending | **here**, in the renderer | how a value looks | no — appearance only |

A discrete control is never numerically low-pass filtered as though it were an axis:
averaging "rest" and "fist" interpolates through states nobody selected. A noisy
classifier needs a stability gate, which is layer 2 and lives on the MyoGestic side.

Layer 3 is worth having — a hand that snaps between poses looks wrong — but it cannot
make an unstable prediction stable. A build with blending on and no debounce still
jumps between states; it just does so smoothly, which is arguably worse because it
looks deliberate. `DeclareReply.blends_presentation` reports whether blending is on so
a client can *see* layer 3, never so it can rely on it.

### `VhiTrainingAid` — recording, not control

A separate service, and the separation is the point. A canonical discrete DOF is a
**held state**: an application asks for a grip and the hand holds a grip. Collecting
regression training data wants the opposite — a control hand that keeps *moving*, so
the recorded `VHI_Control` stream sweeps a continuous kinematic range for EMG windows
to be aligned against. Folding that into the discrete vocabulary would have made
"grip" mean "grip, unless someone is recording", which is how a control standard rots.

So the aid carries the two things that belong to a recording *session* rather than to
the thing being controlled:

| RPC | Does |
|---|---|
| `SetRecordingSession` | Gate VHI's local keyboard so a session has one movement source. |
| `StartTrainingProgram` | Cycle the control hand through a movement, producing a trajectory. |
| `StopTrainingProgram` | Stop it and rest the hand. Idempotent. |
| `GetTrainingState` | State, plus the movement names a program may use. |

A program names a VHI movement, which is fine *because* this is not canonical: a
recording aid is allowed to be application-specific, so the canonical vocabulary never
grows a concept ("sweep me for training") that no application controls.

While a program runs it **owns** the control hand: `SetControl`'s discrete DOFs are
refused with a reason rather than being allowed to interrupt the trajectory a recording
is being aligned against. Continuous DOFs are unaffected — they drive the *predicted*
hand.

!!! warning "A negotiation that settles names but not units is not a negotiation"
    `DeclareReply.continuous_encoding` says how to encode values on the LSL
    stream, and it is not optional. The first end-to-end v2 run agreed on channel
    names while VHI's continuous path still decoded legacy units, so a canonical
    `+1` arrived as a legacy `+1` and the hand **extended when it was told to
    flex**. VHI reports `LEGACY_NEGATED` until that decoder is gone; a client that
    receives `ENCODING_UNSPECIFIED` must fall back rather than assume.

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
