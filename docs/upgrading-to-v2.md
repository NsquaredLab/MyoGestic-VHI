# Upgrading to VHI 2.0

VHI 2.0 replaces the v1 gRPC control plane with the **standard control vocabulary**, and
replaces the two nine-channel pose inlets with **one LSL stream per DOF**, standard-signed.
All of it is breaking, and all of it is detectable — nothing degrades silently.

!!! danger "Read this before updating a running experiment"
    A v1 client talking to VHI 2.0 gets `UNIMPLEMENTED` on every control call. A client
    still publishing `MyoGestic_Output` or `MyoGestic_ControlPose` moves nothing at all —
    those names no longer resolve to anything. And a client that streams the old pose
    convention will render **every joint inverted**. Update MyoGestic and VHI together.

    The manifest now carries the check for exactly this. `vocabulary_version` is `"2"` on
    2.0, a client declares the oldest vocabulary it can drive, and MyoGestic **refuses**
    any renderer reporting less — by name, at bind, rather than by leaving you to notice
    that a hand is not moving.

## What changed, and why

**Movements, poses and modes were three different things wearing one interface.** v1
sent nine floats whose channel meaning lived nowhere: channels 6-8 were dead on both
ends for years without anything noticing, and MyoGestic's own documentation described
channel 0 as thumb *rotation* and channels 6-8 as a wrist. Neither was true.

v2 replaces that with a **manifest**. `GetControlManifest` lists every address VHI
exports — `vhi.prediction.index`, `vhi.control.gesture` — each with its kind and its
range or its states. A streamed control's address is also the name of the LSL stream it
is read from, so nothing further describes the wire. A client fetches the manifest once,
maps its own names onto those addresses, and sends. Nothing hard-codes a stream layout on
either side, and nothing is negotiated: the manifest is the same for every client, and
VHI keeps no per-client state.

### One stream per DOF, applied on arrival

`MyoGestic_Output` and `MyoGestic_ControlPose` are **gone**. Every control VHI exports is
now its own LSL stream, named for its own address and **one channel wide**:

| v1 / early v2 | current |
|---|---|
| `MyoGestic_Output`, 9 channels, positional | `vhi.prediction.index`, `vhi.prediction.thumb.flexion`, … — nine streams, 1 channel each |
| `MyoGestic_ControlPose`, 9 channels, positional | `vhi.control.pose.index`, `vhi.control.pose.thumb.flexion`, … — nine streams, 1 channel each |
| `channel` on the capability told you where to write in the frame | **the address is the stream name.** `stream_name` and `channel` are gone from the capability entirely |

**The manifest stopped describing the wire, because the address already did.**
`ControlCapability` used to carry a `stream_name` and a `channel` beside each address, and
both are now gone: field numbers `10` and `11` are `reserved`, and so are the two *names*,
so a later field cannot quietly inherit either spelling in a JSON or text-format payload.
Neither ever said anything — `stream_name` always equalled `address`, and `channel` was
always `0`, or `-1` for the one control that never streams at all. Read `cap.address` and
publish under it; `cap.stream_name` and `cap.channel` raise `AttributeError` against a
regenerated stub, which is the loudest way for a field removal to reach a Python client.

**`vocabulary_version` is how a mismatched pair announces itself.** It is `"2"` on 2.0 —
one stream per DOF, named for the address, one channel wide — where `1` was the manifest
that carried `stream_name` and `channel` and let several controls share one wider stream.
A client declares the oldest vocabulary it can drive and refuses anything below it; that
is the only thing that makes two *separately installed* applications say a skew out loud
rather than bind cleanly and render nothing.

**And VHI now refuses a mis-shaped stream on receipt.** A resolved stream whose channel
count is not exactly 1 is logged as an error and its inlet is never opened. It used to
resize its buffer to whatever turned up and read element zero — which silently accepted a
nine-channel `MyoGestic_Output` frame from an un-migrated client and rendered its *thumb*
on every DOF.

**There is no whole-pose frame any more and nothing waits for one.** A sample is applied
the moment it arrives, and the DOFs that did not deliver hold what they were last
commanded to. That is the point rather than a tolerance: the DOFs are independently
actuated, may come from different producers, and may update at different rates, so a hand
whose index has moved and whose thumb has not is a real pose. **Two producers can each
own some DOFs of the same hand without contending** — the capability this shape exists
for.

Two practical consequences for a migrating client:

- **Publish only what you drive.** You no longer have to invent values for DOFs you do
  not control, and `0` is now a deliberate command for "rest this DOF" rather than filler.
- **The control hand follows *any* one of its nine streams.** A producer driving a single
  DOF takes the hand. The falling edge is the *last* one going quiet.

!!! success "The read-back outlets are unaffected"
    `VHI_Control` and `VHI_Predict` still publish each hand's whole **nine-channel** pose
    at 60 Hz, with the same labels, source IDs and metadata. A read-back is a recording,
    and a recording wants one row per instant. Nothing in a recording pipeline changes.

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

What decides now is **stream presence**: the control hand renders its
`vhi.control.pose.*` streams while a sample has arrived on any of them within the last
five seconds, and runs its own movement state machine otherwise. Publish, and it follows;
stop publishing all of them, and it returns to rest on its own. That is exactly how the
predicted hand has always followed its own streams — the two hands differing on it was
the only reason a mode existed.

The refusals moved with it. A discrete DOF sent while the stream is driving comes back
as `ControlAck.applied = false` with `rejected["vhi.control.gesture"]` reading
`'<movement>' was refused — a control-pose stream is driving the control hand`. It is a
**command-time** rejection now, not a setup-time one — there is no setup call left to
carry it. See [What drives the control hand](concepts/control-hand-drivers.md).

### The continuous streams are now standard

The `vhi.prediction.*` streams carry **standard** values: `+1` means the direction the DOF
name denotes, so `+1` on `vhi.prediction.index` *flexes*. v1 took raw rig units — the pose multipliers
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
    `vhi.prediction.index` and reading `VHI_Predict` channel 2 gives `+1` back — the
    renderer is the identity rather than a sign flip. Nothing archived depends on that
    stream, which is what makes the change safe to make.

    The optional `vhi.control.pose.*` streams are standard too, unconditionally. There
    is nothing to opt into: publish one and the control hand follows it, stop publishing
    them all and it returns to its own movements five seconds later. See
    [the LSL reference](reference/lsl-reference.md#the-control-pose-streams-are-standard-always).

## Upgrade steps

### If you use MyoGestic

Say what you control in your own names, point each at an address VHI publishes, and let
`RendererTarget` resolve the two against the manifest:

```python
from myogestic.controls import ControlBus, load_control_map, resolve
from myogestic.renderer import RendererTarget
from myogestic.vhi import virtual_hand

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

target = RendererTarget(
    client=client,                       # reads the manifest; refuses a pre-2.0 build
    interface=vhi,                       # one single-channel stream per address driven
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
5. **Delete any hand-built 9-float frame, and the stream it went to.** There is no frame
   any more: `MyoGestic_Output` and `MyoGestic_ControlPose` do not exist, each DOF has a
   stream of its own, and a frame assembled by position is correct for nothing. Push
   standard values through the bus and let it publish one stream per address the manifest
   lists.
6. **Drop `freeze`, `set_speed`, `set_chirality`, `set_control_mode`.** They have no
   equivalent by design.
7. **Stop asking for `Stream` mode.** Whatever selected it — a `control_pose=True`
   flag, a `DriverMode`, a `set_control_mode` call — just delete it. Publishing any
   `vhi.control.pose.*` stream is the whole request; which hand you drive is now decided
   by which addresses your control map points at.

### If you use VHI directly

Generate stubs from `proto/renderer_control.proto`, then:

1. Call `GetControlManifest` once, unconditionally, before you send anything, and
   **refuse a `vocabulary_version` below `2`**. It is a string holding a decimal integer;
   compare it numerically, and say what you refused and why.
2. For each control you drive, take the capability's `address` — never a remembered name
   — and create **one single-channel LSL outlet under it**. The address *is* the stream
   name: there is no `stream_name` and no `channel` on a capability any more, and a stream
   that is not exactly one `float32` channel wide is refused by VHI rather than read at
   index 0.
3. Push each DOF on its own outlet, at whatever rate you have values for it, and/or call
   `SetControl` for held states. Nothing has to be opened, declared or requested first,
   and you publish only the DOFs you drive — the rest hold where they are.

An `UNIMPLEMENTED` on step 1 is a renderer too old to have a manifest at all; a
`vocabulary_version` below your minimum is one that has a manifest and speaks a transport
you do not. Both are a renderer you cannot drive, and both are worth naming in a log
rather than working around. Read `ControlAck.rejected` on every `SetControl` too: a
refusal is always named, and a name missing from it is the only evidence a value landed.

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
— so "does `vhi.prediction.index` curl the index finger, in the flexion direction" is a
machine-checkable question rather than something you watch for:

```python
reply = vhi.control_client().sweep("vhi.prediction.index")
for observation in reply.observed:
    print(observation.element, observation.degrees_at_hi, observation.degrees_at_lo)
```

A correct DOF moves exactly the elements its name denotes, and `degrees_at_lo` is the
exact negative of `degrees_at_hi`. Anything else is a mapping or rigging error.
