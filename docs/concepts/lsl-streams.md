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
| `MyoGestic_ControlPose` | the **control** hand (only in [`Stream` mode](control-modes.md)) | 9 × `float32` | ~32 Hz (the producer's pace) |

The two inlets are **independent**: `MyoGestic_Output` only ever drives the
predicted hand, `MyoGestic_ControlPose` only ever drives the control hand.
(Producing `MyoGestic_ControlPose` is opt-in - most setups never publish it
and leave the control hand in `Movement` mode.)

The inlet rate is **whatever the producer pushes**. MyoGestic's default
prediction loop runs at ~32 Hz, but VHI itself doesn't impose or assume a
rate - it consumes whatever arrives, smooths between samples (see
[`SetSmoothing`](grpc-control.md)), and renders at its own physics tick.

## Outlets - what VHI publishes

| Stream name | Carries | Rate |
|---|---|---|
| `VHI_Control` | the **control** hand's current pose | 60 Hz |
| `VHI_Predict` | the **predicted** hand's current pose | 60 Hz |

These let the rest of the experiment record what VHI is actually showing - for
example, MyoGestic consumes `VHI_Control` as a regression target (the
control-hand kinematics the model should learn to reproduce). Outlets can be
switched off with the `EnableOutlets` flag.

!!! info "Input rate ≠ display rate"
    The inlets carry the **predicted-pose timeline** (whatever the model
    produces, ~32 Hz). The outlets carry the **displayed-pose timeline**
    (whatever VHI is rendering, 60 Hz). They are not the same signal: the
    outlets are an interpolated, smoothed, mode-aware view of what reached
    the screen. For frame-accurate experiment recording, treat `VHI_Predict`
    / `VHI_Control` as the canonical record of what the participant *saw*,
    and the inlets as the *cause*. Record both - LSL's XDF format is
    designed for exactly this heterogeneous-rate, multi-stream case
    (`pyxdf.load_xdf` lines them up on a common clock).

!!! warning "gRPC commands don't appear on LSL"
    Discrete commands go over [gRPC](grpc-control.md), not LSL, so they have
    no LSL timestamps and don't show up in an XDF recording. The *effect*
    of a command (a movement starts, a freeze is engaged) becomes visible
    on `VHI_Control` at the next physics tick - but rejected commands
    (`applied=false`) and exact `SetMovement`-issued instants are not in
    the LSL record. If experiment integrity requires that timeline, log
    enqueue and ack timestamps client-side (see the
    [`VhiControlClient` reference](../reference/grpc-api.md)).

!!! note "Dropped streams"
    Earlier versions also published `VHI_MovementState` and `VHI_MenuState`.
    These were removed: the commanded movement and the settings are now known
    to MyoGestic directly (it issues the commands), and the actual kinematics
    are already in `VHI_Control`.

## The 9-DOF channel layout

Every VHI pose vector - in or out - is 9 `float32` channels:

| # | Channel | Notes |
|---|---|---|
| 0 | Thumb flexion | normalised, roughly −1 … +1 |
| 1 | Thumb abduction | |
| 2 | Index flexion | |
| 3 | Middle flexion | |
| 4 | Ring flexion | |
| 5 | Pinky flexion | |
| 6 | Wrist flexion | usually 0 - wrist is not animated by default |
| 7 | Wrist abduction | usually 0 |
| 8 | Wrist rotation | usually 0 |

Values are normalised against per-joint maximum-flexion limits, so `1.0` means
"fully flexed" for that finger. VHI expands these 6 finger DOFs across the 16
animated joints internally (see [Architecture](architecture.md)).

See the [LSL reference](../reference/lsl-reference.md) for stream types,
source IDs and exact metadata.
