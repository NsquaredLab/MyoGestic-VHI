# Stream a custom pose

By default the control hand plays *named* [movements](../concepts/movements.md).
When you need to drive it with an **arbitrary, runtime-generated** pose - a
data glove, another model, a generated trajectory - publish the control-pose LSL
streams and [the hand follows them](../concepts/control-hand-drivers.md).

## When to use this

| You have… | Use… |
|---|---|
| a fixed set of gestures you can name | [a custom movement in the TOML config](add-a-custom-movement.md) |
| continuous values for one or more DOFs, generated at runtime | **this** - the control-pose streams |

## The shape: one stream per DOF

The control hand exports nine DOFs, and **each is its own LSL stream, one channel
wide, named for its own address**:

```text
vhi.control.pose.thumb.flexion      vhi.control.pose.wrist.flexion
vhi.control.pose.thumb.abduction    vhi.control.pose.wrist.abduction
vhi.control.pose.index              vhi.control.pose.wrist.rotation
vhi.control.pose.middle
vhi.control.pose.ring
vhi.control.pose.little
```

Three consequences shape everything below:

- **Publish only the DOFs you drive.** There is no frame to fill in. A DOF nobody
  publishes holds what it was last commanded to, so a glove that only tracks fingers
  never has to invent a wrist value.
- **Any one of them takes the hand.** The control hand is stream-driven while *any*
  control-pose stream has delivered within the last five seconds. Driving a single
  finger is enough.
- **Two producers can share one hand.** They own different DOFs, run at different
  rates, and never have to agree on anything — which is the capability this shape
  exists for.

## The two steps

### 1. Read the manifest for the stream names

There is nothing to declare and no mode to request. The one thing you need from VHI is
the name to publish each control under, and `GetControlManifest` publishes that:

```python
from myogestic.vhi import virtual_hand

vhi = virtual_hand()
client = vhi.control_client()

# What the control hand exports, and the stream name for each.
for cap in client.capabilities() or ():
    if cap.address.startswith("vhi.control.pose."):
        print(cap.address, cap.stream_name, cap.channel)
        # -> vhi.control.pose.index  vhi.control.pose.index  0
```

`stream_name` is the address and `channel` is `0` for every one of them: publish under
the name you read, one channel wide, and there is no positional layout to get wrong.

`vhi.control.pose.*` is the control hand's own namespace — deliberately distinct from
`vhi.prediction.*`, and on separate streams. Nothing can route a model's output into
the hand that output is supposed to be the ground truth *for*.

There is one convention on these streams: **standard** values in `[-1, 1]`, where `+1`
is the direction the DOF's name denotes and `0` is rest. VHI converts to its own rig
units internally, so a producer never chooses an encoding — it only chooses whether to
publish.

### 2. Publish an outlet per DOF, and push

```python
from pylsl import StreamInfo, StreamOutlet

def dof_outlet(address, rate=32):
    """One single-channel LSL outlet, named for the DOF it drives."""
    info = StreamInfo(
        name=address,          # straight off the manifest's stream_name
        type="MyoGestic_Control",
        channel_count=1,
        nominal_srate=rate,
        channel_format="float32",
        source_id=f"my-glove:{address}",
    )
    return StreamOutlet(info)

index = dof_outlet("vhi.control.pose.index")
thumb = dof_outlet("vhi.control.pose.thumb.flexion")

index.push_sample([0.5])   # half-flexed; the control hand is now stream-driven
thumb.push_sample([1.0])   # and the thumb follows, on its own timeline
```

That is the whole activation: the hand renders each sample as it arrives, and no call
anywhere turns it on. The prediction streams are untouched — driving the control hand
does not touch the predicted hand.

## Notes

- **Keep pushing.** After five seconds of silence on a stream
  (`ControlPoseStaleAfterSeconds`) VHI drops that inlet and looks for the name again.
  When the *last* control-pose stream goes quiet the producer counts as gone: the hand
  stops any running trajectory, clears every DOF to rest, and resumes its own movements.
  Push at your loop rate even when the value has not changed.
- **`0` rests a DOF; silence does not.** A DOF holds its last commanded value. Stopping
  one stream while the others keep going leaves that DOF exactly where it was.
- **Discrete DOFs are refused while you are streaming.** `SetControl` comes back with
  `applied = false` and the reason in `rejected` — two drivers, one hand. Stop
  publishing every control-pose stream if you want named movements back.
- `VHI_Control` still publishes the control hand's *actual* pose as a whole
  **nine-channel** stream at 60 Hz, so a streamed pose round-trips into the recording
  just like a played movement does. The read-back shape did not change with the
  per-DOF inlets: a recording wants one row per instant.
- Driving this from MyoGestic rather than raw `pylsl`? Point your control map at the
  `vhi.control.pose.*` addresses and let `VhiTarget` resolve them against the manifest —
  see [Drive VHI from MyoGestic](drive-from-myogestic.md).

## Use case: 9-DOF model-robustness validation

The control-pose streams are the right tool for **systematically testing a myocontrol
model across the full 9-DOF pose space** - including multi-DOF activations
that no single named movement covers. The pattern is:

```python
import time
from itertools import product

from myogestic.vhi import virtual_hand
from pylsl import StreamInfo, StreamOutlet

vhi = virtual_hand()
client = vhi.control_client()
recording = vhi.recording_client()

# 1. Orchestration over gRPC. Nothing here turns the streams on — the only call is
#    the manifest, and it is a read.
STREAM = {
    cap.address: cap.stream_name
    for cap in client.capabilities() or ()
    if cap.address.startswith("vhi.control.pose.")
}
DIGITS = [                              # the six finger DOFs; the order is yours to
    "vhi.control.pose.thumb.flexion",   # pick, because nothing here is positional
    "vhi.control.pose.thumb.abduction",
    "vhi.control.pose.index",
    "vhi.control.pose.middle",
    "vhi.control.pose.ring",
    "vhi.control.pose.little",
]
recording.set_recording_session(True)   # gate VHI's keyboard - MyoGestic owns the hand

# 2. One outlet per DOF under test. The wrist is never published, so it holds at rest
#    for the whole sweep without anything having to write zeros to it.
outlets = {
    address: StreamOutlet(
        StreamInfo(STREAM[address], "MyoGestic_Control", 1, 32, "float32", f"sweep:{address}")
    )
    for address in DIGITS
}

# 3. Continuous injection over LSL. The first sample is the activation.
LEVELS = [0.0, 0.5, 1.0]                # rest / half / full flexion per DOF
                                        # (standard: +1 flexes)
SETTLE_S = 0.5                          # well inside the 5 s stale window

for combo in product(LEVELS, repeat=len(DIGITS)):    # 3^6 = 729 multi-DOF poses
    for address, level in zip(DIGITS, combo):
        outlets[address].push_sample([level])
    time.sleep(SETTLE_S)
    # Your model's prediction at this moment is recorded on its own LSL
    # outlet; line it up with VHI_Control post-hoc via XDF timestamps.

# 4. Tear down. Stop pushing and the hand releases itself five seconds later.
recording.stop_trajectory()             # no-op unless one was started
recording.set_recording_session(False)
```

`SETTLE_S` is half a second, comfortably inside the five-second stale window, so the
hand stays stream-driven for the whole sweep without anything holding it there. And no
stream name is written down anywhere: `STREAM` comes from the manifest, so a build that
renames a DOF moves this loop with it.

Two planes, two roles - this is the whole design:

| Plane | What it carries here | Role |
|---|---|---|
| **gRPC** | `GetControlManifest` (the stream names), `SetRecordingSession`, `GetRecordingSessionState` | discovery, setup, assertions |
| **LSL** (`vhi.control.pose.*`) | the test poses themselves, one stream per DOF | continuous data, and the only thing that activates the hand |

Two LSL records line everything up offline:

- `VHI_Control` is the **ground truth** the participant saw - record this and
  align it to your model's prediction outlet via [XDF](../concepts/lsl-streams.md).
  It is a whole nine-channel pose per sample, which is exactly what makes it the record.
- Your injected `vhi.control.pose.*` streams and the model's output are
  what you compare. Robust models track the injected pose with bounded error
  across all combos; brittle models fail on combos no single named movement
  covers.

Edge cases the named-movement set can't reach - "thumb half-flexed + index
fully flexed + everything else at rest", say - are exactly what this loop
exposes.
