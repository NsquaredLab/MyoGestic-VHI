# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`VhiControl` — the one gRPC control service, and `GetControlManifest` is its whole
  contract.** The manifest lists every **address** VHI exports
  (`vhi.prediction.index`, `vhi.control.gesture`) with what it can render for each: the
  kind, the range or the states, the LSL stream it is read from, and the channel it
  occupies there. A client calls it once, unconditionally, before it sends anything,
  and maps its own configuration's names onto those addresses. There is no per-client
  negotiation and nothing to declare — the manifest is the same for everyone, and VHI
  keeps no session state about who is talking to it. Neither side hard-codes a channel
  index.

  Each pose stream numbers its channels from zero, so a capability publishes
  `stream_name` beside `channel`: `vhi.prediction.index` and `vhi.control.pose.index`
  are both channel 2, on different streams and different hands.

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
  movement names a trajectory may use. These RPCs live on `VhiControl` rather than a
  service of their own — a standard discrete DOF is a *held state*, and a running
  trajectory must not redefine that, so while one runs it owns the control hand and
  discrete DOFs are refused with the reason.
- **The control hand follows the presence of its pose stream, and has no modes.**
  Publish the optional `MyoGestic_ControlPose` inlet and the control hand renders it;
  stop, and after `ControlPoseStaleAfterSeconds` (5 s) it stops any running trajectory,
  returns to rest, and resumes its own named movements. Publishing *is* the request:
  an inlet nobody reads is indistinguishable from a stream that is not arriving. This
  is exactly how the predicted hand has always followed `MyoGestic_Output`, and the two
  hands differing on it was the only reason a mode ever existed. The inlet's addresses
  are published in the manifest under `vhi.control.pose.*` — a namespace of their own,
  because they are a separate hand on a separate stream.

  A stream and a discrete DOF cannot both own those bones, so while the stream is live
  a discrete DOF is refused **by name**, with
  `'<movement>' was refused — a control-pose stream is driving the control hand` in
  `ControlAck.rejected`. v1 arbitrated the same conflict through `ControlMode`, where a
  client only ever saw commands quietly not apply.
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
- **BREAKING: the continuous LSL inlet takes standard values.** `MyoGestic_Output` now
  carries values where `+1` means the direction the DOF name denotes, so `+1` on index
  flexion *flexes*. v1 expected the renderer's own units of the day — the Unity-signed
  gain table, in which flexion was negative — so a v1 frame and a standard one of the
  same sign render opposite hands. There is exactly one encoding now, so nothing
  negotiates it: the field that
  once announced it is gone, and a client simply sends standard values.

  **All four streams are standard**, inlets and outlets alike — see `VHI_Control`
  above and `VHI_Predict` below. Nothing on the wire is in rig units any more.
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
  renderer it cannot drive.

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

  The direction anchor is no longer derived from anything the renderer also reads. It
  holds the movement whose *name* says what it is — `Movements.Index` is index flexion
  because a human called it that — and asserts what `VHI_Control` publishes.
- **A channel is an address, and both ends read it from one table.** The manifest says
  `vhi.prediction.index` is channel 2 and a producer writes it there; VHI reads its
  inlets positionally and reconstructs nothing. Reading a sender's channel labels back
  instead meant asking the inlet for its stream info, which is the only thing that
  starts liblsl's `info_receiver` thread — and cancelling that thread mid-request
  crashed the renderer. It bought a producer the right to send a narrower frame, which
  saved three floats.
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
  were never reachable over `SetControl`, and the control-pose stream is written by
  channel.

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
  negating renderer, so the suite agreed with the rig while both disagreed with the DOF
  names. Restoring the blanket negation fails 11 assertions.
- **The standard conversion is unconditional, and no longer claims otherwise.** The
  ingest comment said it was "gated behind the handshake" while negating regardless of
  what a client sent. There is one encoding and no field left to name another: it must
  not be possible for the same `+1` to render two ways.
- **An over-range pose sample could invert a joint.** The gRPC path clamped to `[-1, 1]`;
  the LSL path did not. Past `±90°` the Euler round-trip used to read a bone back wraps and
  changes sign, so a sample beyond `±1.06` on an `85°` gain flipped the read-back. Both
  paths clamp now.
- `VHI_Predict` publishes standard values, so pushing `+1` on `MyoGestic_Output` and
  reading that stream returns `+1` — the renderer is the identity rather than a sign flip.
  `VHI_Control` publishes standard values too, so a fist is the same
  `[1, -1, 1, 1, 1, 1, 0, 0, 0]` on the stream you train from and the one you drive.
- `tools/gen_api_docs.sh` no longer hard-codes the `Myogestic.Vhi.V1` namespace in its
  post-processing globs, so a namespace bump does not leave it dying partway with a
  `FileNotFoundError` after having already regenerated the tree.

## [1.0.0] - 2026-05-30

Initial public release.
