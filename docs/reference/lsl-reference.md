# LSL streams

Every LSL stream VHI consumes or publishes. All are managed by
`LSLCommunicationController`; see [LSL streams (concept)](../concepts/lsl-streams.md)
for the *why*.

**The two directions no longer share a shape**, so read the one you need:

- **Inbound**, into VHI: **one stream per DOF**, named for the DOF's own address and
  **one channel wide**. Eighteen of them — nine driving the predicted hand, nine driving
  the control hand. A sample is applied the moment it arrives.
- **Outbound**, out of VHI: two read-back outlets, `VHI_Control` and `VHI_Predict`, each
  publishing a whole **nine-channel** pose at 60 Hz. A read-back is a recording, and a
  recording wants one row per instant.

## Inlets - consumed by VHI

A DOF's stream name **is** its address, and that is the whole of the inbound transport
contract. The manifest carries nothing else about a wire — no stream name beside the
address, no channel number — because a capability's address already says everything a
producer needs: publish one stream under it, one `float32` channel wide. A client reads
the address off `GetControlManifest` and there is no positional layout to get wrong.

Nine drive the **predicted** hand:

```text
vhi.prediction.thumb.flexion      vhi.prediction.wrist.flexion
vhi.prediction.thumb.abduction    vhi.prediction.wrist.abduction
vhi.prediction.index              vhi.prediction.wrist.rotation
vhi.prediction.middle
vhi.prediction.ring
vhi.prediction.little
```

…and nine drive the **control** hand,
[while any of them is delivering](../concepts/control-hand-drivers.md):

```text
vhi.control.pose.thumb.flexion    vhi.control.pose.wrist.flexion
vhi.control.pose.thumb.abduction  vhi.control.pose.wrist.abduction
vhi.control.pose.index            vhi.control.pose.wrist.rotation
vhi.control.pose.middle
vhi.control.pose.ring
vhi.control.pose.little
```

Every one of them is optional. **Publish only the DOFs you drive** — nothing waits for
the rest, and a name nobody publishes costs only an unresolved lookup.

| | |
|---|---|
| Channels | **1 × `float32`**, exactly. Any other width is refused outright — see below |
| Rate | the producer's, per stream. MyoGestic's prediction loop is ~32 Hz; VHI imposes nothing |
| Resolution | by **name** only; one liblsl resolve every ~5 s covers every stream still missing, so the resolve count does not grow with the number of DOFs |
| Type | **not checked.** A stream is *found* on its name alone; the type it advertises is informational |
| Goes stale after | 5 s of silence (`PredictionStaleAfterSeconds` / `ControlPoseStaleAfterSeconds`). VHI drops that one inlet and looks for the name again, so the next producer of it is picked up |
| Nominal rate | advertised by the producer, ignored by VHI. It applies what arrives and renders at its own physics tick |

### A stream that is not one channel wide is never opened

Names find a stream; **width decides whether it is opened**. A resolved stream whose
channel count is anything other than exactly 1 is logged as an error and its inlet is not
created:

```text
❌ vhi.prediction.index is published 9 channels wide, and this contract is one address
per stream, one float32 channel. Not opening it — publish one stream per DOF, named for
the address.
```

This used to be tolerated: the inlet resized its buffer to whatever turned up and read
element zero. That quietly accepted a nine-channel whole-pose outlet from a producer too
old to know the streams had been split apart — and element zero of that frame is the
thumb, so every DOF would have rendered the thumb's value with nothing anywhere saying
so. A receiver that advertises an invariant is the thing that has to enforce it, and a
loud refusal is the only reading of a mis-shaped stream that a producer can act on.

### A DOF nobody is driving holds its last commanded value

There is no whole-pose frame and nothing waits for one. Each sample lands on its own DOF
the moment it arrives, and the DOFs that did not deliver keep what they were last
commanded to.

That is the point of the shape, not a tolerance for missing data: the DOFs are
independently actuated, may come from different producers, and may update at different
rates. **A hand whose index has moved and whose thumb has not is a real pose**, not a
corrupt or half-delivered one. Two producers — one publishing the thumb, one the index —
drive the same hand without contending or agreeing on anything.

Two consequences worth knowing:

- **A held value is not a stale value.** A DOF sits where it was put until something
  commands it again. If you want it back at rest, push a `0`; going silent is not the
  same statement.
- **Going silent releases the *control* hand entirely.** When the *last* control-pose
  stream goes quiet the hand is genuinely unclaimed: VHI stops any running recording
  trajectory, clears the whole commanded pose to rest, and gives the hand back to its own
  movements. It has to clear the pose and not just the rig — otherwise the next single
  DOF to arrive would bring a departed producer's other eight values back with it.

## Outlets - published by VHI

Both outlets are created at startup and are **unchanged** by the per-DOF inbound shape:
each publishes its hand's whole pose, all nine channels, every physics tick.

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
[movements TOML](../concepts/movements.md), and a `pose_convention` entry — see
[the sign convention](#one-sign-convention-in-both-directions).

## The nine DOFs

**This table is the authoritative map.** It was read out of `Vhi.VhiControlService`'s own
address tables and confirmed against recorded sessions. Do not restate it from memory —
earlier descriptions put thumb *rotation* where thumb flexion is, and called channels 6-8
a dead wrist when they are a live one.

Inbound, an address **is** a stream name, and that stream has exactly one channel, so
there is no index to choose. Outbound, the same DOF is a numbered channel of the
read-back. The `#` column is **the outlets'** channel number; it is not something an
inbound producer indexes.

| Address suffix | # on the outlets | Outlet channel label | Bones |
|---|---|---|---|
| `thumb.flexion` | 0 | `ThumbFlexion` | 1/2/3, X axis |
| `thumb.abduction` | 1 | `ThumbAbduction` | 1/2/3, Z axis. The distal bone's Z gain is `0`, so only two of the three move. |
| `index` | 2 | `IndexFlexion` | 4, 5, 6 |
| `middle` | 3 | `MiddleFlexion` | 7, 8, 9 |
| `ring` | 4 | `RingFlexion` | 10, 11, 12 |
| `little` | 5 | `PinkyFlexion` | 13, 14, 15 |
| `wrist.flexion` | 6 | `WristFlexion` | bone 0, X axis |
| `wrist.abduction` | 7 | `WristAbduction` | bone 0, Z axis |
| `wrist.rotation` | 8 | `WristRotation` | bone 0, Y axis - pronation/supination, `±179°` |

Prefix a suffix with `vhi.prediction.` for the predicted hand or `vhi.control.pose.` for
the control hand. The two namespaces are deliberately separate: the prediction streams
carry model output, and the control-pose streams drive the hand that output is supposed
to be the ground truth *for*.

!!! info "All nine DOFs render"
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

### One sign convention, in both directions

The shapes differ; the **sign** does not. That has not always been true, and getting it
wrong inverts every joint, so it is worth stating per direction:

| Stream | Direction | Convention |
|---|---|---|
| `vhi.prediction.*` (inlets) | into VHI | **Standard** — `+1` is the direction the DOF's *name* denotes, so `+1` on `vhi.prediction.index` *flexes*. |
| `vhi.control.pose.*` (inlets) | into VHI | **Standard**, unconditionally — see below. |
| `VHI_Control` (outlet) | out of VHI | **Standard** — `+1` flexes. It published VHI's rig units, `-1` flexing, before 2.0. |
| `VHI_Predict` (outlet) | out of VHI | **Standard** — `+1` flexes. |

The domain is `[-1, 1]` everywhere, `0` is rest, and VHI clamps to it. Both outlets
advertise `pose_convention = "standard"` in their metadata, and the control outlet's
`source_id` is `control_hand_002_standard`, so the two conventions are distinguishable on
the wire rather than by remembering which release you are on.

### The control-pose streams are standard, always

The prediction side had its convention *changed* in 2.0. The control-pose side used to be
handled differently again: its convention was chosen by the client through a field on a
`Declare` handshake, defaulting to the old rig units. Both the field and the
handshake are gone. There is one encoding now — standard, unconditionally, on every
inbound stream — and VHI multiplies into rig degrees internally by
`Vhi.StandardPose.AtPlusOne`, so a producer never chooses a convention. It only chooses
whether to publish.

### Publishing is the whole request

There is no flag, no mode and no RPC. VHI resolves each stream by name whenever it does
not have it, and the control hand follows the control-pose streams whenever **any** of
them is delivering:

| What you do | VHI does |
|---|---|
| Publish nothing (the default) | The control hand runs its own movement state machine — named movements from discrete DOFs, a recording trajectory, or the keyboard. |
| Publish *any* `vhi.control.pose.*` stream | The control hand renders each sample as a standard value on that DOF, and keeps doing so while samples keep arriving. The DOFs you do not publish hold where they are. |
| Stop publishing all of them | Five seconds (`ControlPoseStaleAfterSeconds`) after the last sample on the last live stream, the hand stops any running trajectory, returns to rest, and resumes its movement state machine. VHI drops the inlets and re-resolves by name, so the next producer of each is picked up. |

Three things worth knowing:

- **One DOF is enough to take the hand.** The control hand follows *any* live
  control-pose stream, not all nine. Requiring all nine would mean a producer that drives
  a single DOF never took the hand at all — which is the case this shape exists for. The
  falling edge is the *last* stream going quiet, which is when the hand is genuinely
  unclaimed.
- **Presence is the request because an inlet nobody reads is indistinguishable from a
  stream that is not arriving.** This is exactly how the predicted hand has always
  treated its own streams; the control hand needed a handshake for the same idea and no
  longer does.
- **A live stream and a discrete DOF cannot both drive the hand.** A discrete DOF renders
  as a control-hand *movement*, and a streamed pose drives the same bones. v1 arbitrated
  per command through `ControlMode`; now the stream simply wins, and a discrete DOF sent
  while it is live comes back in `ControlAck.rejected` reading `'<movement>' was refused
  — a control-pose stream is driving the control hand`. Read the ack rather than assume.

The pose convention and `VHI_Control` both changed in 2.0; see
[Upgrading to VHI 2.0](../upgrading-to-v2.md). Sessions recorded before that release are
in the old units and are converted once with `myogestic.tools.migrate_vhi_sessions`,
which stamps `pose_convention` into the session so a reader never has to guess.

!!! tip "Read the stream name off the manifest"
    The addresses above are what this build exports today. Call `GetControlManifest` and
    publish under each capability's `address` rather than a name copied from this page —
    a build that renames or adds a DOF then moves your client with it, and a name this
    build does not export is refused by `SetControl` with the alternatives named, instead
    of silently never resolving.

    Check `ControlManifest.vocabulary_version` while you are there. It is **`"2"`** on
    this build, and it is a gate rather than a label: one stream per DOF, named for the
    address, one channel wide. A client declares the oldest vocabulary it can drive and
    refuses anything below it, by name, at bind. Vocabulary `1` described the transport
    with per-capability `stream_name` and `channel` fields and allowed several controls to
    share one wider stream; those fields are gone and their numbers and names are
    reserved.

## Minimal producer

One outlet, one channel, named for the DOF it drives:

```python
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo(
    name="vhi.prediction.index",   # the address, straight off the manifest
    type="MyoGestic_Control",
    channel_count=1,
    nominal_srate=32,
    channel_format="float32",
    source_id="my-source:vhi.prediction.index",
)
outlet = StreamOutlet(info)
outlet.push_sample([0.0])   # rest
outlet.push_sample([1.0])   # the predicted hand's index flexes; nothing else moves
```

Drive a second DOF by creating a second outlet under its own address. Nothing links the
two: they may run at different rates, and they may live in different processes.

`0.0` is rest, which is why the example starts there. `+1.0` is **standard** — the
direction the DOF's name denotes — so `1.0` on `vhi.prediction.index` flexes the index
and `-1.0` extends it. What "flexes" means is not a matter of taste here:
`Vhi.StandardPose.AtPlusOne` is the one place that rule lives, in rig-native degrees, and
`tests/test_v2_contract.py` checks the rendered degrees against the movement library
rather than against a table of its own.

A whole fist, across the nine addresses in the order of the table above, is
`[1, -1, 1, 1, 1, 1, 0, 0, 0]` — five flexions and an *ad*ducted thumb, since abduction
is the direction that name denotes. Nine outlets, nine values; nothing requires them to
be pushed together.

`tests/test_lsl_sender.py` is a runnable version of all nine.
