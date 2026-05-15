# LSL streams

VHI uses [Lab Streaming Layer](https://labstreaminglayer.org/) for all
**continuous time-series** - hand poses in and out. Discrete commands go over
[gRPC](grpc-control.md) instead.

All LSL I/O lives in `LSLCommunicationController`. Streams are resolved **by
name**; an inlet that isn't found yet is retried every few seconds, so VHI and
its peer can start in any order.

## Inlets - what VHI consumes

| Stream name | Drives | Shape |
|---|---|---|
| `MyoGestic_Output` | the **predicted** hand | 9 × `float32` |
| `MyoGestic_ControlPose` | the **control** hand (only in [`Stream` mode](control-modes.md)) | 9 × `float32` |

The two inlets are **independent**: `MyoGestic_Output` only ever drives the
predicted hand, `MyoGestic_ControlPose` only ever drives the control hand.
(Producing `MyoGestic_ControlPose` is opt-in - most setups never publish it
and leave the control hand in `Movement` mode.)

## Outlets - what VHI publishes

| Stream name | Carries | Rate |
|---|---|---|
| `VHI_Control` | the **control** hand's current pose | 60 Hz |
| `VHI_Predict` | the **predicted** hand's current pose | 60 Hz |

These let the rest of the experiment record what VHI is actually showing - for
example, MyoGestic consumes `VHI_Control` as a regression target (the
control-hand kinematics the model should learn to reproduce). Outlets can be
switched off with the `EnableOutlets` flag.

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
