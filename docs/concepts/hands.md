# The two hands

VHI renders two hands. They look identical but answer different questions.

| | **Predicted hand** | **Control hand** |
|---|---|---|
| Question it answers | "What does the model *predict*?" | "What movement is being *cued*?" |
| Driven by | the `vhi.prediction.*` LSL streams, one per DOF | predefined movements (gRPC / keyboard), **or** the `vhi.control.pose.*` streams |
| Node | `PredictedHandSkeleton` | `ControlHandSkeleton` |
| Published as | `VHI_Predict` (LSL, 60 Hz, 9 channels) | `VHI_Control` (LSL, 60 Hz, 9 channels) |

## Predicted hand

The predicted hand is the simple one: each of its nine DOFs arrives on a stream
of its own (`vhi.prediction.index` and its eight siblings) and is applied to the
bones the moment it arrives. A DOF nobody is publishing holds what it was last
commanded to, so a producer driving one finger moves one finger and the rest
hold. Optionally the hand **blends** toward the commanded pose (`EnableSmoothing`
/ `SmoothingSpeed`) - a per-frame spherical interpolation, useful when the model
output is noisy or arrives below display rate.

That's it. It has no state machine and no commands - it is a pure
visualisation of whatever is on those streams.

## Control hand

The control hand is the experimenter's reference. It has **two drivers** (see
[What drives the control hand](control-hand-drivers.md)):

- **The `vhi.control.pose.*` streams** - one per DOF, each applied to the bones as
  it arrives, exactly like the predicted hand's. For custom poses that aren't in
  the predefined set. See [Stream a custom pose](../how-to/stream-a-custom-pose.md).
- **A predefined-movement state machine** - it selects a named movement from the
  [movement set](movements.md) and either snaps to the movement's end pose or plays
  the open/close cycle (`waiting → closing → holding → opening → resting`). Driven
  by [standard discrete DOFs](grpc-control.md), a recording trajectory, or the
  keyboard.

Only one is active at a time, and **stream presence** decides: while a sample has
arrived on **any** control-pose stream within the last five seconds the streams drive
the hand, and the state machine does not run. One DOF is enough — a producer driving a
single finger has still taken the hand. So they never fight over the bones, and a client
that stops publishing all of them gets the movement state machine back without asking.
While any stream is live, discrete DOFs and recording trajectories are **refused by
name**.

### Sessions and keyboard authority

While a MyoGestic recording session is active, VHI's local keyboard control of
the control hand is **disabled** - set via the recording aid's `SetRecordingSession(true)`
call - so MyoGestic is the sole movement source for the recording. This is
orthogonal to which driver is live: the session gate only gates the keyboard.

## Why two hands and not one

Keeping them separate is what makes VHI useful for myocontrol research: an
operator cues a movement on the **control** hand, the subject's EMG drives a
model, and the model's output animates the **predicted** hand - side by side,
on the same screen, recorded on the same LSL clock. The control hand is the
label; the predicted hand is the result.
