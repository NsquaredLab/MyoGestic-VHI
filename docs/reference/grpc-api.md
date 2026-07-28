# gRPC API

The v2 services — VHI's [control plane](../concepts/grpc-control.md). VHI hosts them;
clients (e.g. MyoGestic) connect over HTTP/2 cleartext, default `127.0.0.1:50051`. All
RPCs are **unary**; the return value is the acknowledgement.

There are two services, and the split is deliberate. `VhiCanonicalControl` renders what
an application *controls*, addressed by canonical DOF name. `VhiTrainingAid` carries
what a recording *session* needs. Keeping them apart is what stops data collection from
bending the meaning of a control: a discrete DOF is a held state, and it stays one even
while a training program is deliberately sweeping the control hand.

!!! warning "The legacy `VhiControl` service (v1) has been removed"
    A client that still speaks it receives `UNIMPLEMENTED` — the signal v2's `Declare`
    handshake reads to recognise a build it cannot negotiate with. Its capabilities were
    split by *kind* rather than moved wholesale: `SetMovement` became a canonical
    discrete DOF, `SetSessionActive` and movement cycling became the recording aid, and
    `SetSmoothing` became `SetPresentation`. `Freeze`, `SetSpeed`, `SetChirality` and
    `SetControlMode` had no consumer and were not replaced.

## RPCs

### `VhiCanonicalControl`

| RPC | Request | Returns | Notes |
|---|---|---|---|
| `Declare` | `DeclareRequest` | `DeclareReply` | Negotiate a control space by name. Per-DOF verdicts, the continuous channel order, how to encode it, and whether the renderer blends. Call before streaming. |
| `SetControl` | `SetControlRequest` | `ControlAck` | Command one frame: `continuous` by name, `discrete` by state. Refusals are named in `rejected`, never silent. |
| `SweepControl` | `SweepControlRequest` | `SweepControlReply` | Drive one DOF across its range; reports which rig elements moved and the signed degrees, read back off the skeleton. |
| `SetPresentation` | `SetPresentationRequest` | `ControlAck` | Renderer blending. Appearance only — never a substitute for a client-side debounce. |

### `VhiTrainingAid`

| RPC | Request | Returns | Notes |
|---|---|---|---|
| `SetRecordingSession` | `SetRecordingSessionRequest` | `TrainingAck` | Mark a recording session active; gates VHI's local keyboard so the session has one movement source. |
| `StartTrainingProgram` | `StartTrainingProgramRequest` | `TrainingAck` | Cycle the control hand through a movement to generate a trajectory. Refused if one is already running. |
| `StopTrainingProgram` | `StopTrainingProgramRequest` | `TrainingAck` | Stop it and rest the hand. Idempotent. |
| `GetTrainingState` | `GetTrainingStateRequest` | `TrainingState` | State, the current movement, and the movement names a program may use. |

While a training program runs it **owns** the control hand: `SetControl`'s discrete DOFs
are refused with the reason rather than interrupting the trajectory a recording is being
aligned against. Continuous DOFs are unaffected — they drive the predicted hand.

`proto/myogestic_vhi_v2.proto` is the canonical source - MyoGestic vendors a copy
and regenerates its stubs from it.

```protobuf
--8<-- "proto/myogestic_vhi_v2.proto"
```
