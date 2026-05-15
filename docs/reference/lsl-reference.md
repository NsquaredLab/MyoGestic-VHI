# LSL streams

Every LSL stream VHI consumes or publishes. All are managed by
`LSLCommunicationController`; see [LSL streams (concept)](../concepts/lsl-streams.md)
for the *why*.

## Inlets - consumed by VHI

### `MyoGestic_Output`

| | |
|---|---|
| Default name | `MyoGestic_Output` (configurable: `PredictionStreamName`) |
| Type | `MyoGestic_9DVector` |
| Channels | 9 × `float32` |
| Drives | the **predicted** hand |
| Resolution | by name; retried every ~5 s until found |

### `MyoGestic_ControlPose`

| | |
|---|---|
| Default name | `MyoGestic_ControlPose` (configurable: `ControlPoseStreamName`) |
| Channels | 9 × `float32` |
| Drives | the **control** hand - only while it is in [`Stream` mode](../concepts/control-modes.md) |
| Resolution | by name; retried every ~5 s. Optional - most setups never publish it. |

## Outlets - published by VHI

Both outlets are created at startup unless `EnableOutlets` is `false`.

### `VHI_Control`

| | |
|---|---|
| Name | `VHI_Control` (configurable: `ControlOutletName`) |
| Type | `MyoGestic_9DVector` |
| Channels | 9 × `float32`, labelled (see layout below) |
| Nominal rate | 60 Hz |
| Source ID | `control_hand_001` |
| Carries | the **control** hand's current pose |

### `VHI_Predict`

| | |
|---|---|
| Name | `VHI_Predict` (configurable: `PredictedOutletName`) |
| Type | `MyoGestic_9DVector` |
| Channels | 9 × `float32`, labelled |
| Nominal rate | 60 Hz |
| Source ID | `predicted_hand_001` |
| Carries | the **predicted** hand's current pose |

Both outlets carry a `config_file` metadata entry pointing at the active
[movements TOML](../concepts/movements.md).

## The 9-DOF channel layout

Identical for every VHI pose stream, in and out. The outlet channels are
labelled with these names:

| # | Channel label | Notes |
|---|---|---|
| 0 | `ThumbFlexion` | normalised, ~ −1 … +1 (`1.0` = full flexion) |
| 1 | `ThumbAbduction` | |
| 2 | `IndexFlexion` | |
| 3 | `MiddleFlexion` | |
| 4 | `RingFlexion` | |
| 5 | `PinkyFlexion` | |
| 6 | `WristFlexion` | not animated by default - usually 0 |
| 7 | `WristAbduction` | usually 0 |
| 8 | `WristRotation` | usually 0 |

## Minimal producer

```python
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo("MyoGestic_Output", "MyoGestic_9DVector", 9, 32, "float32", "my-source")
outlet = StreamOutlet(info)
outlet.push_sample([0.0] * 9)   # 9-DOF pose; VHI's predicted hand follows it
```
