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
`WebApplication` hosting the **one** `VhiControl` service on that port —
control and recording-session coordination alike. It:

- starts in `_Ready()` on `127.0.0.1:<GrpcPort>` (default **50051**, HTTP/2
  cleartext),
- stops in `_ExitTree()`,
- marshals every RPC onto Godot's main thread (see [Architecture](architecture.md)).

VHI is the **server**; MyoGestic is the **client**. The client opens unary
calls; the return value *is* the acknowledgement.

## The contract

The contract is `proto/myogestic_vhi.proto` in this repo — the authoritative
source. MyoGestic vendors a copy and regenerates its Python stubs from it.

### `GetControlManifest` is the whole contract

DOFs are addressed **by name**, and `GetControlManifest` publishes every address VHI
exports along with what it can render for each — the kind, the range, the states. Neither
side hard-codes a stream layout, and neither side keeps a table the other has to be kept
in sync with.

Each streamed control is a stream of its own, named for that control's own address and
one `float32` channel wide. So **the address is the stream name**, and the manifest says
nothing else about the wire — there is nothing else to say. It used to carry a
`stream_name` and a `channel` beside every address; both are gone, because `stream_name`
always equalled `address` and `channel` was always `0`, and a field that can only repeat
its neighbour is a field two codebases can disagree about for no gain. A client publishes
under the address it read; there is no positional layout left to get wrong, and nothing
links one DOF's stream to another's.

A client calls it **once, unconditionally, before it sends anything**. There is no
per-client negotiation, no declared subset, and nothing to declare: the manifest is
the same for every client, and VHI keeps no session state about who is talking to it.
The sequence is:

1. `GetControlManifest` — one call. Check the `vocabulary_version` it reports, then map
   your own model-output names onto the addresses it lists.
2. Then either **publish** a single-channel stream named for that address, one per DOF you
   drive, or **send** `SetControl` for held states and low-rate updates. Both work
   immediately; nothing has to be opened first, and you publish only the DOFs you actually
   drive.

`SweepControl`, `SetPresentation` and the recording RPCs are optional extras on top.

### The vocabulary version is a gate, not a label

`ControlManifest.vocabulary_version` is a decimal integer, compared numerically, and this
build reports **`"2"`**. A client declares the oldest vocabulary it can drive and
**refuses** anything below it, loudly, at bind — MyoGestic declares a minimum of 2 and
will not drive a renderer reporting less.

That refusal exists because VHI and its clients are *separately installed applications*.
Upgrading one does not upgrade the other, and a version-skewed pair otherwise fails in the
quietest way this system has: an old renderer sits waiting for a wide pose stream nobody
publishes any more, logs nothing at all, and the hand simply never moves. There is no
symptom to read, so the check has to happen at the one moment both versions are on the
table.

| Vocabulary | The transport it describes |
|---|---|
| `1` | a manifest carrying `stream_name` and `channel` per capability; several controls could share one wider stream. **Retired.** |
| `2` | one stream per DOF, named for the address, one `float32` channel wide. |

Field numbers `10` and `11` are `reserved` in the `.proto`, and so are the *names*
`stream_name` and `channel` — a later field reusing either spelling would read as the old
one in JSON or text format to anything still carrying the v1 schema, which is the mistake
reserving the numbers alone does not prevent.

VHI enforces the same contract from the receiving end: a resolved LSL stream whose channel
count is not exactly 1 is logged as an error and its inlet is never opened. See
[the LSL reference](../reference/lsl-reference.md#a-stream-that-is-not-one-channel-wide-is-never-opened).

!!! warning "The legacy `VhiControl` service has been removed"
    `proto/myogestic_vhi.proto` and its service are gone. They spoke in movement
    names and a nine-float pose whose channel meaning lived nowhere — channels 6-8
    were dead on both ends for years without anything noticing, and MyoGestic's own
    tables documented channel 1 wrongly.

    A client that still speaks v1 now gets `UNIMPLEMENTED`. That is also what a
    current client sees when it calls `GetControlManifest` against a build too old to
    answer, which is how it recognises a renderer it cannot drive. Nothing degrades
    silently.

    Its capabilities went to three different places, because they were three
    different kinds of thing: `SetMovement` became a standard **discrete DOF**
    (a held state), `SetSessionActive` and movement cycling became the
    **recording-session RPCs**, and `SetSmoothing` became `SetPresentation` — a
    renderer presentation setting. `Freeze`, `SetSpeed`, `SetChirality` and
    `SetControlMode` were transport concepts with no consumer and were not
    replaced. `GetState`'s only real job, discovering movement names, is
    `GetRecordingSessionState`.

### Smoothing is three layers, not one

`SetPresentation` (on `VhiControl`) configures the renderer's visual
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
looks deliberate. Blending is a renderer setting a client *writes* and never reads
back — there is no field anywhere reporting whether it is on, precisely so nothing
can be built on top of it.

### Recording-session RPCs — coordination, not control

Not control, and grouped apart within `VhiControl` on purpose. A standard discrete DOF
is a **held state**: an application asks for a grip and the hand holds a grip.
Collecting regression training data wants the opposite — a control hand that keeps
*moving*, so the recorded `VHI_Control` stream sweeps a continuous kinematic range for
EMG windows to be aligned against. Folding that into the discrete vocabulary would have
made "grip" mean "grip, unless someone is recording", which is how a control standard
rots. They live in the same service as control because both drive the same control
hand through the same state machine — splitting them into a second service implied an
independence the renderer does not have.

So these RPCs carry the two things that belong to a recording *session* rather than to
the thing being controlled:

| RPC | Does |
|---|---|
| `SetRecordingSession` | Gate VHI's local keyboard so a session has one movement source. |
| `StartRecordingTrajectory` | Cycle the control hand through a movement, producing a trajectory. |
| `StopRecordingTrajectory` | Stop it, resting the hand only if one was running. Idempotent. |
| `GetRecordingSessionState` | State, plus the movement names a trajectory may use. |

A trajectory names a VHI movement, which is fine *because* this is not standard: a
recording trajectory is allowed to be application-specific, so the standard vocabulary
never grows a concept ("sweep me for training") that no application controls.

While a trajectory runs it **owns** the control hand: `SetControl`'s discrete DOFs are
refused with a reason rather than being allowed to interrupt the trajectory a recording
is being aligned against. Continuous DOFs are unaffected — they drive the *predicted*
hand.

A live `vhi.control.pose.*` stream owns the hand the same way — any one of the nine is
enough — and refuses the same commands for the same reason. See
[What drives the control hand](control-hand-drivers.md). Both are **command-time**
refusals carried in `ControlAck.rejected`: there is no setup call left at which a
client could be told in advance, and none would help, because the answer depends on
what is arriving at the moment of the command.

!!! warning "A negotiation that settled names but not units was not a negotiation"
    An earlier draft of this service had a `Declare` RPC, and its reply carried a
    `continuous_encoding` field so a client could ask which convention was in force.
    The first end-to-end run agreed on channel names while VHI's continuous path
    still decoded legacy units, so a standard `+1` arrived as a legacy `+1` and the
    hand **extended when it was told to flex** — exactly the class of bug an
    encoding-negotiation field invites. There is one encoding now: standard,
    unconditionally, on every continuous stream. Nothing is left to negotiate, and
    nothing is left to misread — which is why `Declare` itself is gone too.

`VhiControl`'s control-facing RPCs, at a glance:

| RPC | Purpose |
|---|---|
| `GetControlManifest` | every address VHI exports with its kind, range and states, plus the vocabulary version to gate on. Call it first |
| `SetControl` | command one standard frame — continuous values and discrete states |
| `SweepControl` | drive one DOF across its range and report which bones moved, in signed degrees |
| `SetPresentation` | renderer blending (appearance only — layer 3 of three) |

`SetControl` returns a `ControlAck { applied, rejected }`, where `rejected` maps a
DOF name to the reason it was refused. A refusal is always *named*: a DOF that
cannot be rendered is reported, never silently dropped, because a dropped joint
looks exactly like a joint that is working and holding still.

See the [gRPC API reference](../reference/grpc-api.md) for every message and
field, including the full `.proto`.
