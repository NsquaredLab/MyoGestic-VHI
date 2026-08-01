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

v2 replaces that with a **manifest**. `GetControlManifest` lists every address VHI
exports — `vhi.prediction.index`, `vhi.control.gesture` — each with its kind, its
range or its states, the LSL stream it is read from and the channel it occupies there.
A client fetches it once, maps its own names onto those addresses, and sends. Nothing
hard-codes a channel index on either side, and nothing is negotiated: the manifest is
the same for every client, and VHI keeps no per-client state.

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

### The control hand has no modes

v1 had a `SetControlMode` RPC, and an intermediate v2 draft had a
`ControlHandDriverMode` enum (`Movement` / `Stream` / `Idle`) that a client flipped as
a side effect of declaring a control-pose stream. Both are gone, along with the
`DriverMode` Inspector field and the `SetDriverMode` method behind it.

What decides now is **stream presence**: the control hand renders
`MyoGestic_ControlPose` while a sample has arrived within the last five seconds, and
runs its own movement state machine otherwise. Publish, and it follows; stop, and it
returns to rest on its own. That is exactly how the predicted hand has always followed
`MyoGestic_Output` — the two hands differing on it was the only reason a mode existed.

The refusals moved with it. A discrete DOF sent while the stream is driving comes back
as `ControlAck.applied = false` with `rejected["vhi.control.gesture"]` reading
`'<movement>' was refused — a control-pose stream is driving the control hand`. It is a
**command-time** rejection now, not a setup-time one — there is no setup call left to
carry it. See [What drives the control hand](concepts/control-hand-drivers.md).

### The continuous stream is now standard

`MyoGestic_Output` carries **standard** values: `+1` means the direction the DOF name
denotes, so `+1` on index flexion *flexes*. v1 took raw rig units — the pose multipliers
the renderer applies to its per-bone gains — and named no channel, so what a value meant
was a matter of matching tables.

A pre-release draft of v2 let a client ask which convention was in force, through a
`continuous_encoding` field on the reply to a `Declare` handshake. It didn't last: the
first end-to-end build got the handshake wrong in exactly the way an optional encoding
invites — it agreed on channel *names* while the decoder still expected the old units,
and the hand extended when it was told to flex. The fix was to stop negotiating. There
is one encoding now, standard, unconditionally; the field is gone, and so is the
handshake that carried it.

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

    The optional `MyoGestic_ControlPose` inlet is standard too, unconditionally. There
    is nothing to opt into: publish the stream and the control hand follows it, stop and
    it returns to its own movements five seconds later. See
    [the LSL reference](reference/lsl-reference.md#myogestic_controlpose-is-standard-always).

## Upgrade steps

### If you use MyoGestic

Say what you control in your own names, point each at an address VHI publishes, and let
`VhiTarget` resolve the two against the manifest:

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
    client=client,                       # reads the manifest; refuses a pre-2.0 build
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
target.negotiate()      # re-reads the manifest; cheap and idempotent
```

`negotiate` is a MyoGestic-side retry, not a wire handshake: it re-fetches
`GetControlManifest` and resolves this configuration against it. VHI is told nothing
by it, and calling it twice costs one extra RPC.

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
7. **Stop asking for `Stream` mode.** Whatever selected it — a `control_pose=True`
   flag, a `DriverMode`, a `set_control_mode` call — just delete it. Publishing
   `vhi.control_outlet()` is the whole request, and `VhiTarget(..., stream="control_pose")`
   is how the bus drives that stream instead of `MyoGestic_Output`.

### If you use VHI directly

Generate stubs from `proto/myogestic_vhi.proto`, then:

1. Call `GetControlManifest` once, unconditionally, before you send anything.
2. For each control you drive, read the capability's `stream_name` and `channel` and
   build your frame *from those* — never from a remembered order. Both pose streams
   number their channels from zero, so a channel means nothing without its stream.
3. Publish that stream, and/or call `SetControl` for held states. Nothing has to be
   opened, declared or requested first.

An `UNIMPLEMENTED` on step 1 is a renderer too old to drive. Read
`ControlAck.rejected` on every `SetControl`: a refusal is always named, and a name
missing from it is the only evidence a value landed.

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
