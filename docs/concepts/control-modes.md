# Control-hand modes

The control hand can be driven three different ways, and they would otherwise
fight over the bones every frame. A single **driver mode** decides which one
is live.

## The three modes

| Mode | Driven by | Movement commands |
|---|---|---|
| **`Movement`** *(default)* | the predefined-movement state machine - gRPC `SetMovement` + the keyboard | accepted |
| **`Stream`** | a continuous pose on the `MyoGestic_ControlPose` LSL inlet | **rejected** |
| **`Idle`** | nothing - holds the rest pose | **rejected** |

The default is `Movement`, so **nothing changes** unless a client explicitly
switches the mode. Switching is a gRPC call:

```python
client.set_control_mode("STREAM")   # "MOVEMENT" | "STREAM" | "IDLE"
```

While the hand is in `Stream` or `Idle` mode, `SetMovement`, `Freeze` and
`SetSpeed` are rejected - the `CommandAck` comes back with `applied = false`
and a message telling you to call `SetControlMode(MOVEMENT)` first. This keeps
ownership of the hand unambiguous.

`GetState` reports the current mode in its `control_mode` field, so a client
can show it and gate its own UI accordingly.

!!! tip "Switching back to Movement resets the hand"
    Switching *to* `Movement` returns the hand to its resting state; switching
    to `Idle` also resets to rest; switching to `Stream` leaves the hand where
    it is until the next streamed sample arrives.

## `cycle`: hold the end pose, or play the movement

Within `Movement` mode, `SetMovement` takes a `cycle` flag that decides *how*
the movement is shown:

| `cycle` | Behaviour | Use it for |
|---|---|---|
| `false` *(default)* | snap to the movement's **end pose** and hold it | a **classifier** output, or a manual one-shot command |
| `true` | play the open/close **cycle** - `rest → flex → hold → release`, looping | recording **regression** data, so `VHI_Control` sweeps a continuous kinematic range |

The distinction matters because the two ML workflows want different things
from the control hand:

- **Classification** produces a discrete label - the hand should *be* that
  gesture, held, so the operator and subject see an unambiguous target.
- **Regression** needs a continuous target - the hand should *move through*
  the movement so the recorded `VHI_Control` kinematics span the full range
  the model regresses against.

The keyboard ++arrow-down++ always plays the cycle - that's the original
operator-cueing affordance, unchanged.
