# What drives the control hand

Three things can write to the control hand's bones, and they would otherwise fight
over them every frame. There is no mode field and no mode RPC deciding between
them: **the presence of a control-pose stream decides**, checked fresh every frame.

## The two drivers

| Driver | Live when | Written by |
|---|---|---|
| **The `vhi.control.pose.*` streams** | a sample arrived on **any** of the nine within the last `ControlPoseStaleAfterSeconds` (**5 s**) | anything publishing any of those LSL streams |
| **The named-movement state machine** | *otherwise* — including at startup, and again once the last stream goes stale | discrete DOFs over `SetControl`, a recording trajectory, or the local keyboard |

That is the whole rule. Publish any `vhi.control.pose.*` stream and the control hand
follows it; stop publishing all of them, and after five seconds of silence it gives
itself back to its own movements. This is exactly how the predicted hand has always
followed its `vhi.prediction.*` streams — the control hand simply used to need a
handshake for the same idea, and no longer does.

```python
# Nothing to declare, nothing to request. Publish, and the hand follows.
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo("vhi.control.pose.index", "MyoGestic_Control", 1, 32, "float32", "cue")
outlet = StreamOutlet(info)
outlet.push_sample([0.5])   # the control hand's index half-flexes, and the hand is yours
```

**One DOF is enough to take the hand.** Requiring all nine would mean a producer that
drives a single DOF never took the control hand at all, which is the case this shape
exists for; the falling edge is the *last* stream going quiet, which is when the hand is
genuinely unclaimed. The DOFs you never publish simply hold where they are.

See [Stream a custom pose](../how-to/stream-a-custom-pose.md) for the whole flow, and
[the LSL reference](../reference/lsl-reference.md#the-control-pose-streams-are-standard-always)
for the conventions.

## What the streams refuse while they are driving

Ownership of the hand stays unambiguous: while any control-pose stream is live, a command
that would move the hand its own way is **refused by name**, not applied and then
overwritten sixty times a second.

| Call | While a stream is live |
|---|---|
| `SetControl` with a discrete DOF | `ControlAck.applied = false`, and `rejected["vhi.control.gesture"]` reads `'<movement>' was refused — a control-pose stream is driving the control hand` |
| `StartRecordingTrajectory` | `RecordingAck.applied = false`, with the reason in `message` |
| The local keyboard | ignored — the movement state machine is not running |

This is a **command-time** rejection, not a setup-time one. There is no handshake
left at which a client could be told in advance, and none is needed: the answer
depends on whether a stream is arriving *right now*, which only the moment of the
command can know. Read `ControlAck.rejected` rather than assuming a command landed.

!!! tip "Going stale resets the hand"
    When the *last* live stream falls silent for `ControlPoseStaleAfterSeconds`, the hand
    does not hold its last streamed pose. It stops any running recording trajectory,
    clears every DOF back to rest, and resumes its movement state machine — so a producer
    that dies mid-recording cannot leave `VHI_Control` publishing a plausible held
    gesture that looks like an operator deliberately holding one.

    The whole pose is cleared, not just the rig: otherwise the next single DOF to arrive
    would bring a departed producer's other eight values back with it.

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
