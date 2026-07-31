# gRPC API

`VhiControl` — VHI's [control plane](../concepts/grpc-control.md). VHI hosts it;
clients (e.g. MyoGestic) connect over HTTP/2 cleartext, default `127.0.0.1:50051`. All
RPCs are **unary**; the return value is the acknowledgement.

One service carries both what an application *controls*, addressed by standard DOF
name, and what a recording *session* needs. They stay conceptually separate even
though they share a service: a discrete DOF is a held state, and it stays one even
while a recording trajectory is deliberately sweeping the control hand.

!!! warning "The legacy `VhiControl` service (v1) has been removed"
    A client that still speaks it receives `UNIMPLEMENTED` — the signal `Declare`'s
    handshake reads to recognise a build it cannot negotiate with. Its capabilities were
    split by *kind* rather than moved wholesale: `SetMovement` became a standard
    discrete DOF, `SetSessionActive` and movement cycling became the recording session
    RPCs, and `SetSmoothing` became `SetPresentation`. `Freeze`, `SetSpeed`, `SetChirality`
    and `SetControlMode` had no consumer and were not replaced.

## RPCs

### `VhiControl`

| RPC | Request | Returns | Notes |
|---|---|---|---|
| `Declare` | `DeclareRequest` | `DeclareReply` | Negotiate a control space by name. Per-DOF verdicts, the continuous channel order, and whether the renderer blends. Call before streaming. |
| `SetControl` | `SetControlRequest` | `ControlAck` | Command one frame: `continuous` by name, `discrete` by state. Refusals are named in `rejected`, never silent. |
| `SweepControl` | `SweepControlRequest` | `SweepControlReply` | Drive one DOF across its range; reports which rig elements moved and the signed degrees, read back off the skeleton. |
| `SetPresentation` | `SetPresentationRequest` | `ControlAck` | Renderer blending. Appearance only — never a substitute for a client-side debounce. |
| `SetRecordingSession` | `SetRecordingSessionRequest` | `RecordingAck` | Mark a recording session active; gates VHI's local keyboard so the session has one movement source. |
| `StartRecordingTrajectory` | `StartRecordingTrajectoryRequest` | `RecordingAck` | Cycle the control hand through a movement to generate a trajectory. Refused if one is already running. |
| `StopRecordingTrajectory` | `StopRecordingTrajectoryRequest` | `RecordingAck` | Stop it, resting the hand only if one was running. Idempotent. |
| `GetRecordingSessionState` | `GetRecordingSessionStateRequest` | `RecordingSessionState` | State, the current movement, and the movement names a trajectory may use. |

While a recording trajectory runs it **owns** the control hand: `SetControl`'s discrete
DOFs are refused with the reason rather than interrupting the trajectory a recording is
being aligned against. Continuous DOFs are unaffected — they drive the predicted hand.

`proto/myogestic_vhi.proto` is the authoritative source - MyoGestic vendors a copy
and regenerates its stubs from it.

```protobuf
--8<-- "proto/myogestic_vhi.proto"
```
