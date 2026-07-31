# Configuration

VHI's configurable properties are Godot `[Export]` fields - editable in the
Inspector per node, and persisted in the scene. This page lists them all, by
node.

## `LSLCommunicationController`

LSL inlets and outlets - see [LSL streams](lsl-reference.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `PredictionStreamName` | `string` | `MyoGestic_Output` | Inlet name for the predicted-hand pose stream. |
| `ControlPoseStreamName` | `string` | `MyoGestic_ControlPose` | Inlet name for the control-hand pose stream (`Stream` mode). |
| `ControlOutletName` | `string` | `VHI_Control` | Outlet name for the control-hand pose. |
| `PredictedOutletName` | `string` | `VHI_Predict` | Outlet name for the predicted-hand pose. |
| `ExpectedChannels` | `int` | `9` | Channel count for the inlets/outlets. |

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
| `DriverMode` | `ControlHandDriverMode` | `Movement` | `Movement` \| `Stream` \| `Idle` - see [Control-hand modes](../concepts/control-modes.md). |

## `PredictedHandSkeleton`

The [predicted hand](../concepts/hands.md).

| Field | Type | Default | Purpose |
|---|---|---|---|
| `EnableSmoothing` | `bool` | `false` | Spherically interpolate toward the incoming pose instead of snapping. |
| `SmoothingSpeed` | `float` | `5.0` | Interpolation speed when `EnableSmoothing` is on. |

!!! tip "Set at runtime, too"
    `Frequency`, `HoldTime`, `RestTime` and smoothing can also be changed live
    from the in-app control panel, or over gRPC (`SetSpeed`, `SetPresentation`).
    The driver mode has no control-panel toggle - it is set in the Inspector or
    over gRPC (the driver mode).
