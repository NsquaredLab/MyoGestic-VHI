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
`WebApplication` hosting **two** services on the same port:
`VhiCanonicalControl` (control) and `VhiTrainingAid` (recording).
It:

- starts in `_Ready()` on `127.0.0.1:<GrpcPort>` (default **50051**, HTTP/2
  cleartext),
- stops in `_ExitTree()`,
- marshals every RPC onto Godot's main thread (see [Architecture](architecture.md)).

VHI is the **server**; MyoGestic is the **client**. The client opens unary
calls; the return value *is* the acknowledgement.

## The contract

The contract is `proto/myogestic_vhi_v2.proto` in this repo — the canonical
source. MyoGestic vendors a copy and regenerates its Python stubs from it.

DOFs are addressed **by name**, and `Declare` negotiates which ones this hand can
render, so neither side hard-codes a channel index.

!!! warning "The legacy `VhiControl` service has been removed"
    `proto/myogestic_vhi.proto` and its service are gone. They spoke in movement
    names and a nine-float pose whose channel meaning lived nowhere — channels 6-8
    were dead on both ends for years without anything noticing, and MyoGestic's own
    tables documented channel 1 wrongly.

    A client that still speaks v1 now gets `UNIMPLEMENTED`, which is exactly the
    signal v2's `Declare` handshake reads to recognise a build it cannot negotiate
    with. Nothing degrades silently.

    Its capabilities went to three different places, because they were three
    different kinds of thing: `SetMovement` became a canonical **discrete DOF**
    (a held state), `SetSessionActive` and movement cycling became the
    **recording aid**, and `SetSmoothing` became `SetPresentation` — a renderer
    presentation setting. `Freeze`, `SetSpeed`, `SetChirality` and `SetControlMode`
    were transport concepts with no consumer and were not replaced. `GetState`'s
    only real job, discovering movement names, is `GetTrainingState`.

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
    flex**. The inlet now takes `CANONICAL` values; a client that receives
    `ENCODING_UNSPECIFIED` must fall back rather than assume.

The `VhiCanonicalControl` service, at a glance:

| RPC | Purpose |
|---|---|
| `Declare` | negotiate a control space by name; returns per-DOF verdicts, the channel order, and how to encode it |
| `SetControl` | command one canonical frame — continuous values and discrete states |
| `SweepControl` | drive one DOF across its range and report which bones moved, in signed degrees |
| `SetPresentation` | renderer blending (appearance only — layer 3 of three) |

`SetControl` returns a `ControlAck { applied, rejected }`, where `rejected` maps a
DOF name to the reason it was refused. A refusal is always *named*: a DOF that
cannot be rendered is reported, never silently dropped, because a dropped joint
looks exactly like a joint that is working and holding still.

See the [gRPC API reference](../reference/grpc-api.md) for every message and
field, including the full `.proto`.
