# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **liblsl is configured by VHI itself at startup** (macOS and Linux): IPv6 off and, on macOS,
  multicast pinned to the first physical interface, written to `user://lsl_api.cfg` and applied through `LSLAPICFG` (an
  `LSLAPICFG` already in the environment wins). The repo's `lsl_api.cfg` carried the IPv6 half
  since July, but liblsl reads that file only from the working directory, so no exported build
  ever applied it. On a Mac with VPN tunnels up this is the difference between fingers that
  drop and come back at random — discovery replies from tunnel addresses that never connect, a
  resolver that dies with "internal error" when a tunnel goes down, and two configd-watchdog
  kernel panics (2026-07-31, 2026-09-12) — and a link that stays up.
  Known limit: the address is read once at startup, so restart VHI after changing networks.
- **The inlet resolve loop backs off** while it finds nothing: 5 s doubling to 30 s, reset by a
  connect or a dropped inlet. A producer that is not running no longer costs a resolve every
  five seconds for as long as VHI is open.

## [2.0.0] - 2026-08-02

**One control service, and the manifest is the contract.** VHI 2.0 and **MyoGestic 2.5 are one
release** and must be upgraded together: the pair negotiates a `vocabulary_version`, this build
reports `2`, and MyoGestic 2.5 requires 2. A mismatched pair refuses at bind rather than
half-driving a hand.

There is no upgrade path from 1.x and no compatibility window. `myogestic-install-vhi` refuses
anything below 2.0 before downloading it.


### Added

- **`RemoteControl` — the one gRPC control service, and `GetControlManifest` is its
  whole contract.** The manifest lists every **address** VHI exports
  (`vhi.prediction.index`, `vhi.control.gesture`) with what it can render for each: the
  kind, and the range or the states. A client calls it once, unconditionally, before it
  sends anything, and maps its own configuration's names onto those addresses. There is no
  per-client negotiation and nothing to declare — the manifest is the same for everyone,
  and VHI keeps no session state about who is talking to it. Neither side hard-codes a
  stream layout.

  A streamed capability's **address is the name of its LSL stream**, and the manifest says
  nothing further about the wire because there is nothing further to say: every DOF is a
  stream of its own, one `float32` channel wide, so a client publishes under the address it
  read and there is no position to get wrong. `vhi.control.gesture` is the one control that
  never touches LSL, and its **kind** is what says so — a discrete control is a held state,
  it travels over `SetControl`, and it drives no stream at all.

  A DOF that cannot be rendered is reported by name with a reason and **never silently
  ignored** — an ignored joint looks exactly like a joint that is working and holding
  still.
- **Discrete DOFs render as control-hand movements.** States resolve case-insensitively
  against the movement names the build actually offers, discovered per call rather than
  from a table, because the movement set changes with the movement mode. A DOF where any
  state fails to resolve is refused outright: partly-resolvable is not partly-renderable,
  it is a DOF that silently does nothing some of the time.
- **`SweepControl` — verification without a human at the screen.** Drives one named DOF
  across its range and reports which rig elements moved and by how many *signed* degrees,
  read back off the skeleton. Turns "does `vhi.prediction.index` curl the index finger,
  in the flexion direction" into an assertion. It reports the model's own bone names, so a
  re-rig surfaces as a changed name rather than as a hand moving the wrong finger.
- **Recording-session coordination lives on the same service, deliberately not on the
  control plane.** `SetRecordingSession` gates VHI's local keyboard off so a recording
  has one movement source; `StartRecordingTrajectory` cycles the control hand through a
  movement so the recorded pose stream sweeps a continuous range instead of snapping
  between held states; `GetRecordingSessionState` reports session state plus the
  movement names a trajectory may use. These RPCs live on `RemoteControl` rather than a
  service of their own — a standard discrete DOF is a *held state*, and a running
  trajectory must not redefine that, so while one runs it owns the control hand and
  discrete DOFs are refused with the reason.
- **The control hand follows the presence of its pose streams, and has no modes.**
  Publish any of the optional `vhi.control.pose.*` streams and the control hand renders
  it; stop publishing all of them, and after `ControlPoseStaleAfterSeconds` (5 s) it
  stops any running trajectory, returns to rest, and resumes its own named movements.
  Publishing *is* the request: an inlet nobody reads is indistinguishable from a stream
  that is not arriving. This is exactly how the predicted hand has always followed its
  own streams, and the two hands differing on it was the only reason a mode ever existed.
  `vhi.control.pose.*` is a namespace of its own, because it is a separate hand serving a
  separate purpose from `vhi.prediction.*`.

  A stream and a discrete DOF cannot both own those bones, so while the stream is live
  a discrete DOF is refused **by name**, with
  `'<movement>' was refused — a control-pose stream is driving the control hand` in
  `ControlAck.rejected`. v1 arbitrated the same conflict through `ControlMode`, where a
  client only ever saw commands quietly not apply.
- **`SetPresentation` — target-side blending, named for what it is.** The third of three
  distinct smoothing layers (continuous smoothing and discrete debounce are the other
  two, both on the MyoGestic side). Appearance only; it cannot make an unstable
  prediction stable.
- **The first automated test suite in this repository.** `tests/test_v2_contract.py`
  launches VHI, drives it over gRPC, and machine-checks the rig: every DOF moves exactly
  the bones its name denotes, turns each by the documented degrees, and mirrors exactly
  in the other direction. Stubs are generated at session start rather than committed, so
  the tests cannot drift from `proto/`. Run with
  `uv run --group test pytest tests/test_v2_contract.py`.

### Changed

- **BREAKING: the contract is named for what it is, not for who serves it first.**
  `proto/myogestic_vhi.proto` → `proto/remote_control.proto`, `package myogestic.vhi` →
  `package myogestic.remote`, and `service VhiControl` → `service RemoteControl`.
  Nothing about the wire's *data* moved — every field number, name and type is unchanged
  — but the **service path** did (`/myogestic.remote.RemoteControl/SetControl`), so
  MyoGestic and VHI must be upgraded together. The contract never described a hand: it
  describes a remote target that publishes a manifest of addressed controls, and VHI is one
  implementation of it. The C# namespace follows the package: `Myogestic.Vhi.*` →
  `Myogestic.Remote.*`. `Vhi.VhiControlService` keeps its name — it is VHI's
  implementation, and that *is* VHI-specific.
- **BREAKING: `stream_name` and `channel` are gone from `ControlCapability`, and
  `vocabulary_version` is now the gate that says so.** The two fields described a
  transport that had stopped needing describing: VHI publishes one LSL stream per DOF,
  named for that DOF's own address and exactly one `float32` channel wide, so
  `stream_name` always equalled `address` and `channel` was always `0` — or `-1` for the
  one discrete control, which is a fact its **kind** already carried. A field that can
  only repeat its neighbour is a field two codebases can disagree about for no gain. The
  address is the stream name, and that is now the whole of the inbound transport contract.

  Field numbers `10` and `11` are `reserved`, and so are the **names** `stream_name` and
  `channel`. Reserving the numbers alone would still let a later field take either
  spelling, and in JSON or text format that field would then read as this one to anything
  still carrying the old schema.

  `ControlManifest.vocabulary_version` moves from `"1"` to **`"2"`**, and from
  informational to load-bearing. It is a decimal integer compared numerically; a client
  declares the oldest vocabulary it can drive and **refuses** anything below it, by name,
  at bind. MyoGestic declares a minimum of 2. VHI and its clients are separately installed
  applications, so upgrading one does not upgrade the other, and without the gate the skew
  is silent in the worst way available: an old target waits for a wide pose stream nobody
  publishes any more, logs nothing at all, and the hand simply never moves. Vocabulary `1`
  was the `stream_name`/`channel` manifest, in which several controls could share one wider
  stream; `2` is one stream per DOF, named for the address.

  Code reading `cap.stream_name` or `cap.channel` raises `AttributeError` against a
  regenerated stub, which is the loudest way a field removal reaches a Python client.

- **BREAKING: an inbound stream that is not exactly one channel wide is refused, not
  tolerated.** A resolved stream is checked for width before an inlet is opened, and
  anything other than 1 is logged and left unopened:
  `❌ <name> is published N channels wide, and this contract is one address per stream,
  one float32 channel. Not opening it — publish one stream per DOF, named for the
  address.` It used to resize its buffer to whatever turned up and read element zero,
  which quietly accepted a nine-channel whole-pose outlet from a client too old to know
  the streams had been split apart — and element zero of that frame is the *thumb*, so
  every DOF would have rendered the thumb's value with nothing anywhere saying so. A
  receiver that advertises an invariant is the thing that has to enforce it. The
  `✅ Connected to LSL inlet` line no longer reports a channel count, because there is only
  one count it can ever be.

- **BREAKING: one LSL stream per DOF, replacing the two nine-channel pose inlets.**
  `MyoGestic_Output` and `MyoGestic_ControlPose` are gone. Every control VHI exports is
  now its own stream, named by its own address and **one channel wide**:
  `vhi.prediction.index`, `vhi.prediction.thumb.flexion`, `vhi.control.pose.index`, and
  their siblings — the same names `GetControlManifest` has always published. **The address
  is the stream name**, so a client reads the name it must publish under straight off the
  manifest and there is no positional layout left to get wrong.

  **There is no whole-pose frame any more and nothing waits for one.** A sample is
  applied to the hand the moment it arrives, and the DOFs that did not deliver hold what
  they were last commanded to. The DOFs are independently actuated, may come from
  different producers and may update at different rates — nothing links them, so a hand
  whose index has moved and whose thumb has not is a real pose rather than a
  half-delivered one. Two producers, one publishing the thumb and one the index, drive
  the same hand without contending or agreeing on anything, which is the capability this
  shape exists for.

  `ControlPoseLive` now means **any** control-pose stream is delivering. Requiring all
  nine would mean a producer that drives one DOF never took the control hand at all,
  which is the case worth supporting; the falling edge is the *last* stream going quiet,
  which is when the hand is genuinely unclaimed and `StopToRest` is right. Each inlet
  keeps its own staleness clock and is dropped on its own, so a producer replaced on one
  DOF is picked up without disturbing the eight beside it.

  The two read-back outlets are **unchanged**: `VHI_Control` and `VHI_Predict` still
  publish each hand's whole nine-channel pose at 60 Hz. A recording wants one row per
  instant.

  Resolution is one liblsl resolve per retry interval however many inlets are missing —
  the pass asks once and matches every wanted name against that one answer. Never more
  than one resolve in flight, and the count cannot grow with the number of DOFs.

- **`VHI_Control` publishes standard values.** A held `Fist` is
  `[1, -1, 1, 1, 1, 1, …]`: five flexions and an *ad*ducted thumb. It previously published
  the rig's own units, opposite on five channels. The outlets advertise
  `pose_convention = "standard"` and the control outlet's `source_id` is now
  `control_hand_002_standard`, so the conventions are distinguishable on the wire.
  Sessions recorded before this are in the old units: convert them once with
  `myogestic.tools.migrate_vhi_sessions`, which stamps `pose_convention` into the session
  so a reader never has to guess.
- **`VHI_Control` fills the wrist channels.** They were hardcoded to three zeros "for
  compatibility", so a recording of `WristUpDown` or `WristLeftRight` captured nothing.
- **`movements.toml` declares its convention and is migrated once.** A file without a
  `convention` key is Unity-signed, and is rewritten in place in rig-native degrees with a
  `.unity-signed.bak` copy beside it. Converting on every load instead would leave a file
  on disk whose numbers mean the opposite of what they say.
- **BREAKING: the continuous LSL inlets take standard values.** Every inbound stream now
  carries values where `+1` means the direction the DOF name denotes, so `+1` on
  `vhi.prediction.index` *flexes*. v1 expected VHI's own rig units of the day — the
  Unity-signed gain table, in which flexion was negative — so a v1 sample and a standard
  one of the same sign render opposite hands. There is exactly one encoding now, so
  nothing negotiates it: the field that once announced it is gone, and a client simply
  sends standard values.

  **Every stream is standard**, inlets and outlets alike — see `VHI_Control`
  above and `VHI_Predict` below. Nothing on the wire is in rig units any more.
- **`SweepControl`'s expectation is axis-aware.** Thumb abduction drives all three thumb
  bones through one channel, but the distal bone's Z gain is `0` — a channel-wide
  expectation reported a correct sweep as a mismatch.
- The nine DOFs are documented from a **verified** map. `thumb.flexion` and
  `thumb.abduction` are two distinct DOFs and the previous documentation had them
  swapped, calling the first thumb *rotation*; the three wrist DOFs are live, not the dead
  channels 6-8 they were described as — see Fixed, below. They are read-back channels 0,
  1 and 6-8 on `VHI_Control` / `VHI_Predict`; inbound they are streams of their own.
- **BREAKING: the control plane collapsed to one gRPC service, and none of this has a
  compatibility window.** `VhiTrainingAid` is gone; its RPCs move onto `RemoteControl` and
  lose "training" from their names in the process (`StartTrainingProgram` →
  `StartRecordingTrajectory`, `StopTrainingProgram` → `StopRecordingTrajectory`,
  `GetTrainingState` → `GetRecordingSessionState`) — a recording aid that cannot see what
  the control service already declared to the same hand was two sources of truth for one
  state machine, not two independent responsibilities. The per-capability
  `ContinuousEncoding` field is gone from the manifest, and no field anywhere names an
  encoding: the sign convention they used to carry left the wire entirely once the
  predicted hand stopped computing one to negate. None of this degrades gracefully — an old MyoGestic against a
  new VHI, or the reverse, refuses to link at all: wrong service name, wrong RPC names, a
  field that no longer exists. **MyoGestic and VHI must be upgraded together**; there is
  no staged rollout and no version this pair is backward-compatible with.

### Removed

- **BREAKING: the v1 `VhiControl` service and `proto/myogestic_vhi.proto`.** A client
  that still speaks v1 receives `UNIMPLEMENTED` — the same signal a current client gets
  from `GetControlManifest` against a build too old to answer, and how it recognises a
  target it cannot drive.

  Capabilities were split by *kind* rather than moved wholesale: `SetMovement` → a
  standard discrete DOF; `SetMovement(cycle=true)` and `SetSessionActive` → the
  recording-session RPCs; `SetSmoothing` → `SetPresentation`; `GetState` →
  `GetRecordingSessionState`.
  `Freeze`, `SetSpeed`, `SetChirality` and `SetControlMode` were **not** replaced: each
  was a transport concept with no consumer, and `SetChirality` never worked — its handler
  always returned `applied=false`.

  See **[Upgrading to VHI 2.0](docs/upgrading-to-v2.md)** for the full migration.

### Fixed

- **Both hands bent the wrong way, and `VHI_Control` published the opposite of
  `VHI_Predict`.** Standard `+1` extended a digit instead of flexing it, and the
  ground-truth stream you train on called a fist `-1` while the prediction stream you
  drive needed `+1`. Every model trained against VHI needed its weights flipped by hand,
  and nothing on either wire said so.

  The root cause was that `MovementPoses` is not what the rig renders.
  `ApplyMovementPose` interpolated with `-Mathf.Sin(argument)`, so a held `Fist` put
  `WaveBone_7` at `+85°` while the table read `-85`. **Positive X is flexion on this
  rig.** Both skeletons had privately copied those raw rows as their gain tables, and so had the
  contract suite's direction gate — every check agreed with every other and all of them
  disagreed with the hand. Four fingers curling backwards still look fist-shaped; the
  thumb is the only digit whose flexion is not symmetric front-to-back, and it is where
  the error was finally visible.

  `StandardPose.AtPlusOne` is now the only place that knows what a standard value means in
  degrees; neither skeleton owns a sign. `MovementPoses` is in rig-native degrees and the
  animation interpolates plainly — which also fixes a movement whose rest pose is not
  zero, where `rest + (max - rest) * -1` was not an interpolation at all
  (`Movements.Thumb`'s middle joint reached `+35°` instead of `55°`).

  **Two vocabularies, and every sentence in this release says which it is in.** *Rig-native
  degrees* are what a bone is actually rotated by; *standard values* are the `[-1, 1]`
  domain on the wire. `StandardPose.AtPlusOne` maps between them and
  `StandardPose.Standard` is a plain divide by it, so the two **agree in sign**: positive
  X is flexion in degrees, and standard `+1` flexes. Only one thing was ever in the other
  sign — the Unity-authored `MovementPoses` rows, which `ApplyMovementPose` negated on the
  way to the bone. Those rows are rig-native now, so nothing in this repository is
  Unity-signed and no sentence here needs to be read twice.

  The direction anchor is no longer derived from anything VHI also reads. It
  holds the movement whose *name* says what it is — `Movements.Index` is index flexion
  because a human called it that — and asserts what `VHI_Control` publishes.
- **VHI no longer reads a sender's channel labels back off the inlet, and does not
  crash.** Asking an inlet for its stream info is the only thing that starts liblsl's
  `info_receiver` thread, and cancelling that thread mid-request crashed VHI. All
  it bought was a producer's right to send a narrower frame, which saved three floats.
  What a stream carries is settled before a byte moves — by its **name**, which is the
  address of the one DOF it drives — so there is nothing to reconstruct on arrival.
- **The wrist renders.** `wrist.flexion` and `wrist.abduction` on both hands, channels 6
  and 7. Bone 0 is the common ancestor of all five digit chains, so turning it turns the
  whole hand — it was mapped, named "wrist", and never written to.

  **All three wrist numbers are calibrations, not derivations** (`StandardPose.Wrist`,
  in rig-native degrees). A bone's local basis is its own, so nothing about the fingers
  constrains which way joint 0 turns — an earlier reading derived X from the digits and
  got it wrong twice over.

  **X = +30.** `Movements.WristUpDown` gives the magnitude as ±30°; the sign says
  standard `+1` flexes, matching the digits. **Z = +20.** `Movements.WristLeftRight`
  gives ±20° and names neither side — "left/right" says which axis, not which is
  abduction — so the sign is taken by analogy with flexion and marked as such. Flipping
  either is one number in `Vhi.StandardPose` and a failing test.

  `wrist.rotation` renders too, on channel 8, and it is the one control here with **no
  rig-side evidence at all** — no movement in the library touches joint 0's Y axis, so both
  its range and its sign were chosen rather than derived. `+1` is pronation.

  Its range is `±179°`, and the missing degree is deliberate: `GetEuler` returns angles in
  `(-180°, +180°]`, so at exactly half a turn the pose is correct while the **read-back
  inverts** — a commanded `+1` reports as `-1`. Measured, not assumed. One degree short and
  the round-trip is exact at every value.
- **BREAKING: one address per control, and the manifest names it.** The suffix appears
  exactly where it carries information: a digit that bends one way keeps its bare name,
  because `index` cannot mean anything else, while the thumb and the wrist name their
  axes. Five second spellings that VHI accepted without ever advertising them are gone,
  and sending one is now **refused**:

  | Retired | Send instead |
  | --- | --- |
  | `vhi.prediction.index.flexion` | `vhi.prediction.index` |
  | `vhi.prediction.middle.flexion` | `vhi.prediction.middle` |
  | `vhi.prediction.ring.flexion` | `vhi.prediction.ring` |
  | `vhi.prediction.little.flexion` | `vhi.prediction.little` |
  | `vhi.prediction.thumb` | `vhi.prediction.thumb.flexion` (or `.abduction`) |

  The same five spellings on `vhi.control.pose.*` are gone from the resolver too; they
  were never reachable over `SetControl`, and a control-pose DOF is driven by publishing
  under the address VHI advertises, which was never one of them.

  **The manifest does not change** — it never carried these — so a client that already
  resolves against `GetControlManifest` is unaffected, and one that hard-coded a spelling
  by analogy is told what to send: a refused address whose replacement differs by one
  trailing segment comes back as `not renderable — did you mean vhi.prediction.index?
  See GetControlManifest`, with the suggestion read out of the live table rather than out
  of a list of retired names, which would be the second vocabulary again.
- **A standard `+1` extended every digit instead of flexing it.** Both hands converted
  standard values by negating all nine channels on ingest, reasoning that "the flexion
  gains are negative". That is backwards: the gain table *is*
  `MovementPoses[Movements.Fist]`, the fully-closed hand, so a multiplier of `+1` already
  puts a digit at max flexion — `WaveBone_7` at `+85°` in rig-native degrees, where
  `IndexExtension` puts it at `-20°`. Negating it rendered `-85°`, an opening hand.

  The rule now lives in one place, `Vhi.StandardPose`, beside the pose library that
  justifies it: `+1` for the five flexion channels, `-1` for thumb abduction alone,
  because the fist's thumb Z is *ad*duction and no movement in the library goes the other
  way. Getting that one channel right by negating everything is why the bug was not
  obvious — abduction was correct, and five DOFs were not.

  `tests/test_v2_contract.py` now derives its expectation from the movement library rather
  than from a sweep. That mattered: the old expectation table had been filled in *from* the
  negating rig, so the suite agreed with itself while both disagreed with the DOF
  names. Restoring the blanket negation fails 11 assertions.
- **The standard conversion is unconditional, and no longer claims otherwise.** The
  ingest comment said it was "gated behind the handshake" while negating regardless of
  what a client sent. There is one encoding and no field left to name another: it must
  not be possible for the same `+1` to render two ways.
- **An over-range pose sample could invert a joint.** The gRPC path clamped to `[-1, 1]`;
  the LSL path did not. Past `±90°` the Euler round-trip used to read a bone back wraps and
  changes sign, so a sample beyond `±1.06` on an `85°` gain flipped the read-back. Both
  paths clamp now.
- `VHI_Predict` publishes standard values, so pushing `+1` on `vhi.prediction.index` and
  reading channel 2 of that stream returns `+1` — VHI is the identity rather
  than a sign flip.
  `VHI_Control` publishes standard values too, so a fist is the same
  `[1, -1, 1, 1, 1, 1, 0, 0, 0]` on the stream you train from and the one you drive.
- `tools/gen_api_docs.sh` no longer hard-codes the `Myogestic.Vhi.V1` namespace in its
  post-processing globs, so a namespace bump does not leave it dying partway with a
  `FileNotFoundError` after having already regenerated the tree.

## [1.0.0] - 2026-05-30

Initial public release.
