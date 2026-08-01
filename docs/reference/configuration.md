# Configuration

VHI's configurable properties are Godot `[Export]` fields - editable in the
Inspector per node, and persisted in the scene. This page lists them all, by
node.

## `LSLCommunicationController`

LSL inlets and outlets - see [LSL streams](lsl-reference.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `ControlOutletName` | `string` | `VHI_Control` | Outlet name for the control-hand pose read-back. |
| `PredictedOutletName` | `string` | `VHI_Predict` | Outlet name for the predicted-hand pose read-back. |
| `ExpectedChannels` | `int` | `9` | Channel count of the two **outlets**. The inlets are one channel each, not configurable, and a wider inbound stream is [refused rather than truncated](lsl-reference.md#a-stream-that-is-not-one-channel-wide-is-never-opened). |
| `PredictionStaleAfterSeconds` | `float` | `5.0` | Silence after which a prediction DOF's inlet is assumed dead and dropped. Per DOF, on its own clock. |
| `ControlPoseStaleAfterSeconds` | `float` | `5.0` | Silence after which a control-pose DOF's inlet counts as gone: it is dropped, and stops counting toward the control hand being stream-driven. When the last one goes, the hand [returns to its own movements](../concepts/control-hand-drivers.md). |

!!! note "The inlet names are not configurable"
    There is no `PredictionStreamName` or `ControlPoseStreamName` field any more. Each
    DOF's inlet is named for that DOF's address — the same name `GetControlManifest`
    publishes — so renaming one on this side would only make the manifest lie. There is
    nothing to rename on the manifest side either: a capability carries an address and no
    separate stream name, because the address *is* the name. See
    [LSL streams](lsl-reference.md#inlets-consumed-by-vhi).

## `GrpcControlServer`

The in-process [gRPC server](../concepts/grpc-control.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `GrpcPort` | `int` | `50051` | TCP port the `VhiControl` server listens on (`127.0.0.1`). |

## `ControlHandSkeleton`

The [control hand](../concepts/hands.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `ConfigFilePath` | `string` | `user://movements.toml` | Path to the [movement-pose TOML](../how-to/add-a-custom-movement.md). |
| `Frequency` | `float` | `0.5` | Movement cycles per second (the `closing`/`opening` interpolation speed). |
| `HoldTime` | `float` | `1.0` | Seconds held at max flexion in the cycle. |
| `RestTime` | `float` | `1.0` | Seconds held at rest in the cycle. |

There is no driver-mode field. What drives the control hand is decided by whether a
control-pose stream is arriving — see
[What drives the control hand](../concepts/control-hand-drivers.md).

## `PredictedHandSkeleton`

The [predicted hand](../concepts/hands.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `EnableSmoothing` | `bool` | `false` | Spherically interpolate toward the incoming pose instead of snapping. |
| `SmoothingSpeed` | `float` | `5.0` | Interpolation speed when `EnableSmoothing` is on. |

!!! tip "Set at runtime, too"
    `Frequency`, `HoldTime`, `RestTime` and smoothing can also be changed live
    from the in-app control panel. Over gRPC, `SetPresentation` sets the blending;
    the three timing values are set by `StartRecordingTrajectory`, which takes
    `frequency_hz`, `hold_time_s` and `rest_time_s` and leaves any of them alone when
    passed a non-positive (or, for the two times, negative) value. There is no
    standalone `SetSpeed` RPC.
