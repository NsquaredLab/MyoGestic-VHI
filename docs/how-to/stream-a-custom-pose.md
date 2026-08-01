# Stream a custom pose

By default the control hand plays *named* [movements](../concepts/movements.md).
When you need to drive it with an **arbitrary, runtime-generated** pose - a
data glove, another model, a generated trajectory - publish a continuous LSL
stream and [the hand follows it](../concepts/control-hand-drivers.md).

## When to use this

| You have… | Use… |
|---|---|
| a fixed set of gestures you can name | [a custom movement in the TOML config](add-a-custom-movement.md) |
| a raw 9-DOF pose generated every frame, with no name | **this** - a control-pose stream |

## The two steps

### 1. Read the manifest for the channels you write to

There is nothing to declare and no mode to request. The one thing you need from VHI is
which channel of `MyoGestic_ControlPose` each control lands on, and
`GetControlManifest` publishes that:

```python
from myogestic.controls import load_control_map, resolve
from myogestic.vhi import virtual_hand

vhi = virtual_hand()
client = vhi.control_client()

# See what the control hand exports, and on which channel of which stream.
for cap in client.capabilities() or ():
    if cap.address.startswith("vhi.control.pose."):
        print(cap.address, cap.stream_name, cap.channel)

# `vhi.control.pose.*` is the control hand's own namespace — distinct from
# `vhi.prediction.*`, and on its own stream. `resolve` checks your map against the
# manifest and raises on an address this build does not export, so a typo surfaces
# here rather than as a joint that never moves.
controls = resolve(
    load_control_map({"dofs": {"my_index": "vhi.control.pose.index"}}),
    client.capabilities(),
)
```

Hand `controls` to a `ControlBus` with `VhiTarget(vhi.control_outlet(),
client=client, stream="control_pose")` if you want the bus to place your values on the
right channels for you — or build the frame yourself, as below.

Both pose streams number their channels from zero, so read `stream_name` alongside
`channel` — `vhi.prediction.index` and `vhi.control.pose.index` are both channel 2, on
different streams and different hands.

There is one convention on this stream: standard values, where `+1` is the direction
each channel's name denotes. VHI converts them to its own rig units internally, so a
producer never chooses an encoding — it only chooses whether to publish.

### 2. Push poses to the `control_outlet`

`InterfaceSpec.control_outlet()` gives you an LSL outlet wired to the
`MyoGestic_ControlPose` stream (9 channels, 32 Hz):

```python
import numpy as np

pose_outlet = vhi.control_outlet()

# Push a 9-DOF pose every frame. Channel layout (see LSL streams):
#   [thumb_flex, thumb_abd, index, middle, ring, pinky, wrist_flex, wrist_abd, wrist_rot]
pose = np.array([0, 0, 0.5, 0.5, 0.5, 0.5, 0, 0, 0], dtype=np.float32)
pose_outlet.push(pose)
```

The control hand applies whatever is on the stream, exactly like the predicted
hand applies `MyoGestic_Output`. That is the whole activation: the hand renders the
stream while samples keep arriving, and no call anywhere turns it on. The two inlets
are independent - streaming to the control hand does not touch the predicted hand.

## Notes

- The pose vector is the same [9-DOF layout](../concepts/lsl-streams.md) used
  everywhere in VHI: 6 finger DOFs plus 3 (usually-zero) wrist DOFs,
  normalised so `1.0` is full flexion.
- **Keep pushing.** After five seconds of silence
  (`ControlPoseStaleAfterSeconds`) VHI treats the producer as gone: the hand stops any
  running trajectory, returns to rest, and resumes its own movements. Push at your
  loop rate even when the pose has not changed, exactly as you would for
  `MyoGestic_Output`.
- **Discrete DOFs are refused while you are streaming.** `SetControl` comes back with
  `applied = false` and the reason in `rejected` — two drivers, one hand. Stop
  streaming if you want named movements back.
- `VHI_Control` still publishes the control hand's *actual* pose, so a streamed
  pose round-trips into the recording just like a played movement does.

## Use case: 9-DOF model-robustness validation

A control-pose stream is the right tool for **systematically testing a myocontrol
model across the full 9-DOF pose space** - including multi-DOF activations
that no single named movement covers. The pattern is:

```python
import time
from itertools import product
import numpy as np
from myogestic.vhi import virtual_hand

vhi = virtual_hand()
client = vhi.control_client()
recording = vhi.recording_client()
pose_outlet = vhi.control_outlet()

# 1. Orchestration over gRPC. Nothing here turns the stream on — the only call is
#    the manifest, and it is a read.
CHANNEL = {
    cap.address: cap.channel
    for cap in client.capabilities() or ()
    if cap.stream_name == vhi.control_pose_stream_name
}
DIGITS = [                              # the six finger DOFs, in whatever order
    "vhi.control.pose.thumb.flexion",   # you like — CHANNEL places them
    "vhi.control.pose.thumb.abduction",
    "vhi.control.pose.index",
    "vhi.control.pose.middle",
    "vhi.control.pose.ring",
    "vhi.control.pose.little",
]
recording.set_recording_session(True)   # gate VHI's keyboard - MyoGestic owns the hand

# 2. Continuous pose injection over LSL. The first sample is the activation.
LEVELS = [0.0, 0.5, 1.0]                # rest / half / full flexion per DOF
                                        # (standard: +1 flexes)
SETTLE_S = 0.5                          # well inside the 5 s stale window

for combo in product(LEVELS, repeat=len(DIGITS)):    # 3^6 = 729 multi-DOF poses
    pose = np.zeros(9, dtype=np.float32)             # wrist channels stay at rest
    for address, level in zip(DIGITS, combo):
        pose[CHANNEL[address]] = level
    pose_outlet.push(pose)
    time.sleep(SETTLE_S)
    # Your model's prediction at this moment is recorded on its own LSL
    # outlet; line it up with VHI_Control post-hoc via XDF timestamps.

# 3. Tear down. Stop pushing and the hand releases itself five seconds later.
recording.stop_trajectory()             # no-op unless one was started
recording.set_recording_session(False)
```

`SETTLE_S` is half a second, comfortably inside the five-second stale window, so the
hand stays stream-driven for the whole sweep without anything holding it there. And no
channel index is written down anywhere: `CHANNEL` comes from the manifest, so a build
that renumbers its pose layout moves this loop with it.

Two planes, two roles - this is the whole design:

| Plane | What it carries here | Role |
|---|---|---|
| **gRPC** | `GetControlManifest` (the channels), `SetRecordingSession`, `GetRecordingSessionState` | discovery, setup, assertions |
| **LSL** (`MyoGestic_ControlPose`) | the 9-DOF test poses themselves | continuous data, and the only thing that activates the hand |

Two LSL records line everything up offline:

- `VHI_Control` is the **ground truth** the participant saw - record this and
  align it to your model's prediction outlet via [XDF](../concepts/lsl-streams.md).
- `MyoGestic_ControlPose` (your test injection) and the model's output are
  what you compare. Robust models track the injected pose with bounded error
  across all combos; brittle models fail on combos no single named movement
  covers.

Edge cases the named-movement set can't reach - "thumb half-flexed + index
fully flexed + everything else at rest", say - are exactly what this loop
exposes.
