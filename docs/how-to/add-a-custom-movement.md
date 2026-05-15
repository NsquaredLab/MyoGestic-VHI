# Add a custom movement

Named movements are defined in a **TOML config**, not in code - so adding one
is an edit, not a rebuild. This is the right path when the gesture is
something you can name ahead of time. (For arbitrary runtime poses, see
[Stream a custom pose](stream-a-custom-pose.md) instead.)

## Where the config lives

On first run VHI generates `user://movements.toml`. Godot's `user://` resolves
to a per-project app-data directory:

| OS | Resolved path |
|---|---|
| macOS | `~/Library/Application Support/Godot/app_userdata/Virtual Hand Interface/movements.toml` |
| Windows | `%APPDATA%\Godot\app_userdata\Virtual Hand Interface\movements.toml` |
| Linux | `~/.local/share/godot/app_userdata/Virtual Hand Interface/movements.toml` |

The default path is the `ConfigFilePath` [export](../reference/configuration.md),
baked into the build at edit-time - so a compiled binary can't change its
default. To use a TOML kept under version control, open the **control panel**
(right side, `>` button) and click **Load Config File** to pick any TOML from
disk; VHI then watches *that* file for hot-reload instead. The same panel has
**Edit Config File**, which opens the current file in your OS's default text
editor.

## Edit and hot-reload

VHI watches the file with a `FileSystemWatcher` - **save the TOML and the hand
updates live**, no restart.

Each movement is a `[movements.<Name>]` table with one line per joint -
`joint_name = [x, y, z]`, the joint's **max-flexion** pose in Euler degrees.
There is no rest pose in the file; rest is always neutral (`[0, 0, 0]`). On the
X axis, **negative = flexion**, positive = extension. The generated file is the
best reference - open it and copy an existing entry like `Fist`, rename it, and
adjust the joint values.

```toml
# movements.toml - one movement entry (joint_name = [x, y, z], degrees)
[movements.MyGesture]
wrist          = [0, 0, 0]
thumb_proximal = [-20, 0, 0]
thumb_middle   = [-30, 0, 0]
thumb_distal   = [-30, 0, 0]
index_proximal = [-40, 0, 0]
# … index_middle, index_distal, then middle_*, ring_*, pinky_* (16 joints total)
```

## Make it selectable

Once the movement is in the config:

- it appears in `GetState().available_movements`, so any gRPC client can
  discover it;
- `SetMovement("MyGesture")` plays it (hold the end pose, or `cycle=True` for
  the loop - see [Control-hand modes](../concepts/control-modes.md));
- the keyboard ++arrow-left++ / ++arrow-right++ cycle through it;
- it shows up in MyoGestic's movement palette.

!!! tip "Keep names in sync"
    `SetMovement` matches by name. If MyoGestic sends class labels straight
    through to `SetMovement`, the classifier's class names and the movement
    names in this TOML need to agree - unknown names are rejected with
    `applied = false`.
