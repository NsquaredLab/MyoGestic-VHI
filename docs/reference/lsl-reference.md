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

## The channel layout

**This table is the authoritative map.** It was read out of VHI's own consumers
(`PredictedHandSkeleton` / `ControlHandSkeleton`) and confirmed against recorded
sessions. Do not restate it from memory — earlier descriptions of this stream had
channel 0 as thumb *rotation* and channels 6-8 as a wrist, and neither was true.

| # | Channel label | Notes |
|---|---|---|
| 0 | `ThumbFlexion` | bones 1/2/3, X axis |
| 1 | `ThumbAbduction` | bones 1/2/3, Z axis. The distal bone's Z gain is `0`, so only two of the three move. |
| 2 | `IndexFlexion` | bones 4, 5, 6 |
| 3 | `MiddleFlexion` | bones 7, 8, 9 |
| 4 | `RingFlexion` | bones 10, 11, 12 |
| 5 | `PinkyFlexion` | bones 13, 14, 15 |
| 6-8 | `WristFlexion`, `WristAbduction`, `WristRotation` | **Read by no consumer.** Always `0`. |

!!! danger "Channels 6-8 are dead, not merely idle"
    They are labelled for wire-compatibility only. Nothing in VHI reads them, and no
    setting animates them — they are `0` in every reference recording because they can
    only ever be `0`. There is no wrist on this rig.

    They are also absent from `DeclareReply.continuous_channel_order`: v2 will not name
    a channel it does not read, because naming a dead channel is how the wrong maps
    spread in the first place.

### Two sign conventions, and which stream uses which

The layout is shared, the **sign is not**. Getting this wrong inverts every joint, so it
is worth stating per stream:

| Stream | Direction | Convention |
|---|---|---|
| `MyoGestic_Output` (inlet) | into VHI | **Canonical** — `+1` is the direction the channel's name denotes, so `+1` on `IndexFlexion` *flexes*. |
| `MyoGestic_ControlPose` (inlet) | into VHI | **Renderer units** — `-1` flexes. Not part of the v2 negotiation. |
| `VHI_Control` (outlet) | out of VHI | **Renderer units** — `-1` flexes. |
| `VHI_Predict` (outlet) | out of VHI | **Renderer units** — `-1` flexes. |

`MyoGestic_Output` changed convention in 2.0; see
[Upgrading to VHI 2.0](../upgrading-to-v2.md). The outlets deliberately did **not**, so
sessions recorded before that release stay readable by the same decoder — on the
MyoGestic side that decoder is `myogestic.vhi.legacy.decode_pose`, which remains the
reader for archived kinematics.

!!! tip "Don't hard-code any of this"
    A frame built by hand from this table is correct for exactly one of the two
    conventions and silently inverted on the other. Call `Declare` and honour
    `continuous_channel_order` and `continuous_encoding`; treat
    `ENCODING_UNSPECIFIED` as "cannot negotiate" rather than guessing. MyoGestic's
    `VhiTarget` does all of that.

    `MyoGestic_ControlPose` is the one stream with no handshake — it is opt-in, outside
    the v2 negotiation, and still expects renderer units.

## Minimal producer

```python
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo("MyoGestic_Output", "MyoGestic_9DVector", 9, 32, "float32", "my-source")
outlet = StreamOutlet(info)
outlet.push_sample([0.0] * 9)   # all-rest; VHI's predicted hand follows it
```

Zeros are rest under either convention, which is why the example uses them. A non-zero
frame is **canonical** on this stream as of 2.0 — `[1.0, 0, 0, 0, 0, 0, 0, 0, 0]` flexes
the thumb. Producers that predate 2.0 sent the negation of that.
