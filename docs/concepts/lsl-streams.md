# LSL streams

VHI uses [Lab Streaming Layer](https://labstreaminglayer.org/) for all
**continuous time-series** - hand poses in and out. Discrete commands go over
[gRPC](grpc-control.md) instead.

All LSL I/O lives in `LSLCommunicationController`. Streams are resolved **by
name**; an inlet that isn't found yet is retried every few seconds, so VHI and
its peer can start in any order.

## Inlets - what VHI consumes

| Stream name | Drives | Shape | Rate |
|---|---|---|---|
| `MyoGestic_Output` | the **predicted** hand | 9 × `float32` | ~32 Hz (the producer's pace) |
| `MyoGestic_ControlPose` | the **control** hand ([while it is delivering](control-hand-drivers.md)) | 9 × `float32` | ~32 Hz (the producer's pace) |

The two inlets are **independent**: `MyoGestic_Output` only ever drives the
predicted hand, `MyoGestic_ControlPose` only ever drives the control hand.
(Producing `MyoGestic_ControlPose` is optional - most setups never publish it,
and the control hand plays its named movements instead.)

The inlet rate is **whatever the producer pushes**. MyoGestic's default
prediction loop runs at ~32 Hz, but VHI itself doesn't impose or assume a
rate - it consumes whatever arrives, smooths between samples (see
[`SetPresentation`](grpc-control.md)), and renders at its own physics tick.

## Outlets - what VHI publishes

| Stream name | Carries | Rate |
|---|---|---|
| `VHI_Control` | the **control** hand's current pose | 60 Hz |
| `VHI_Predict` | the **predicted** hand's current pose | 60 Hz |

These let the rest of the experiment record what VHI is actually showing - for
example, MyoGestic consumes `VHI_Control` as a regression target (the
control-hand kinematics the model should learn to reproduce). Outlets can be
always published.

!!! info "Input rate ≠ display rate"
    The inlets carry the **predicted-pose timeline** (whatever the model
    produces, ~32 Hz). The outlets carry the **displayed-pose timeline**
    (whatever VHI is rendering, 60 Hz). They are not the same signal: the
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

## The channel layout

Every VHI pose vector — in or out — is 9 `float32` channels, and **all nine are read**:
thumb flexion and abduction, one flexion channel per finger, then wrist flexion, abduction
and rotation. The wrist is bone 0, the common ancestor of all five digit chains, so it
turns the whole hand.

A stream does not have to be nine wide, either. A producer that labels its channels with
control addresses may send however many it drives, in any order, and VHI places them by
name; see [the inbound streams](#inlets-what-vhi-consumes).

Values are normalised against per-joint maximum-flexion limits, and VHI expands the six
DOFs across the 16 animated joints internally (see [Architecture](architecture.md)).

!!! important "One authoritative map, and it is not on this page"
    The exact channel-to-bone mapping and — critically — **which sign convention each
    stream uses** live in
    [the LSL reference](../reference/lsl-reference.md#the-channel-layout). This page
    deliberately does not restate them.

    That is not tidiness. This map was previously written out in several places and they
    disagreed: one described channel 0 as thumb *rotation*, another had channel 1 as `0`
    in a fist where recordings show `-1.0`. Duplicating it is how it drifts, so there is
    now one copy.

    Every stream is standard as of 2.0: `+1` is the direction the channel's name denotes,
    on the two inlets and both outlets alike. `VHI_Control` was the exception and it was a
    bug — it published the renderer's own units, opposite on five channels, so a fist read
    `-1` on the stream you train from and `+1` on the one you drive. Recordings from before
    the fix are converted by `myogestic.tools.migrate_vhi_sessions`; the outlets advertise
    `pose_convention` so a reader never has to infer which it holds.

See the [LSL reference](../reference/lsl-reference.md) for stream types,
source IDs and exact metadata.
