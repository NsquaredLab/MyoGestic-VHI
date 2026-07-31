# Upgrading to VHI 2.0

VHI 2.0 replaces the v1 gRPC control plane with the **standard control vocabulary**, and
changes the convention of the continuous LSL stream it reads. Both are breaking, and
both are detectable — nothing degrades silently.

!!! danger "Read this before updating a running experiment"
    A v1 client talking to VHI 2.0 gets `UNIMPLEMENTED` on every control call, and a
    client that streams the old pose convention will render **every joint inverted**.
    Update MyoGestic and VHI together, or run the compatibility path described below.

## What changed, and why

**Movements, poses and modes were three different things wearing one interface.** v1
sent nine floats whose channel meaning lived nowhere: channels 6-8 were dead on both
ends for years without anything noticing, and MyoGestic's own documentation described
channel 0 as thumb *rotation* and channels 6-8 as a wrist. Neither was true.

v2 makes an application declare what it controls **by name** and VHI answer with what
it can render. Nothing hard-codes a channel index on either side.

| v1 | current | Why it moved |
|---|---|---|
| `SetMovement(name)` | a standard **discrete DOF** | A movement is a *held state*. Declaring it as one gets you a stability gate for free. |
| `SetMovement(name, cycle=true)` | `StartRecordingTrajectory` | Cycling is a *recording* concern, not a control primitive — see below. |
| `SetSessionActive` | `SetRecordingSession` | Gating a recording is a property of the session, not of the controlled thing. |
| `SetSmoothing` | `SetPresentation` | Renamed for what it does: appearance. The old name invited it to be mistaken for chatter protection. |
| `GetState` | `GetRecordingSessionState` | Its only real job was discovering movement names. |
| `Freeze`, `SetSpeed`, `SetChirality`, `SetControlMode` | **removed, not replaced** | No consumer in MyoGestic. `SetChirality` never worked — its handler always returned `applied=false`. |

### Recording is not control

A standard discrete DOF is a held state: ask for a grip, hold a grip. Collecting
regression training data wants the opposite — a control hand that keeps *moving*, so
the recorded `VHI_Control` stream sweeps a continuous range for EMG windows to be
aligned against.

Those are different jobs, so they stay apart in the RPC surface even where they share a
service. Folding a sweep into the discrete vocabulary would have made "grip" mean *grip,
unless someone is recording*. While a recording trajectory runs it **owns** the control
hand: discrete DOFs are refused with the reason rather than silently interrupting the
trajectory a recording is aligned against.

### The continuous stream is now standard

`MyoGestic_Output` carries **standard** values: `+1` means the direction the DOF name
denotes, so `+1` on index flexion *flexes*. v1 took raw rig units — the pose multipliers
the renderer applies to its per-bone gains — and named no channel, so what a value meant
was a matter of matching tables.

`DeclareReply` briefly carried a `continuous_encoding` field so a client could tell which
convention was in force. It didn't last: the first end-to-end v2 build got the handshake
wrong in exactly the way an optional encoding invites — it agreed on channel *names*
while the decoder still expected the old units, and the hand extended when it was told
to flex. The fix was to stop negotiating: there is one encoding now, standard,
unconditionally, and the field is gone.

!!! warning "`VHI_Control` changed too, and it is a wire break"
    It published the renderer's own units, opposite to `VHI_Predict` on five channels —
    so a fist read `-1` on the stream you train from and `+1` on the one you drive, and
    every model needed its weights flipped by hand. Both are standard now. Sessions
    recorded before this are in the old units: convert them once with
    `myogestic.tools.migrate_vhi_sessions`, which stamps `pose_convention` into the
    session so a reader never has to guess. The outlet advertises the same key, and its
    `source_id` moved to `control_hand_002_standard`.

    `VHI_Predict` publishes **standard** values, so pushing `+1` on
    `MyoGestic_Output` and reading `VHI_Predict` gives `+1` back — the renderer is the
    identity rather than a sign flip. Nothing archived depends on that stream, which is
    what makes the change safe to make.

    The optional `MyoGestic_ControlPose` inlet is standard too, unconditionally — declare
    `control_pose=True` to opt in. Declaring that stream is also how you ask for `Stream`
    mode, since there is no separate mode RPC. See
    [the LSL reference](reference/lsl-reference.md#myogestic_controlpose-is-standard-always).

## Upgrade steps

### If you use MyoGestic

Declare what you control and let `VhiTarget` negotiate. It handles both conventions,
so the same application code works against VHI 1.x and 2.0:

```python
from myogestic.controls import ControlBus, load_control_map, resolve
from myogestic.vhi import VhiTarget, virtual_hand

vhi = virtual_hand()
client = vhi.control_client()

# Your name on the left, a control VHI declares on the right. VHI owns the
# semantics — which addresses exist, and whether each is a number or a held state.
CONTROL_MAP = load_control_map({
    "dofs": {
        "my_index": "vhi.prediction.index",
        "gesture": {"target": "vhi.control.gesture", "debounce_s": 0.1},
    }
})

# Resolution needs a running VHI, because the manifest is what declares the
# semantics. So resolve after VHI is up, not at import.
controls = resolve(CONTROL_MAP, client.capabilities())

target = VhiTarget(
    vhi.outlet(),
    client=client,                       # negotiates v2
)
bus = ControlBus(controls, targets=[target], hz=32)
recording = vhi.recording_client()
```

The mapping is normally a TOML file rather than a dict literal —
`load_control_map` takes a plain Mapping, so MyoGestic reads no configuration
files itself. See `examples/controls/` in the MyoGestic repository for
ready-to-copy files, including a classifier one that gates a probability with
`threshold_fraction` before the same weighted fan-out a regressor uses.

Then, once VHI is actually running — which for an app that launches VHI from its own UI
is *after* startup:

```python
target.negotiate()      # settles the contract; cheap and idempotent
```

1. **Replace `set_movement` with a discrete DOF.** `bus.select("gesture", "Fist")`
   for a deliberate click; `bus.push({...})` per tick for a classifier. Delete any
   `EdgeTrigger` you wrapped around the client — `debounce_s` on the DOF replaces it,
   and the bus owns the edge detection, dedupe and rebase.
2. **Replace `set_session_active`** with `recording.set_recording_session(...)`. It
   returns `False` when the aid is unavailable rather than raising, so you can decide
   whether an ungated recording is acceptable.
3. **Replace cycling `set_movement`** with
   `recording.start_trajectory(movement, frequency_hz=...)`. Call `stop_trajectory()` in
   teardown — it is idempotent.
4. **Replace `set_smoothing`** with `control_client().set_presentation(blend=...)`.
5. **Delete any hand-built 9-float frame.** It is correct for exactly one convention and
   silently inverted on the other. Push standard values through the bus instead.
6. **Drop `freeze`, `set_speed`, `set_chirality`, `set_control_mode`.** They have no
   equivalent by design.

### If you use VHI directly

Generate stubs from `proto/myogestic_vhi.proto` and call `Declare` first. Honour
`continuous_channel_order` — build your frame *from* it, don't assume your own order.

## Smoothing: three layers, not one

Worth stating explicitly, because v1's single `SetSmoothing` knob encouraged treating
them as interchangeable:

| Layer | Where | Applies to | Authoritative? |
|---|---|---|---|
| Continuous smoothing | MyoGestic's `ControlBus`, before any target | continuous DOFs | **yes** — sets the commanded value |
| Debounce / hysteresis | MyoGestic, declared on the DOF | discrete DOFs | **yes** — sets *when* a state changes |
| Presentation blending | VHI's `SetPresentation` | appearance | no |

A discrete control is **never** numerically low-pass filtered: averaging "rest" and
"fist" interpolates through a state nobody selected. A noisy classifier needs a
stability gate, which is layer 2. And blending cannot make an unstable prediction
stable — with blending on and no debounce a hand still jumps between states, just
smoothly, which is arguably worse because it looks deliberate.

## Verifying an upgrade

`SweepControl` exists for this. It drives one named DOF across its range and reports
which rig elements moved and by how many **signed** degrees, read back off the skeleton
— so "does `vhi.prediction.index.flexion` curl the index finger, in the flexion
direction" is a
machine-checkable question rather than something you watch for:

```python
reply = vhi.control_client().sweep("vhi.prediction.index.flexion")
for observation in reply.observed:
    print(observation.element, observation.degrees_at_hi, observation.degrees_at_lo)
```

A correct DOF moves exactly the elements its name denotes, and `degrees_at_lo` is the
exact negative of `degrees_at_hi`. Anything else is a mapping or rigging error.
