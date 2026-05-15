# Getting Started

This walks you from a fresh clone to a running VHI window with a hand moving.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| **Godot** | 4.6 **.NET** build | The Mono/.NET edition - *not* the standard build. C# won't load otherwise. |
| **.NET SDK** | **8.0** | The project targets `net8.0` to match Godot 4.6's runtime. A .NET 8 SDK is also required for [exporting](how-to/build-and-export.md). |
| **Python 3** | 3.x | Only for the LSL test scripts in `tests/` (`pip install pylsl numpy`). Optional. |

!!! warning "Use the .NET build of Godot"
    On macOS the .NET build is `Godot.app` reported as `…mono…` by
    `Godot --version`. The standard (non-.NET) build cannot run VHI.

## Restore and run

```bash
# 1. Restore NuGet packages (SharpLSL, Grpc.AspNetCore, Tomlyn)
dotnet restore

# 2. Open the project in the Godot editor and press F5 - or run headless:
godot --path .
```

On first run VHI:

- builds the C# assembly,
- opens the main scene (`scenes/HandMain.tscn`) with the **control** and
  **predicted** hands,
- starts the [gRPC control server](concepts/grpc-control.md) on
  `127.0.0.1:50051`,
- begins resolving its [LSL inlets](concepts/lsl-streams.md)
  (`MyoGestic_Output`, `MyoGestic_ControlPose`).

You should see a console line like:

```text
✅ gRPC control server listening on 127.0.0.1:50051
```

## See a hand move

Out of the box both hands sit at rest - VHI is *waiting* for input. Two quick
ways to get motion:

=== "Keyboard (control hand)"

    With the window focused, the **control hand** responds to:

    | Key | Action |
    |---|---|
    | :material-arrow-left: / :material-arrow-right: | cycle the selected movement (wraps around at both ends) |
    | :material-arrow-down: | start the movement |
    | :material-arrow-up: | stop, return to rest |
    | ++space++ | freeze / unfreeze at the current pose |

    Cycling is the keyboard equivalent of gRPC `SetMovement` - both pick a
    movement by name from the same `available_movements` list. The
    programmatic path is `client.set_movement(name)` (see
    [gRPC control](concepts/grpc-control.md)).

=== "LSL stream (predicted hand)"

    Send a mock 9-channel stream named `MyoGestic_Output` and the **predicted
    hand** follows it:

    ```bash
    python tests/test_lsl_sender.py
    ```

    See [LSL streams](concepts/lsl-streams.md) for the channel layout.

=== "gRPC (control hand)"

    Command the control hand directly. From any gRPC client (here, via
    MyoGestic's generated stubs):

    ```python
    from myogestic.interfaces import virtual_hand
    client = virtual_hand().control_client()
    client.set_movement("Fist")     # control hand snaps to the Fist pose
    ```

    See [Drive VHI from MyoGestic](how-to/drive-from-myogestic.md).

## Next steps

- Understand the moving parts → [Concepts](concepts/index.md)
- Wire it to MyoGestic → [Drive VHI from MyoGestic](how-to/drive-from-myogestic.md)
- Ship a build → [Build and export](how-to/build-and-export.md)
