# gRPC API

The `VhiControl` service - VHI's [discrete control plane](../concepts/grpc-control.md).
VHI hosts it; clients (e.g. MyoGestic) connect over HTTP/2 cleartext, default
`127.0.0.1:50051`. All RPCs are **unary**; the return value is the
acknowledgement.

## RPCs

| RPC | Request | Returns | Notes |
|---|---|---|---|
| `SetMovement` | `SetMovementRequest` | `CommandAck` | Select a predefined movement. `cycle=false` holds the end pose; `cycle=true` plays the loop. Rejected outside `Movement` mode. |
| `Freeze` | `FreezeRequest` | `CommandAck` | Freeze / release the control hand at its current pose. Rejected outside `Movement` mode. |
| `SetSpeed` | `SetSpeedRequest` | `CommandAck` | Movement animation timing (frequency, hold, rest). Rejected outside `Movement` mode. |
| `SetSmoothing` | `SetSmoothingRequest` | `CommandAck` | Toggle predicted-hand smoothing and its speed. |
| `SetChirality` | `SetChiralityRequest` | `CommandAck` | **Not implemented** - returns `applied=false`. |
| `SetSessionActive` | `SetSessionActiveRequest` | `CommandAck` | Mark a recording session active/inactive; gates VHI's local keyboard. Orthogonal to driver mode. |
| `SetControlMode` | `SetControlModeRequest` | `CommandAck` | Switch the control-hand [driver mode](../concepts/control-modes.md). |
| `GetState` | `GetStateRequest` | `StateReply` | Query state; doubles as a connection handshake and movement-name discovery. |

## Messages

### `CommandAck` - returned by every command RPC

| Field | Type | Meaning |
|---|---|---|
| `applied` | `bool` | Whether the command took effect. |
| `current_state` | `string` | `waiting` \| `closing` \| `holding` \| `opening` \| `resting` \| `frozen`. |
| `current_movement` | `string` | The currently selected movement name. |
| `message` | `string` | Human-readable reason when `applied == false`. |

### `StateReply` - returned by `GetState`

| Field | Type | Meaning |
|---|---|---|
| `current_state` | `string` | As in `CommandAck`. |
| `current_movement` | `string` | The currently selected movement name. |
| `session_active` | `bool` | Whether a recording session is marked active. |
| `available_movements` | `repeated string` | Valid `SetMovement` names for the current mode - discover, don't hard-code. |
| `mode` | `string` | `"AI"` or `"Classifier"` - the movement-set mode. |
| `control_mode` | `ControlMode` | `MOVEMENT` \| `STREAM` \| `IDLE`. |

### `ControlMode` enum

| Value | Meaning |
|---|---|
| `MOVEMENT` (0) | Predefined-movement state machine + keyboard. The default. |
| `STREAM` (1) | Continuous pose from the `MyoGestic_ControlPose` LSL inlet. |
| `IDLE` (2) | Hold the rest pose; ignore keyboard, stream, and movement commands. |

### Request messages

| Message | Fields |
|---|---|
| `SetMovementRequest` | `string movement_name`, `bool cycle` |
| `FreezeRequest` | `bool frozen` |
| `SetSpeedRequest` | `float frequency_hz`, `float hold_time_s`, `float rest_time_s` |
| `SetSmoothingRequest` | `bool enabled`, `float smoothing_speed` |
| `SetChiralityRequest` | `bool right_hand` |
| `SetSessionActiveRequest` | `bool active` |
| `SetControlModeRequest` | `ControlMode mode` |
| `GetStateRequest` | *(empty)* |

## The full contract

`proto/myogestic_vhi.proto` is the canonical source - MyoGestic vendors a copy
and regenerates its stubs from it.

```protobuf
--8<-- "proto/myogestic_vhi.proto"
```
