# What drives the control hand

Three things can write to the control hand's bones, and they would otherwise fight
over them every frame. There is no mode field and no mode RPC deciding between
them: **the presence of a control-pose stream decides**, checked fresh every frame.

## The two drivers

| Driver | Live when | Written by |
|---|---|---|
| **The `MyoGestic_ControlPose` stream** | a sample arrived within the last `ControlPoseStaleAfterSeconds` (**5 s**) | anything publishing that LSL stream |
| **The named-movement state machine** | *otherwise* — including at startup, and again once the stream goes stale | discrete DOFs over `SetControl`, a recording trajectory, or the local keyboard |

That is the whole rule. Publish `MyoGestic_ControlPose` and the control hand
follows it; stop publishing, and after five seconds of silence it gives itself back
to its own movements. This is exactly how the predicted hand has always followed
`MyoGestic_Output` — the control hand simply used to need a handshake for the same
idea, and no longer does.

```python
# Nothing to declare, nothing to request. Publish, and the hand follows.
pose_outlet = virtual_hand().control_outlet()
pose_outlet.push(np.array([0, 0, 0.5, 0.5, 0.5, 0.5, 0, 0, 0], dtype=np.float32))
```

See [Stream a custom pose](../how-to/stream-a-custom-pose.md) for the whole flow, and
[the LSL reference](../reference/lsl-reference.md#myogestic_controlpose-is-standard-always)
for the conventions.

## What the stream refuses while it is driving

Ownership of the hand stays unambiguous: while the stream is live, a command that
would move the hand its own way is **refused by name**, not applied and then
overwritten sixty times a second.

| Call | While the stream is live |
|---|---|
| `SetControl` with a discrete DOF | `ControlAck.applied = false`, and `rejected["vhi.control.gesture"]` reads `'<movement>' was refused — a control-pose stream is driving the control hand` |
| `StartRecordingTrajectory` | `RecordingAck.applied = false`, with the reason in `message` |
| The local keyboard | ignored — the movement state machine is not running |

This is a **command-time** rejection, not a setup-time one. There is no handshake
left at which a client could be told in advance, and none is needed: the answer
depends on whether a stream is arriving *right now*, which only the moment of the
command can know. Read `ControlAck.rejected` rather than assuming a command landed.

!!! tip "Going stale resets the hand"
    When the stream falls silent for `ControlPoseStaleAfterSeconds`, the hand does
    not hold its last streamed pose. It stops any running recording trajectory,
    returns to rest, and resumes its movement state machine — so a producer that
    dies mid-recording cannot leave `VHI_Control` publishing a plausible held
    gesture that looks like an operator deliberately holding one.

`GetRecordingSessionState` reports the control hand's current movement and whether a
recording trajectory is running, so a client can show that and gate its own UI.

## Held state, or a swept trajectory

When the movement state machine is the driver, there are two ways the hand can show a
movement, and they are reached through two different kinds of RPC — deliberately,
because they are two different kinds of thing:

| What you want | How | Use it for |
|---|---|---|
| snap to the movement's **end pose** and hold it | a standard **discrete DOF** (`SetControl`) | a **classifier** output, or a manual one-shot command |
| play the open/close **cycle** — `rest → flex → hold → release`, looping | a **recording trajectory** (`StartRecordingTrajectory`) | recording **regression** data, so `VHI_Control` sweeps a continuous kinematic range |

A discrete DOF is a *held state*; sweeping is a *recording aid*. Keeping them
apart is what stops data collection from changing what "grip" means.

The distinction matters because the two ML workflows want different things
from the control hand:

- **Classification** produces a discrete label - the hand should *be* that
  gesture, held, so the operator and subject see an unambiguous target.
- **Regression** needs a continuous target - the hand should *move through*
  the movement so the recorded `VHI_Control` kinematics span the full range
  the model regresses against.

While a recording trajectory runs it **owns** the hand in the same way a stream does:
discrete DOFs are refused with the reason rather than interrupting the trajectory a
recording is being aligned against.

The keyboard ++arrow-down++ always plays the cycle - that's the original
operator-cueing affordance, unchanged. A recording session gates the keyboard off
entirely (`SetRecordingSession`), so a recording has one movement source.
