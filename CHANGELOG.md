# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`VhiCanonicalControl` — the canonical control service.** An application declares
  which of VHI's **addresses** it drives (`vhi.prediction.index`, `vhi.control.gesture`),
  under whatever names its own configuration uses, and VHI answers with what
  it can render. Neither side hard-codes a channel index. `Declare` returns a per-DOF
  verdict, the continuous channel order, how to encode that stream, and whether the
  renderer blends. A DOF that cannot be rendered is reported with a reason and **never
  silently ignored** — an ignored joint looks exactly like a joint that is working and
  holding still.
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
- **`VhiTrainingAid` — a recording aid, deliberately not a control plane.** Carries the
  recording-session gate (stops VHI's local keyboard competing as a movement source) and
  training programs that cycle the control hand to generate a continuous trajectory for
  aligning EMG windows against. A separate service so the boundary is structural: a
  canonical discrete DOF is a *held state*, and collecting training data must not
  redefine that. While a program runs it owns the control hand and discrete DOFs are
  refused with the reason.
- **The optional `MyoGestic_ControlPose` inlet is under the same handshake, additively.**
  `DeclareRequest.control_pose_encoding` lets a client say which convention it will send:
  omitting it (what every existing client does) changes nothing at all, `LEGACY_NEGATED`
  gets the handshake while keeping renderer units, and `CANONICAL` reads the stream as
  canonical values. Unlike `MyoGestic_Output`, this stream's convention was **negotiated
  rather than changed**, so an existing renderer-unit producer needs no edit.

  Declaring the stream is also how a client asks for `Stream` mode, since v2 has no
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
  negative. `DeclareReply.continuous_encoding` announces which is in force and is not
  optional — a client reading `ENCODING_UNSPECIFIED` must fall back rather than guess.

  VHI's own outlets (`VHI_Control`, `VHI_Predict`) deliberately **did not** change: every
  session recorded before this release stays readable by the same decoder.
- **`SweepControl`'s expectation is axis-aware.** Thumb abduction drives all three thumb
  bones through one channel, but the distal bone's Z gain is `0` — a channel-wide
  expectation reported a correct sweep as a mismatch.
- The `MyoGestic_Output` inlet is documented with its **verified** channel map. Channel 0
  is thumb *flexion* and channel 1 thumb *abduction*; the previous documentation had
  those swapped and described channels 6-8 as a wrist. There are no wrist channels — 6-8
  are read by no consumer.

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

- `tools/gen_api_docs.sh` no longer hard-codes the `Myogestic.Vhi.V1` namespace in its
  post-processing globs, so a namespace bump does not leave it dying partway with a
  `FileNotFoundError` after having already regenerated the tree.

## [1.0.0] - 2026-05-30

Initial public release.
