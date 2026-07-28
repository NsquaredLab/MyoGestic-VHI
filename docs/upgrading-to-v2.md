# Upgrading to VHI 2.0

VHI 2.0 replaces the v1 gRPC control plane with the **canonical control standard**, and
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

| v1 | v2 | Why it moved |
|---|---|---|
| `SetMovement(name)` | a canonical **discrete DOF** | A movement is a *held state*. Declaring it as one gets you a stability gate for free. |
| `SetMovement(name, cycle=true)` | `VhiTrainingAid.StartTrainingProgram` | Cycling is a *recording* aid, not a control primitive — see below. |
| `SetSessionActive` | `VhiTrainingAid.SetRecordingSession` | Gating a recording is a property of the session, not of the controlled thing. |
| `SetSmoothing` | `SetPresentation` | Renamed for what it does: appearance. The old name invited it to be mistaken for chatter protection. |
| `GetState` | `VhiTrainingAid.GetTrainingState` | Its only real job was discovering movement names. |
| `Freeze`, `SetSpeed`, `SetChirality`, `SetControlMode` | **removed, not replaced** | No consumer in MyoGestic. `SetChirality` never worked — its handler always returned `applied=false`. |

### Recording is not control

A canonical discrete DOF is a held state: ask for a grip, hold a grip. Collecting
regression training data wants the opposite — a control hand that keeps *moving*, so
the recorded `VHI_Control` stream sweeps a continuous range for EMG windows to be
aligned against.

Those are different jobs, so they have separate services. Folding a sweep into the
discrete vocabulary would have made "grip" mean *grip, unless someone is recording*.
While a training program runs it **owns** the control hand: discrete DOFs are refused
with the reason rather than silently interrupting the trajectory a recording is aligned
against.

### The continuous stream is now canonical

`MyoGestic_Output` carries **canonical** values: `+1` means the direction the DOF name
denotes, so `+1` on index flexion *flexes*. v1 wanted the renderer's own convention,
where flexion was negative.

`DeclareReply.continuous_encoding` reports which convention is in force, and it is not
optional: a client that reads `ENCODING_UNSPECIFIED` must fall back rather than guess.
That field exists because the first end-to-end v2 build got this wrong — the handshake
agreed on channel *names* while the decoder still expected the old units, and the hand
extended when it was told to flex.

!!! info "VHI's own outlets did **not** change"
    `VHI_Control` and `VHI_Predict` still publish in the renderer's units. That is
    deliberate: every session recorded before this release stays readable by the same
    decoder. Only the stream VHI *reads* changed convention.

## Upgrade steps

### If you use MyoGestic

Declare what you control and let `VhiTarget` negotiate. It handles both conventions,
so the same application code works against VHI 1.x and 2.0:

```python
from myogestic.controls import ControlBus, load_dofs
from myogestic.vhi import VhiTarget, virtual_hand

vhi = virtual_hand()
CONTROLS = load_dofs({
    "dofs": {
        "index.flexion": "continuous",
        "hand.gesture": {
            "kind": "discrete",
            "states": ["rest", "fist"],
            "rest": "rest",
            "debounce_s": 0.1,
        },
    }
})

target = VhiTarget(
    vhi.outlet(),
    client=vhi.canonical_client(),     # negotiates v2
    legacy_client=vhi.control_client(),  # renders discrete DOFs on VHI 1.x
)
bus = ControlBus(CONTROLS, targets=[target], hz=32)
training_aid = vhi.training_client()
```

Then, once VHI is actually running — which for an app that launches VHI from its own UI
is *after* startup:

```python
target.negotiate()      # settles the contract; cheap and idempotent
```

1. **Replace `set_movement` with a discrete DOF.** `bus.select("hand.gesture", "fist")`
   for a deliberate click; `bus.push({...})` per tick for a classifier. Delete any
   `EdgeTrigger` you wrapped around the client — `debounce_s` on the DOF replaces it,
   and the bus owns the edge detection, dedupe and rebase.
2. **Replace `set_session_active`** with `training_aid.set_recording_session(...)`. It
   returns `False` when the aid is unavailable rather than raising, so you can decide
   whether an ungated recording is acceptable.
3. **Replace `set_movement(..., cycle=True)`** with
   `training_aid.start_program(movement, frequency_hz=...)`. Call `stop_program()` in
   teardown — it is idempotent.
4. **Replace `set_smoothing`** with `canonical_client().set_presentation(blend=...)`.
5. **Delete any hand-built 9-float frame.** It is correct for exactly one convention and
   silently inverted on the other. Push canonical values through the bus instead.
6. **Drop `freeze`, `set_speed`, `set_chirality`, `set_control_mode`.** They have no v2
   equivalent by design.

### If you use VHI directly

Generate stubs from `proto/myogestic_vhi_v2.proto` and call `Declare` first. Honour
`continuous_channel_order` (build your frame *from* it, don't assume your own order) and
`continuous_encoding`. Treat `ENCODING_UNSPECIFIED` as "cannot negotiate".

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
— so "does `index.flexion` curl the index finger, in the flexion direction" is a
machine-checkable question rather than something you watch for:

```python
reply = vhi.canonical_client().sweep("index.flexion")
for observation in reply.observed:
    print(observation.element, observation.degrees_at_hi, observation.degrees_at_lo)
```

A correct DOF moves exactly the elements its name denotes, and `degrees_at_lo` is the
exact negative of `degrees_at_hi`. Anything else is a mapping or rigging error.
