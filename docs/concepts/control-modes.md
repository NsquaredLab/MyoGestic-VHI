# Control-hand modes

The control hand can be driven three different ways, and they would otherwise
fight over the bones every frame. A single **driver mode** decides which one
is live.

## The three modes

| Mode | Driven by | Movement commands |
|---|---|---|
| **`Movement`** *(default)* | the predefined-movement state machine - canonical discrete DOFs + the keyboard | accepted |
| **`Stream`** | a continuous pose on the `MyoGestic_ControlPose` LSL inlet | **rejected** |
| **`Idle`** | nothing - holds the rest pose | **rejected** |

The default is `Movement`, so **nothing changes** unless a client asks for
something else.

There is no mode RPC in v2. `Stream` mode is requested **by declaring the
stream** — `DeclareRequest.control_pose_encoding` — because an inlet nobody
reads is indistinguishable from a stream that is not arriving, so declaring
that you will send one *is* the request:

```python
# MyoGestic
vhi.canonical_client().declare(controls, control_pose="canonical")
```

See [Stream a custom pose](../how-to/stream-a-custom-pose.md) for the whole
flow, and [the LSL reference](../reference/lsl-reference.md#myogestic_controlpose-is-negotiated-not-fixed)
for the conventions.

While the hand is in `Stream` or `Idle` mode, discrete DOFs are rejected — the
`ControlAck` names the DOF in its `rejected` map with the mode it is actually
in. This keeps ownership of the hand unambiguous, and it is also why declaring
a control-pose stream *together with* a discrete DOF is refused at the
handshake rather than per command.

`GetTrainingState` reports the control hand's current movement and whether a
training program is running, so a client can show that and gate its own UI.

!!! tip "Leaving Stream mode resets the hand"
    Switching *to* `Movement` returns the hand to its resting state; switching
    to `Idle` also resets to rest; switching to `Stream` leaves the hand where
    it is until the next streamed sample arrives.

## Held state, or a swept trajectory

Within `Movement` mode there are two ways the hand can show a movement, and in
v2 they are reached through two different services — deliberately, because they
are two different kinds of thing:

| What you want | How | Use it for |
|---|---|---|
| snap to the movement's **end pose** and hold it | a canonical **discrete DOF** (`SetControl`) | a **classifier** output, or a manual one-shot command |
| play the open/close **cycle** — `rest → flex → hold → release`, looping | a **training program** (`VhiTrainingAid.StartTrainingProgram`) | recording **regression** data, so `VHI_Control` sweeps a continuous kinematic range |

A discrete DOF is a *held state*; sweeping is a *recording aid*. Keeping them
apart is what stops data collection from changing what "grip" means.

The distinction matters because the two ML workflows want different things
from the control hand:

- **Classification** produces a discrete label - the hand should *be* that
  gesture, held, so the operator and subject see an unambiguous target.
- **Regression** needs a continuous target - the hand should *move through*
  the movement so the recorded `VHI_Control` kinematics span the full range
  the model regresses against.

The keyboard ++arrow-down++ always plays the cycle - that's the original
operator-cueing affordance, unchanged.
