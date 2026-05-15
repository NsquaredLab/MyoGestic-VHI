# The two hands

VHI renders two hands. They look identical but answer different questions.

| | **Predicted hand** | **Control hand** |
|---|---|---|
| Question it answers | "What does the model *predict*?" | "What movement is being *cued*?" |
| Driven by | the `MyoGestic_Output` LSL stream | predefined movements (gRPC / keyboard), **or** a streamed pose |
| Node | `PredictedHandSkeleton` | `ControlHandSkeleton` |
| Published as | `VHI_Predict` (LSL, 60 Hz) | `VHI_Control` (LSL, 60 Hz) |

## Predicted hand

The predicted hand is the simple one: it consumes a continuous 9-DOF pose from
the `MyoGestic_Output` LSL inlet and applies it to the bones every frame.
Optionally it **smooths** the incoming pose (`EnableSmoothing` /
`SmoothingSpeed`) - a per-frame spherical interpolation toward the target,
useful when the model output is noisy or arrives below display rate.

That's it. It has no state machine and no commands - it is a pure
visualisation of whatever is on the stream.

## Control hand

The control hand is the experimenter's reference. It has **three driver
modes** (see [Control-hand modes](control-modes.md)):

- **`Movement`** *(default)* - a predefined-movement state machine. It selects
  a named movement from the [movement set](movements.md) and either snaps to
  the movement's end pose or plays the open/close cycle (`waiting → closing →
  holding → opening → resting`). Driven by [gRPC `SetMovement`](grpc-control.md)
  or the keyboard.
- **`Stream`** - driven by a continuous pose on the `MyoGestic_ControlPose` LSL
  inlet, exactly like the predicted hand. For custom poses that aren't in the
  predefined set. See [Stream a custom pose](../how-to/stream-a-custom-pose.md).
- **`Idle`** - holds the rest pose; ignores keyboard, stream and commands.

Only one driver is active at a time - the mode decides which, so they never
fight over the bones. Movement commands (`SetMovement`, `Freeze`, `SetSpeed`)
are **rejected** unless the hand is in `Movement` mode.

### Sessions and keyboard authority

While a MyoGestic recording session is active, VHI's local keyboard control of
the control hand is **disabled** - set via the gRPC `SetSessionActive(true)`
call - so MyoGestic is the sole movement source for the recording. This is
orthogonal to the driver mode: `SetSessionActive` only gates the keyboard.

## Why two hands and not one

Keeping them separate is what makes VHI useful for myocontrol research: an
operator cues a movement on the **control** hand, the subject's EMG drives a
model, and the model's output animates the **predicted** hand - side by side,
on the same screen, recorded on the same LSL clock. The control hand is the
label; the predicted hand is the result.
