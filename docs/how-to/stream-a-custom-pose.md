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
