# LSL streams

VHI uses [Lab Streaming Layer](https://labstreaminglayer.org/) for all
**continuous time-series** - hand poses in and out. Discrete commands go over
[gRPC](grpc-control.md) instead.

All LSL I/O lives in `LSLCommunicationController`. Streams are resolved **by
name**; an inlet that isn't found yet is retried every few seconds, so VHI and
its peer can start in any order.

## Inlets - what VHI consumes

**One stream per DOF.** Every control VHI can be driven with is its own LSL
stream, named for its own address and **one channel wide**:

| Stream family | Drives | Shape | Rate |
|---|---|---|---|
| `vhi.prediction.*` - nine streams | the **predicted** hand | 1 × `float32` each | the producer's pace (~32 Hz from MyoGestic) |
| `vhi.control.pose.*` - nine streams | the **control** hand ([while any is delivering](control-hand-drivers.md)) | 1 × `float32` each | the producer's pace |

The two families are **independent**: `vhi.prediction.*` only ever drives the
predicted hand, `vhi.control.pose.*` only ever drives the control hand. Every
individual stream is optional too - producing the control-pose family at all is
unusual, and most setups let the control hand play its named movements instead.

The stream name *is* the address `GetControlManifest` publishes, so a client
reads the name it must publish under rather than agreeing on a layout. See
[the LSL reference](../reference/lsl-reference.md#inlets-consumed-by-vhi) for
all eighteen names.

The inlet rate is **whatever the producer pushes**, per stream. MyoGestic's
default prediction loop runs at ~32 Hz, but VHI itself doesn't impose or assume
a rate - it applies whatever arrives, optionally blends toward it (see
[`SetPresentation`](grpc-control.md)), and renders at its own physics tick.

### Nothing waits for a whole pose

A sample is applied to the hand the moment it arrives, and a DOF nobody is
driving holds what it was last commanded to. There is no frame to fill in and
nothing that blocks on one.

That is the reason for the shape rather than a concession to missing data. The
DOFs are independently actuated, may come from different producers, and may
update at different rates, so **a hand whose index has moved and whose thumb has
not is a real pose**, not a corrupt one. Two producers can each own some DOFs of
the same hand without contending — a model driving the fingers and a data glove
driving the wrist, say — which is the capability this shape exists for.

The corollary: pushing `0` is how you put a DOF back at rest. Going silent is a
different statement, and on the control hand the *last* stream going silent
releases the whole hand back to its own movements.

## Outlets - what VHI publishes

| Stream name | Carries | Rate |
|---|---|---|
| `VHI_Control` | the **control** hand's current pose | 60 Hz |
| `VHI_Predict` | the **predicted** hand's current pose | 60 Hz |

Each outlet carries its hand's **whole nine-channel pose**, and that has not
changed with the per-DOF inlets: a read-back is a recording, and a recording
wants one row per instant rather than nine independently timed ones.

These let the rest of the experiment record what VHI is actually showing - for
example, MyoGestic consumes `VHI_Control` as a regression target (the
control-hand kinematics the model should learn to reproduce). Both outlets are
always published.

!!! info "Input rate ≠ display rate"
    The inlets carry the **commanded timeline**, one per DOF, at whatever rate
    each producer pushes (~32 Hz for a MyoGestic model). The outlets carry the
    **displayed-pose timeline** (whatever VHI is rendering, 60 Hz). They are not the same signal: the
    outlets are an interpolated, smoothed, mode-aware view of what reached
    the screen. For frame-accurate experiment recording, treat `VHI_Predict`
    / `VHI_Control` as the authoritative record of what the participant *saw*,
    and the inlets as the *cause*. Record both - LSL's XDF format is
    designed for exactly this heterogeneous-rate, multi-stream case
    (`pyxdf.load_xdf` lines them up on a common clock).

!!! warning "gRPC commands don't appear on LSL"
    Discrete commands go over [gRPC](grpc-control.md), not LSL, so they have
    no LSL timestamps and don't show up in an XDF recording. The *effect*
    of a command (a movement starts, a freeze is engaged) becomes visible
    on `VHI_Control` at the next physics tick - but rejected commands
    (`applied=false`) and exact a discrete DOF-issued instants are not in
    the LSL record. If experiment integrity requires that timeline, log
    enqueue and ack timestamps client-side (see the
    [gRPC API reference](../reference/grpc-api.md)).

!!! note "Dropped streams"
    Earlier versions also published `VHI_MovementState` and `VHI_MenuState`.
    These were removed: the commanded movement and the settings are now known
    to MyoGestic directly (it issues the commands), and the actual kinematics
    are already in `VHI_Control`.

## The nine DOFs

A hand is nine degrees of freedom: thumb flexion and abduction, one flexion DOF per
finger, then wrist flexion, abduction and rotation. **All nine render.** The wrist is
bone 0, the common ancestor of all five digit chains, so it turns the whole hand.

Which nine they are is the only thing both directions share. Inbound each is a stream of
its own; outbound all nine are channels of one pose. Values are standard — `[-1, 1]`,
`+1` the direction the DOF's name denotes — and VHI expands them across the 16 animated
joints internally by per-joint gains (see [Architecture](architecture.md)).

!!! important "One authoritative map, and it is not on this page"
    The exact DOF-to-bone mapping, the outlet channel numbers, and — critically — **which
    sign convention applies** live in
    [the LSL reference](../reference/lsl-reference.md#the-nine-dofs). This page
    deliberately does not restate them.

    That is not tidiness. This map was previously written out in several places and they
    disagreed: one described channel 0 as thumb *rotation*, another had channel 1 as `0`
    in a fist where recordings show `-1.0`. Duplicating it is how it drifts, so there is
    now one copy.

    Everything is standard as of 2.0: `+1` is the direction the DOF's name denotes, on the
    inbound streams and both outlets alike. `VHI_Control` was the exception and it was a
    bug — it published the renderer's own units, opposite on five channels, so a fist read
    `-1` on the stream you train from and `+1` on the one you drive. Recordings from before
    the fix are converted by `myogestic.tools.migrate_vhi_sessions`; the outlets advertise
    `pose_convention` so a reader never has to infer which it holds.

See the [LSL reference](../reference/lsl-reference.md) for stream types,
source IDs and exact metadata.
