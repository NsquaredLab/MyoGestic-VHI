# Stream a custom pose

By default the control hand plays *named* [movements](../concepts/movements.md).
When you need to drive it with an **arbitrary, runtime-generated** pose - a
data glove, another model, a generated trajectory - put it in
[`Stream` mode](../concepts/control-modes.md) and feed it a continuous LSL
stream.

## When to use this

| You have… | Use… |
|---|---|
| a fixed set of gestures you can name | [a custom movement in the TOML config](add-a-custom-movement.md) |
| a raw 9-DOF pose generated every frame, with no name | **this** - `Stream` mode |

## The two steps

### 1. Declare the stream

There is no mode RPC in v2. You ask for `Stream` mode **by declaring the stream**,
which is also where you say which convention you will send on it:

```python
from myogestic.controls import load_dofs
from myogestic.vhi import virtual_hand

vhi = virtual_hand()
client = vhi.canonical_client()
controls = load_dofs({"dofs": {"index.flexion": "continuous"}})

reply = client.declare(controls, control_pose="canonical")   # or "legacy"
assert reply is not None and reply.accepted
print(reply.control_pose_stream_name, list(reply.control_pose_channel_order))
```

`"canonical"` means `+1` is the direction each channel's name denotes. `"legacy"`
keeps the pre-2.0 renderer units (`-1` flexes) — the migration path for an existing
producer that wants the handshake without changing its numbers yet. Read
`reply.control_pose_encoding` rather than assuming your request won.

In `Stream` mode the control hand reads its pose from the `MyoGestic_ControlPose`
inlet, and discrete DOFs are rejected — which is why declaring a control-pose stream
*together with* a discrete DOF is refused at the handshake rather than per command.

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
hand applies `MyoGestic_Output`. The two inlets are independent - streaming to
the control hand does not touch the predicted hand.

## Notes

- The pose vector is the same [9-DOF layout](../concepts/lsl-streams.md) used
  everywhere in VHI: 6 finger DOFs plus 3 (usually-zero) wrist DOFs,
  normalised so `1.0` is full flexion.
- If no `MyoGestic_ControlPose` stream is being published, the control hand in
  `Stream` mode simply holds its last pose - VHI keeps retrying the inlet.
- `VHI_Control` still publishes the control hand's *actual* pose, so a streamed
  pose round-trips into the recording just like a played movement does.

## Use case: 9-DOF model-robustness validation

`Stream` mode is the right tool for **systematically testing a myocontrol
model across the full 9-DOF pose space** - including multi-DOF activations
that no single named movement covers. The pattern is:

```python
import time
from itertools import product
import numpy as np
from myogestic.controls import load_dofs
from myogestic.vhi import virtual_hand

vhi = virtual_hand()
client = vhi.canonical_client()
training_aid = vhi.training_client()
pose_outlet = vhi.control_outlet()
controls = load_dofs({"dofs": {"index.flexion": "continuous"}})

# 1. Orchestration over gRPC.
training_aid.set_recording_session(True)   # gate VHI's keyboard - MyoGestic owns the hand
reply = client.declare(controls, control_pose="canonical")   # asks for Stream mode too
assert reply is not None and reply.accepted                  # sanity-check

# 2. Continuous pose injection over LSL.
LEVELS = [0.0, 0.5, 1.0]                # rest / half / full flexion per DOF
                                        # (canonical: +1 flexes, as declared above)
DOFS = 6                                # 6 finger DOFs; wrist held at 0
SETTLE_S = 0.5

for combo in product(LEVELS, repeat=DOFS):           # 3^6 = 729 multi-DOF poses
    pose = np.array([*combo, 0, 0, 0], dtype=np.float32)
    pose_outlet.push(pose)
    time.sleep(SETTLE_S)
    # Your model's prediction at this moment is recorded on its own LSL
    # outlet; line it up with VHI_Control post-hoc via XDF timestamps.

# 3. Tear down.
training_aid.stop_program()                 # no-op unless one was started
training_aid.set_recording_session(False)
```

Two planes, two roles - this is the whole design:

| Plane | What it carries here | Role |
|---|---|---|
| **gRPC** | the driver mode, `SetRecordingSession`, `GetState` | discrete setup / assertions |
| **LSL** (`MyoGestic_ControlPose`) | the 9-DOF test poses themselves | continuous data |

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
