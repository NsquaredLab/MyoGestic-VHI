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

### 1. Switch the control hand to `Stream` mode

```python
from myogestic.interfaces import virtual_hand

vhi = virtual_hand()
client = vhi.control_client()
client.set_control_mode("STREAM")
```

In `Stream` mode the control hand reads its pose from the
`MyoGestic_ControlPose` LSL inlet. `SetMovement` / `Freeze` / `SetSpeed` are
rejected until you switch back with `client.set_control_mode("MOVEMENT")`.

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
from myogestic.interfaces import virtual_hand

vhi = virtual_hand()
client = vhi.control_client()
pose_outlet = vhi.control_outlet()

# 1. Orchestration over gRPC.
client.set_session_active(True)       # gate VHI's keyboard - MyoGestic owns the hand
client.set_control_mode("STREAM")     # control hand reads from MyoGestic_ControlPose
assert client.get_state().control_mode == "STREAM"   # sanity-check

# 2. Continuous pose injection over LSL.
LEVELS = [0.0, 0.5, 1.0]                # rest / half / full flexion per DOF
DOFS = 6                                # 6 finger DOFs; wrist held at 0
SETTLE_S = 0.5

for combo in product(LEVELS, repeat=DOFS):           # 3^6 = 729 multi-DOF poses
    pose = np.array([*combo, 0, 0, 0], dtype=np.float32)
    pose_outlet.push(pose)
    time.sleep(SETTLE_S)
    # Your model's prediction at this moment is recorded on its own LSL
    # outlet; line it up with VHI_Control post-hoc via XDF timestamps.

# 3. Tear down.
client.set_control_mode("MOVEMENT")
client.set_session_active(False)
```

Two planes, two roles - this is the whole design:

| Plane | What it carries here | Role |
|---|---|---|
| **gRPC** | `SetControlMode`, `SetSessionActive`, `GetState` | discrete setup / assertions |
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
