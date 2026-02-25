# Virtual Hand Interface

Real-time 3D hand visualization for HD-sEMG data using Lab Streaming Layer (LSL).

Built with Godot 4.5 (.NET) for the N-squared Lab at FAU Erlangen-Nürnberg.

## Features

- **Dual-hand visualization** - Control hand (predefined movements) and Predicted hand (EMG-driven)
- **LSL integration** - Bidirectional streaming: receives predictions, outputs hand state
- **Movement library** - 17 AI-mode and 15 Classifier-mode hand movements
- **UI controls** - Real-time adjustment of speed, smoothing, and hand chirality
- **Cross-platform** - Windows and Linux builds

## Requirements

- [Godot 4.5](https://godotengine.org/) with .NET support
- .NET 9.0 SDK
- Python 3 with `pylsl` and `numpy` (for test scripts)

## Quick Start

```bash
# 1. Restore NuGet packages
dotnet restore

# 2. Open in Godot and press F5, or:
godot --path .

# 3. (Optional) Send test data from another terminal
python tests/test_lsl_sender.py
```

Use arrow keys to cycle movements (Left/Right) and start/stop them (Down/Up).

### Use with a Real EMG System

Configure your pipeline to output an LSL stream matching:
- **Stream name:** `MyoGestic_Output`
- **Stream type:** `MyoGestic_9DVector`
- **Channels:** 9 floats (thumb flexion/abduction, index, middle, ring, pinky, wrist flexion/abduction/rotation)

```python
from pylsl import StreamInfo, StreamOutlet

info = StreamInfo('MyoGestic_Output', 'MyoGestic_9DVector', 9, 32, 'float32', 'myuid')
outlet = StreamOutlet(info)
outlet.push_sample([0.0] * 9)
```

## Configuration

All settings are adjustable in the Godot Inspector or at runtime via the collapsible UI panel.

| Setting | Default | Location |
|---|---|---|
| `PredictionStreamName` | `MyoGestic_Output` | `LSLCommunicationController` |
| `PredictionStreamType` | `MyoGestic_9DVector` | `LSLCommunicationController` |
| `ExpectedChannels` | 9 | `LSLCommunicationController` |
| `EnableOutlets` | true | `LSLCommunicationController` |
| `Mode` | AI | `ControlHand` |
| `Frequency` | 0.5 Hz | `ControlHand` |
| `HoldTime` / `RestTime` | 1.0 s | `ControlHand` |
| `EnableSmoothing` | false | `PredictedHand` |
| `SmoothingSpeed` | 5.0 | `PredictedHand` |

### LSL Outlet Streams

When `EnableOutlets` is true, the application also publishes:
- `VHI_Control` - Control hand joint state
- `VHI_Predict` - Predicted hand joint state
- `VHI_MovementState` - Current movement and state machine info
- `VHI_MenuState` - UI/menu state

## File Structure

```
├── scenes/
│   ├── HandMain.tscn                # Main scene
│   └── ControlPanel.tscn            # UI control panel
├── src/
│   ├── LSLCommunicationController.cs  # LSL stream management
│   ├── LSLWrapper.cs                  # SharpLSL native wrapper
│   ├── ControlHandSkeleton.cs         # Control hand logic & state machine
│   ├── PredictedHandSkeleton.cs       # Predicted hand visualization
│   ├── MovementDefinitions.cs         # Hand movement poses
│   ├── MovementConfigLoader.cs        # TOML config file loading
│   ├── MovementConfigGenerator.cs     # Config file generation
│   ├── ControlPanelUI.cs              # UI panel controller
│   └── MovementUI.cs                  # Movement name/state overlay
├── models/
│   ├── WVRLeftHand_1106_ASCII.fbx     # Left hand model (26-bone skeleton)
│   └── WVRRightHand_1106_ASCII.fbx    # Right hand model
├── tests/
│   ├── test_lsl_sender.py             # Simulated EMG data sender
│   ├── test_lsl_receiver.py           # Stream monitor
│   ├── test_all_streams.py            # Monitor all LSL streams
│   ├── test_movementstate_receiver.py # Movement state monitor
│   └── test_stream_info.py            # Stream info inspector
├── images/
│   └── logo.png                       # FAU logo
├── project.godot
├── VHI_godot.csproj
├── VHI_godot.sln
└── export_presets.cfg
```

## Building Executables

Export presets for Windows (64-bit) and Linux/X11 (64-bit) are configured in `export_presets.cfg`:

```bash
godot --headless --export-release "Windows Desktop" VHI_windows.exe
godot --headless --export-release "Linux/X11" VHI_linux
```

## Troubleshooting

| Problem | Solution |
|---|---|
| No LSL streams found | Verify sender is running; check stream name is `MyoGestic_Output` (case-sensitive); check firewall |
| SharpLSL errors | Run `dotnet restore`; verify `dotnet --version` shows 9.0+ |
| Control hand not moving | Check `EnableMovementControl = true` in Inspector |
| Predicted hand not moving | Verify LSL stream is active with 9 float channels |
| Hand appears flipped | Toggle "Right Hand (mirror)" in the control panel |

## Contact

N-squared Lab, Friedrich-Alexander-Universität Erlangen-Nürnberg (FAU)
