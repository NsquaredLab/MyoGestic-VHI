# Virtual Hand Interface - Godot Version

Real-time 3D hand visualization for HD-sEMG data using Lab Streaming Layer (LSL).

## Overview

The Godot version provides:
- **Dual-hand visualization**: Control hand (predefined movements) and Predicted hand (EMG-driven)
- **3D hand models**: High-quality rigged hand with 16-bone skeleton
- **LSL integration**: Receives hand movement predictions from EMG analysis
- **Movement library**: 24 predefined hand movements (AI and Classifier modes)
- **UI controls**: Real-time adjustment of speed, smoothing, and hand chirality
- **Cross-platform**: Windows and Linux builds

## Requirements

### Godot
- Godot 4.x with .NET support (.NET version)
- .NET 6.0 SDK or later

### LSL Library
The project uses SharpLSL (LSL C# bindings) installed via NuGet:
```bash
cd godot-project
dotnet restore
```

### Testing Tools (Python)
For testing LSL communication:
```bash
pip install pylsl numpy
```

## Quick Start

### 1. Open the Project
```bash
# Open in Godot Editor
godot --path /path/to/godot-project

# Or just open Godot and select the project folder
```

### 2. Test with Simulated Data
```bash
# Terminal 1: Start LSL test sender
python test_lsl_sender.py

# Terminal 2: Run in Godot (press F5)
# Or from command line:
godot --path /path/to/godot-project
```

### 3. Use with Real EMG System
Configure your EMG pipeline to output LSL stream:
- Stream name: `HandPredictions`
- Stream type: `EMG`
- Channels: 16 (joint angles in degrees)
- Rate: ~60 Hz

## Architecture

### Main Components

**HandMain.tscn** - Main scene containing:
- `LSLCommunicationController` - LSL stream management
- `ControlHand` - Left hand with predefined movements
- `PredictedHand` - Right hand driven by LSL data
- `ControlPanel` - UI for adjusting parameters
- `Camera3D` - Orthographic camera view

### Scripts

1. **LSLCommunicationController.cs**
   - Manages LSL inlet for receiving predictions
   - Non-blocking stream resolution (runs in background thread)
   - Thread-safe data handling

2. **ControlHandSkeleton.cs**
   - Controls the control hand visualization
   - Plays predefined movements from `MovementDefinitions`
   - Movement state machine: waiting → closing → holding → opening → resting
   - Configurable speed (Hz), hold time, rest time

3. **PredictedHandSkeleton.cs**
   - Visualizes predicted hand movements from EMG data
   - Receives 16 joint angles from LSL stream
   - Optional smoothing (Slerp interpolation for quaternions)
   - Configurable smoothing speed

4. **MovementDefinitions.cs**
   - Defines 24 hand movements with joint angles
   - AI mode: 17 movements (thumb, index, middle, ring, pinky, complex grasps)
   - Classifier mode: 11 movements (basic single-finger and grasps)

5. **ControlPanelUI.cs**
   - UI panel for real-time parameter adjustment
   - Hand chirality toggle (left/right hand mirroring)
   - Speed, hold time, rest time sliders for control hand
   - Smoothing toggle and speed for predicted hand
   - Collapsible panel, scales with window size

6. **MovementUI.cs**
   - Displays current movement name and state
   - Shows keyboard controls

7. **LSLWrapper.cs**
   - C# wrapper for SharpLSL native library
   - Handles stream resolution and data pulling

### Hand Model

**WVRLeftHand_1106_ASCII.fbx**
- 26-bone skeleton (WaveBone_0 through WaveBone_25)
- 16 animated bones for hand joints:
  - Wrist (WaveBone_1)
  - Thumb: proximal, middle, distal (WaveBone_3, 4, 5)
  - Index: proximal, middle, distal (WaveBone_7, 8, 9)
  - Middle: proximal, middle, distal (WaveBone_12, 13, 14)
  - Ring: proximal, middle, distal (WaveBone_17, 18, 19)
  - Pinky: proximal, middle, distal (WaveBone_22, 23, 24)

**Note**: Metacarpal bones (WaveBone_2, 6, 11, 16, 21) are structural and not animated.

### Data Format

**LSL Input Stream (HandPredictions):**
- 16 float values (joint angles in degrees)
- Order matches bone indices above
- Non-blocking pull (0.0 timeout)

**Joint Rotation Format:**
- Each joint: `[X_rotation, Y_rotation, Z_rotation]` in degrees
- Applied as Euler angles to bone local rotation

## UI Controls

### Keyboard (Movement Selection)
- **Left Arrow**: Previous movement
- **Right Arrow**: Next movement
- **Down Arrow**: Start movement
- **Up Arrow**: Stop movement

### Control Panel (collapsible, right side)

**Hand Chirality**
- Right Hand (mirror): Toggle between right-handed (checked) and left-handed (unchecked)

**Control Hand**
- Speed (Hz): Movement cycle frequency (0.05 - 2.0 Hz)
- Holding Time: Duration at maximum flexion (0 - 10 seconds)
- Resting Time: Duration at rest position (0 - 10 seconds)

**Predicted Hand**
- Enable Smoothing: Toggle smooth interpolation (for classifier-based predictions)
- Speed: Smoothing interpolation factor (1 - 20)

## Available Movements

### AI Mode (17 movements)
- **Single finger**: Thumb, Index, Middle, Ring, Pinky
- **Grasps**: Fist, TwoFingerPinch, ThreeFingerPinch, Pointing
- **Extensions**: ThumbExtension, IndexExtension, MiddleExtension, RingExtension, PinkyExtension
- **Wrist**: WristUpDown, WristLeftRight
- **Rest**: Rest (neutral position)

### Classifier Mode (15 movements)
- **Single finger**: Thumb, Index, Middle, Ring, Pinky
- **Grasps**: Fist, TwoFingerPinch, ThreeFingerPinch, PrecisionSphere
- **Complex**: RockNRoll, Hook, PeaceSign, Pistol, ExtendedHand
- **Rest**: Rest (neutral position)

## Configuration

### LSL Stream Settings
Edit in Godot Inspector → `Main/LSLCommunicationController`:
- `PredictionStreamName`: "HandPredictions" (default)
- `PredictionStreamType`: "EMG" (default)

### Hand Settings
Edit in Godot Inspector:

**Main/ControlHand**:
- `EnableDataStream`: false (uses predefined movements)
- `EnableMovementControl`: true (plays movements)
- `Mode`: AI or Classifier
- `Frequency`: 0.5 Hz (default)
- `HoldTime`: 1.0 s
- `RestTime`: 1.0 s

**Main/PredictedHand**:
- `EnableSmoothing`: false (set via UI at runtime)
- `SmoothingSpeed`: 5.0 (set via UI at runtime)

### Visual Settings

**Project Settings (project.godot)**:
- MSAA 3D: 4x (anti-aliasing)
- Anisotropic Filtering: 4x (texture quality)
- Compatibility renderer (OpenGL ES 3.0)

**Environment (HandMain.tscn)**:
- Background color: Light blue-grey
- Orthographic camera
- Directional light

## Building Executables

### Export Presets
Configured in `export_presets.cfg`:
- Windows Desktop (64-bit)
- Linux/X11 (64-bit)

### Build Commands
```bash
# Windows
godot --headless --export-release "Windows Desktop" VHI_windows.exe

# Linux
godot --headless --export-release "Linux/X11" VHI_linux

# Include .NET runtime and LSL native libraries
```

### Build Outputs
Export will bundle:
- Godot runtime
- .NET 6.0 runtime
- SharpLSL native libraries (liblsl64.dll / liblsl64.so)
- All scripts and assets

## Testing

### Test 1: Predefined Movements (No LSL)
```bash
godot --path /path/to/godot-project
# Use arrow keys to cycle through movements
# Press Down to start, Up to stop
```

### Test 2: LSL Input
```bash
# Terminal 1: Send test patterns
python test_lsl_sender.py

# Terminal 2: Run Godot
godot --path /path/to/godot-project
# Predicted hand should follow the test patterns
```

### Test 3: Monitor Output
```bash
# Terminal 1: Run Godot
godot --path /path/to/godot-project

# Terminal 2: Receive streams (if outlets enabled)
python test_lsl_receiver.py
```

## Troubleshooting

### No LSL streams found
- Check that `test_lsl_sender.py` is running
- Verify stream name matches (case-sensitive): "HandPredictions"
- Check firewall settings
- LSL auto-discovery may take a few seconds

### SharpLSL errors
```bash
# Restore NuGet packages
cd godot-project
dotnet restore

# Verify .NET SDK
dotnet --version  # Should be 6.0+
```

### Hand not moving
- **Control hand**: Check that `EnableMovementControl = true`
- **Predicted hand**: Verify LSL stream is active and data format is correct (16 floats)
- Check Godot console for errors (View → Debug → Output)

### Performance issues
- Disable smoothing on predicted hand
- Reduce MSAA quality in project settings
- Check LSL sender rate (should not exceed 60 Hz)

### Hand appears flipped/wrong
- Use "Right Hand (mirror)" toggle in control panel
- Default is right-handed; uncheck for left-handed

### UI scaling issues
- UI auto-scales with window size
- Base resolution: 1152x648
- Panel is collapsible (click ">" button on right edge)

## File Structure

```
godot-project/
├── HandMain.tscn                    # Main scene
├── ControlPanel.tscn                # UI control panel scene
├── LSLCommunicationController.cs    # LSL stream manager
├── ControlHandSkeleton.cs           # Control hand logic
├── PredictedHandSkeleton.cs         # Predicted hand logic
├── MovementDefinitions.cs           # 24 movement poses
├── ControlPanelUI.cs                # UI panel controller
├── MovementUI.cs                    # Movement display overlay
├── LSLWrapper.cs                    # LSL C# wrapper
├── models/
│   └── WVRLeftHand_1106_ASCII.fbx  # Hand 3D model + skeleton
├── images/
│   ├── logo.png                     # FAU logo
│   └── n-squared lab Logo White.png
├── lib/
│   └── liblsl64.{dll,so}           # LSL native libraries
├── test_lsl_sender.py              # Python test data sender
├── test_lsl_receiver.py            # Python stream monitor
├── project.godot                   # Godot project config
├── export_presets.cfg              # Export build settings
├── VHI_godot.csproj               # C# project config
└── README.md                       # This file
```

## Differences from Unity Version

### Architecture
- ✅ Godot 4.x (MIT license) vs Unity (proprietary)
- ✅ LSL streams vs UDP sockets
- ✅ Automatic stream discovery vs manual IP configuration
- ✅ C# with Godot API vs Unity API

### Features Added
- Collapsible UI control panel
- Real-time parameter adjustment (no restart needed)
- Hand chirality toggle (left/right hand)
- Window-responsive UI scaling
- Anti-aliasing (MSAA 4x)
- Skin-colored control hand for visual distinction

### Features Maintained
- Same hand model and skeleton structure
- Same 24 movement definitions
- Same joint angle calculations
- AI and Classifier modes
- Smooth interpolation for predictions

### Simplified
- Removed recording system (can be added later)
- Removed error tracking/visualization (can be added later)
- Removed complex UI manager (simplified to control panel)
- Focus on core functionality: visualization and LSL streaming

## Integration with EMG Systems

To integrate with your EMG processing pipeline:

1. **Configure your EMG system** to output LSL stream:
   ```python
   from pylsl import StreamInfo, StreamOutlet

   # Create stream
   info = StreamInfo('HandPredictions', 'EMG', 16, 60, 'float32', 'myuid123')
   outlet = StreamOutlet(info)

   # Send predictions (16 joint angles in degrees)
   joint_angles = [0.0] * 16  # Your predicted angles
   outlet.push_sample(joint_angles)
   ```

2. **Run Godot application**
   - Will auto-discover "HandPredictions" stream
   - Predicted hand will visualize the data in real-time

3. **Adjust settings** via UI panel as needed

## License

MIT License (Godot engine)

## Contact

N-squared Lab, Friedrich-Alexander-Universität Erlangen-Nürnberg (FAU)

For issues or questions, refer to the main project documentation.
