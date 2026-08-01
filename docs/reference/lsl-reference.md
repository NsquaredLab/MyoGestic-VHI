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
| Drives | the **control** hand - [while it is delivering](../concepts/control-hand-drivers.md) |
| Goes stale after | 5 s of silence (`ControlPoseStaleAfterSeconds`); the hand then returns to its own movements |
| Resolution | by name; retried every ~5 s. Optional - most setups never publish it. |

## Outlets - published by VHI

Both outlets are created at startup.

### `VHI_Control`

| | |
|---|---|
| Name | `VHI_Control` (configurable: `ControlOutletName`) |
| Type | `MyoGestic_9DVector` |
| Channels | 9 × `float32`, labelled (see layout below) |
| Nominal rate | 60 Hz |
| Source ID | `control_hand_002_standard` |
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
| 6 | `WristFlexion` | bone 0, X axis |
| 7 | `WristAbduction` | bone 0, Z axis |
| 8 | `WristRotation` | bone 0, Y axis - pronation/supination, `±179°` |

!!! info "All nine channels render"
    The wrist is bone 0, the common ancestor of all five digit chains, so turning it turns
    the whole hand. All three of its axes are driven.

    They are `0` in every *reference recording* because nothing wrote them then, which is
    not the same as being unrenderable — do not read the corpus as evidence about the rig.

!!! warning "Rotation is `±179°`, and the missing degree is deliberate"
    `GetEuler` returns angles in `(-180°, +180°]`, so `-180` and `+180` are the same
    orientation and the decode picks the positive one. At exactly `±180` the *pose* is
    correct and the **read-back inverts**: a commanded `+1` comes back as `-1` on
    `VHI_Predict` channel 8. One degree short removes the wrap entirely and the round-trip
    is exact at every value.

    There is no forearm to carry the motion, so what turns is the hand about its own long
    axis. Unlike flexion and abduction, both the range and the sign of this axis are
    **chosen** — no movement in the library touches joint 0's Y. See `Vhi.StandardPose`.

### One sign convention, on all four streams

The layout is shared and so is the **sign**. That has not always been true, and getting
it wrong inverts every joint, so it is worth stating per stream:

| Stream | Direction | Convention |
|---|---|---|
| `MyoGestic_Output` (inlet) | into VHI | **Standard** — `+1` is the direction the channel's name denotes, so `+1` on `IndexFlexion` *flexes*. |
| `MyoGestic_ControlPose` (inlet) | into VHI | **Standard**, unconditionally — see below. |
| `VHI_Control` (outlet) | out of VHI | **Standard** — `+1` flexes. It published renderer units, `-1` flexing, before 2.0. |
| `VHI_Predict` (outlet) | out of VHI | **Standard** — `+1` flexes. |

Both outlets advertise `pose_convention = "standard"` in their metadata, and the control
outlet's `source_id` is `control_hand_002_standard`, so the two conventions are
distinguishable on the wire rather than by remembering which release you are on.

### `MyoGestic_ControlPose` is standard, always

`MyoGestic_Output` had its convention *changed* in 2.0. The control-pose inlet used to be
handled differently: its convention was chosen by the client through a field on a
`Declare` handshake, defaulting to the old renderer units. Both the field and the
handshake are gone. There is one encoding now — standard, unconditionally, on both
continuous inlets — and VHI converts internally (`Vhi.StandardPose.ToRig`), so a producer
never chooses a convention. It only chooses whether to publish.

### Publishing the stream is the whole request

There is no flag, no mode and no RPC. VHI resolves the inlet by name whenever it does not
have one, and the control hand follows it whenever it is delivering:

| What you do | VHI does |
|---|---|
| Publish nothing (the default) | The control hand runs its own movement state machine — named movements from discrete DOFs, a recording trajectory, or the keyboard. |
| Publish `MyoGestic_ControlPose` | The control hand renders each sample as standard values, and keeps doing so while samples keep arriving. |
| Stop publishing | After `ControlPoseStaleAfterSeconds` (**5 s**), the hand stops any running trajectory, returns to rest, and resumes its movement state machine. VHI drops the inlet and re-resolves by name, so the next producer of that name is picked up. |

Two things worth knowing:

- **Presence is the request because an inlet nobody reads is indistinguishable from a
  stream that is not arriving.** This is exactly how the predicted hand has always
  treated `MyoGestic_Output`; the control hand needed a handshake for the same idea and
  no longer does.
- **A live stream and a discrete DOF cannot both drive the hand.** A discrete DOF renders
  as a control-hand *movement*, and a streamed pose drives the same bones. v1 arbitrated
  per command through `ControlMode`; now the stream simply wins, and a discrete DOF sent
  while it is live comes back in `ControlAck.rejected` reading `'<movement>' was refused
  — a control-pose stream is driving the control hand`. Read the ack rather than assume.

`MyoGestic_Output` and `VHI_Control` both changed convention in 2.0; see
[Upgrading to VHI 2.0](../upgrading-to-v2.md). Sessions recorded before that release are
in the old units and are converted once with `myogestic.tools.migrate_vhi_sessions`,
which stamps `pose_convention` into the session so a reader never has to guess.

!!! tip "Don't hard-code any of this"
    This table is the layout VHI *happens* to use today, and a frame built by hand from
    it goes wrong the moment it does not. Call `GetControlManifest` and build your frame
    from each capability's `stream_name` and `channel` instead. MyoGestic's `VhiTarget`
    does that — pass `stream="control_pose"` when it is driving the control hand, so it
    resolves against *that* stream's channels rather than the output stream's.

## Minimal producer

```python
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo("MyoGestic_Output", "MyoGestic_9DVector", 9, 32, "float32", "my-source")
outlet = StreamOutlet(info)
outlet.push_sample([0.0] * 9)   # all-rest; VHI's predicted hand follows it
```

Zeros are rest, which is why the example uses them. A non-zero frame is **standard** on
this stream — `[1.0, 0, 0, 0, 0, 0, 0, 0, 0]` flexes the thumb, and `-1.0` extends it.
What "flexes" means is not a matter of taste here: the gain table the renderer multiplies
by is `MovementPoses[Fist]`, the fully-closed hand, so a `+1` multiplier reproduces that
pose exactly. `Vhi.StandardPose` is the one place that rule lives, and
`tests/test_v2_contract.py` checks the rendered degrees against the movement library
rather than against a table of its own.
