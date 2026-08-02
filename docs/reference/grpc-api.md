# gRPC API

`VhiControl` — VHI's [control plane](../concepts/grpc-control.md). VHI hosts it;
clients (e.g. MyoGestic) connect over HTTP/2 cleartext, default `127.0.0.1:50051`. All
RPCs are **unary**; the return value is the acknowledgement.

One service carries both what an application *controls*, addressed by standard DOF
name, and what a recording *session* needs. They stay conceptually separate even
though they share a service: a discrete DOF is a held state, and it stays one even
while a recording trajectory is deliberately sweeping the control hand.

!!! warning "The legacy `VhiControl` service (v1) has been removed"
    A client that still speaks it receives `UNIMPLEMENTED` — the same signal a current
    client gets from `GetControlManifest` against a build too old to answer, and how it
    recognises a renderer it cannot drive. Its capabilities were
    split by *kind* rather than moved wholesale: `SetMovement` became a standard
    discrete DOF, `SetSessionActive` and movement cycling became the recording session
    RPCs, and `SetSmoothing` became `SetPresentation`. `Freeze`, `SetSpeed`, `SetChirality`
    and `SetControlMode` had no consumer and were not replaced.

## RPCs

### `VhiControl`

| RPC | Request | Returns | Notes |
|---|---|---|---|
| `GetControlManifest` | `GetControlManifestRequest` | `ControlManifest` | Every address VHI exports, each with its kind and its range or its states — plus the `vocabulary_version` to gate on. A streamed DOF's **address is its stream name**: there is no separate stream name or channel number, because a stream is one DOF and one `float32` channel. **Call this first**, unconditionally — there is nothing else to open, declare or negotiate. |
| `SetControl` | `SetControlRequest` | `ControlAck` | Command one frame. Both maps are keyed by **address**, as published in the manifest: the key says which control, the value says the number or the held state. Refusals are keyed by the same address in `rejected`, never silent. |
| `SweepControl` | `SweepControlRequest` | `SweepControlReply` | Drive one DOF across its range; reports which rig elements moved and the signed degrees, read back off the skeleton. |
| `SetPresentation` | `SetPresentationRequest` | `ControlAck` | Renderer blending. Appearance only — never a substitute for a client-side debounce. |
| `SetRecordingSession` | `SetRecordingSessionRequest` | `RecordingAck` | Mark a recording session active; gates VHI's local keyboard so the session has one movement source. |
| `StartRecordingTrajectory` | `StartRecordingTrajectoryRequest` | `RecordingAck` | Cycle the control hand through a movement to generate a trajectory. Refused if one is already running. |
| `StopRecordingTrajectory` | `StopRecordingTrajectoryRequest` | `RecordingAck` | Stop it, resting the hand only if one was running. Idempotent. |
| `GetRecordingSessionState` | `GetRecordingSessionStateRequest` | `RecordingSessionState` | State, the current movement, and the movement names a trajectory may use. |

While a recording trajectory runs it **owns** the control hand: `SetControl`'s discrete
DOFs are refused with the reason rather than interrupting the trajectory a recording is
being aligned against. A live `vhi.control.pose.*` stream owns it the same way — any one
of the nine is enough — and refuses the same commands. See
[What drives the control hand](../concepts/control-hand-drivers.md). Continuous DOFs are
unaffected either way; they drive the predicted hand.

## `vocabulary_version` — check it, and refuse below it

`ControlManifest.vocabulary_version` is a decimal integer, compared numerically. This
build reports **`"2"`**, and it is load-bearing: a client declares the oldest vocabulary
it can drive and refuses anything below it, by name, at bind. MyoGestic declares a minimum
of 2.

| Vocabulary | The transport it describes |
|---|---|
| `1` | a manifest carrying `stream_name` and `channel` per capability; several controls could share one wider stream. **Retired.** |
| `2` | one stream per DOF, named for the address, one `float32` channel wide. |

VHI and its clients are separately installed applications, so upgrading one does not
upgrade the other. Without the gate a skewed pair fails silently in both directions: an
old renderer waits for a wide pose stream nobody publishes any more and logs nothing, and
a new renderer refuses an old client's wide stream at the LSL layer instead — either way
the hand does not move, and only one of those two says so out loud. The version check is
the one place both sides' versions are visible at once.

!!! note "`stream_name` and `channel` are gone from `ControlCapability`"
    Field numbers `10` and `11` are `reserved`, and so are the names `stream_name` and
    `channel` — a later field reusing either spelling would read as the old one in JSON or
    text format to anything still carrying the v1 schema, which reserving the numbers
    alone does not prevent. Code that read `cap.stream_name` or `cap.channel` raises
    `AttributeError` against a regenerated stub; use `cap.address` as the stream name and
    `cap.kind` to tell a streamed control from a held state.

## The full contract

`proto/myogestic_vhi.proto` is the authoritative source - MyoGestic vendors a copy
and regenerates its stubs from it.

```protobuf
--8<-- "proto/myogestic_vhi.proto"
```
