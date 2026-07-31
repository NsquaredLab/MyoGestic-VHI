# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`VhiControl` — the one gRPC control service.** An application declares which of
  VHI's **addresses** it drives (`vhi.prediction.index`, `vhi.control.gesture`),
  under whatever names its own configuration uses, and VHI answers with what
  it can render. Neither side hard-codes a channel index. `Declare` returns a per-DOF
  verdict, the continuous channel order, and whether the renderer blends. A DOF that
  cannot be rendered is reported with a reason and **never silently ignored** — an
  ignored joint looks exactly like a joint that is working and holding still.
- **Discrete DOFs render as control-hand movements.** States resolve case-insensitively
  against the movement names the build actually offers, discovered per call rather than
  from a table, because the movement set changes with the movement mode. A DOF where any
  state fails to resolve is refused outright: partly-resolvable is not partly-renderable,
  it is a DOF that silently does nothing some of the time.
- **`SweepControl` — verification without a human at the screen.** Drives one named DOF
  across its range and reports which rig elements moved and by how many *signed* degrees,
  read back off the skeleton. Turns "does `vhi.prediction.index.flexion` curl the index
  finger, in the
  flexion direction" into an assertion. It reports the model's own bone names, so a
  re-rig surfaces as a changed name rather than as a hand moving the wrong finger.
- **Recording-session coordination lives on the same service, deliberately not on the
  control plane.** `SetRecordingSession` gates VHI's local keyboard off so a recording
  has one movement source; `StartRecordingTrajectory` cycles the control hand through a
  movement so the recorded pose stream sweeps a continuous range instead of snapping
  between held states; `GetRecordingSessionState` reports session state plus the
  movement names a trajectory may use. These RPCs live on `VhiControl` rather than a
  service of their own — a canonical discrete DOF is a *held state*, and a running
  trajectory must not redefine that, so while one runs it owns the control hand and
  discrete DOFs are refused with the reason.
- **The optional `MyoGestic_ControlPose` inlet is under the same handshake, additively.**
  `DeclareRequest.control_pose` is a plain bool: unset (what every existing client does)
  changes nothing at all, and `true` also negotiates the control hand's pose stream and
  reports its channel order.

  Declaring the stream is also how a client asks for `Stream` mode, since there is no
  separate mode RPC — an inlet nobody reads is indistinguishable from a stream that is
  not arriving. Declaring it *together with* a discrete DOF is refused at the handshake:
  both drive the control hand's bones, and v1 arbitrated that per command via
  `ControlMode`, where a client only ever saw commands quietly not apply.
- **`SetPresentation` — renderer blending, named for what it is.** The third of three
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

- **BREAKING: the continuous LSL inlet takes canonical values.** `MyoGestic_Output` now
  carries values where `+1` means the direction the DOF name denotes, so `+1` on index
  flexion *flexes*. v1 expected the renderer's own convention, where flexion was
  negative. There is exactly one encoding now, so nothing negotiates it: the field that
  once announced it is gone, and a client simply sends canonical values.

  `VHI_Control` deliberately stays in the renderer's own units: the archived reference
  sessions and `myogestic.vhi.legacy.decode_pose` are pinned to them, so every session
  recorded before this release stays readable by the same decoder. `VHI_Predict` does
  not stay in those units — see Fixed, below.
- **`SweepControl`'s expectation is axis-aware.** Thumb abduction drives all three thumb
  bones through one channel, but the distal bone's Z gain is `0` — a channel-wide
  expectation reported a correct sweep as a mismatch.
- The `MyoGestic_Output` inlet is documented with its **verified** channel map. Channel 0
  is thumb *flexion* and channel 1 thumb *abduction*; the previous documentation had
  those swapped. Channels 6-8 are the wrist — see Fixed, below.
- **BREAKING: the control plane collapsed to one gRPC service, and none of this has a
  compatibility window.** `VhiTrainingAid` is gone; its RPCs move onto `VhiControl` and
  lose "training" from their names in the process (`StartTrainingProgram` →
  `StartRecordingTrajectory`, `StopTrainingProgram` → `StopRecordingTrajectory`,
  `GetTrainingState` → `GetRecordingSessionState`) — a recording aid that cannot see what
  the control service already declared to the same hand was two sources of truth for one
  state machine, not two independent responsibilities. The per-capability
  `ContinuousEncoding` field is gone from the manifest, and `control_pose_encoding`
  narrows to a plain `control_pose` bool on both `DeclareRequest` and `DeclareReply`: the
  sign convention it used to carry left the wire entirely once the predicted hand stopped
  computing one to negate. None of this degrades gracefully — an old MyoGestic against a
  new VHI, or the reverse, refuses to link at all: wrong service name, wrong RPC names, a
  field that no longer exists. **MyoGestic and VHI must be upgraded together**; there is
  no staged rollout and no version this pair is backward-compatible with.

### Removed

- **BREAKING: the v1 `VhiControl` service and `proto/myogestic_vhi.proto`.** A client
  that still speaks v1 receives `UNIMPLEMENTED`, which is exactly the signal v2's
  `Declare` handshake reads to recognise a build it cannot negotiate with.

  Capabilities were split by *kind* rather than moved wholesale: `SetMovement` → a
  canonical discrete DOF; `SetMovement(cycle=true)` and `SetSessionActive` → the
  recording aid; `SetSmoothing` → `SetPresentation`; `GetState` → `GetTrainingState`.
  `Freeze`, `SetSpeed`, `SetChirality` and `SetControlMode` were **not** replaced: each
  was a transport concept with no consumer, and `SetChirality` never worked — its handler
  always returned `applied=false`.

  See **[Upgrading to VHI 2.0](docs/upgrading-to-v2.md)** for the full migration.

### Fixed

- **The pose stream describes itself.** A producer that labels its LSL channels with
  control addresses may send **however many controls it drives, in any order** — two
  channels carrying `vhi.prediction.index` and `vhi.prediction.middle` is a complete
  stream, not a truncated nine. VHI reads the labels from the sender's own stream
  description and places each value by name.

  Unlabelled producers are unaffected: the wire *is* the pose order and is read
  positionally, exactly as before. Labels that are not addresses this hand renders — a
  producer naming its channels for a human — also fall back to positional rather than
  routing on a partial match, because dropping the channels that did not resolve would
  misattribute every later one.
- **The wrist renders.** `wrist.flexion` and `wrist.abduction` on both hands, channels 6
  and 7. Bone 0 is the common ancestor of all five digit chains, so turning it turns the
  whole hand — it was mapped, named "wrist", and never written to.

  The X extreme is derived: `Movements.WristUpDown` defines ±30° and negative X is flexion
  throughout this rig, so canonical `+1` is `-30°`. The Z **magnitude** is derived from
  `Movements.WristLeftRight` (±20°) but its **sign is a choice** — "left/right" names an
  axis, not a direction, and nothing in the library, the rig or the docs settles which side
  is abduction. It is taken by analogy with flexion and marked as such in
  `Vhi.StandardPose`; flipping it is one sign and a failing test.

  `wrist.rotation` renders too, on channel 8, and it is the one control here with **no
  rig-side evidence at all** — no movement in the library touches joint 0's Y axis, so both
  its range and its sign were chosen rather than derived. `+1` is pronation.

  Its range is `±179°`, and the missing degree is deliberate: `GetEuler` returns angles in
  `(-180°, +180°]`, so at exactly half a turn the pose is correct while the **read-back
  inverts** — a commanded `+1` reports as `-1`. Measured, not assumed. One degree short and
  the round-trip is exact at every value.
- **`thumb` is now `thumb.flexion`.** The thumb has two axes and a bare name did not say
  which; a single-axis digit keeps its bare name, because `index` cannot mean anything else.
  The suffix appears exactly where it carries information. The bare form is still accepted,
  so a declaration using it renders — but it is no longer advertised, and a client that
  validates against the manifest will refuse it.
- **`Declare` named controls the manifest did not advertise.** Its channel order was a
  hand-maintained literal, and after the aliases were trimmed it went on reporting five
  `.flexion` names the manifest had dropped — so a client building its frame from
  `continuous_channel_order` was handed addresses its own loader would reject. Both orders
  are derived from the same tables the manifest is built from now.
- **The control-pose order reported the *predicted* hand's addresses.** Both orders came
  from the prediction table, so a client declaring a control-pose stream was told its
  channels were `vhi.prediction.*` — the other hand's controls, which it would then have
  routed onto this one. One test asserted this and passed.
- **A canonical `+1` extended every digit instead of flexing it.** Both hands converted
  canonical values by negating all nine channels on ingest, reasoning that "the flexion
  gains are negative". That is backwards: the gain table *is*
  `MovementPoses[Movements.Fist]`, the fully-closed hand, so a multiplier of `+1` already
  puts a digit at max flexion — bone 7 at `-85°`, where `IndexExtension` puts it at
  `+20°`. Negating it rendered `+85°`, an opening hand.

  The rule now lives in one place, `Vhi.StandardPose`, beside the pose library that
  justifies it: `+1` for the five flexion channels, `-1` for thumb abduction alone,
  because the fist's thumb Z is *ad*duction and no movement in the library goes the other
  way. Getting that one channel right by negating everything is why the bug was not
  obvious — abduction was correct, and five DOFs were not.

  `tests/test_v2_contract.py` now derives its expectation from the movement library rather
  than from a sweep. That mattered: the old expectation table had been filled in *from* the
  negating renderer, so the suite agreed with the rig while both disagreed with the DOF
  names. Restoring the blanket negation fails 11 assertions.
- **The canonical conversion is no longer gated, and no longer claims to be.** The ingest
  comment said it was "gated behind the handshake" while negating regardless of what a
  client declared. The conversion is unconditional now, with no field left to say
  otherwise: it must not be possible for the same `+1` to render two ways depending on
  what was said during `Declare`.
- **An over-range pose sample could invert a joint.** The gRPC path clamped to `[-1, 1]`;
  the LSL path did not. Past `±90°` the Euler round-trip used to read a bone back wraps and
  changes sign, so a sample beyond `±1.06` on an `85°` gain flipped the read-back. Both
  paths clamp now.
- `VHI_Predict` publishes canonical values, so pushing `+1` on `MyoGestic_Output` and
  reading that stream returns `+1` — the renderer is the identity rather than a sign flip.
  `VHI_Control` is deliberately unchanged: the archived reference sessions are permanently
  in raw rig units and their reader is pinned to that.
- `tools/gen_api_docs.sh` no longer hard-codes the `Myogestic.Vhi.V1` namespace in its
  post-processing globs, so a namespace bump does not leave it dying partway with a
  `FileNotFoundError` after having already regenerated the tree.

## [1.0.0] - 2026-05-30

Initial public release.
