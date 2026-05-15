# Movements

In [`Movement` mode](control-modes.md), the control hand plays from a set of
**named predefined movements**. This page covers the set, the two modes it
comes in, and the TOML file that defines the poses.

## The movement set

VHI ships 23 movement definitions (`Movements` in `src/MovementDefinitions.cs`):

> Rest, Thumb, Index, Middle, Ring, Pinky, Fist, TwoFingerPinch,
> ThreeFingerPinch, Pointing, ThumbExtension, IndexExtension, MiddleExtension,
> RingExtension, PinkyExtension, WristUpDown, WristLeftRight, PrecisionSphere,
> RockNRoll, Hook, PeaceSign, Pistol, ExtendedHand

A movement defines one **max-flexion** pose per animated joint (Euler degrees);
the rest pose is always neutral (`[0, 0, 0]`) and is not stored. The state
machine interpolates between rest and that max-flexion pose (see
[Control-hand modes](control-modes.md) for `cycle`).

## AI vs. Classifier mode

`MovementMode` selects *which subset* of movements is exposed:

| Mode | Count | Set |
|---|---|---|
| **AI** *(default)* | 17 | Rest, the 5 fingers, Fist, the two pinches, Pointing, the 5 extensions, WristUpDown, WristLeftRight |
| **Classifier** | 15 | Rest, the 5 fingers, Fist, the two pinches, PrecisionSphere, RockNRoll, Hook, PeaceSign, Pistol, ExtendedHand |

The `Mode` `[Export]` on `ControlHandSkeleton` (set in the Inspector) picks the
set. Filtering is by *exclusion*: a movement is hidden only if it belongs
exclusively to the other mode - so the standard config yields exactly the 17
or 15 above, while a fully custom TOML (movement names in neither set) is
exposed in full regardless of mode.

A gRPC client doesn't have to hard-code these - `GetState` returns
`available_movements` (the valid names for the current mode) and `mode`
(`"AI"` or `"Classifier"`). Discover, don't guess.

## Cycling through the set

The keyboard (++arrow-left++ / ++arrow-right++) walks the *currently
available* movement list - the same list `GetState.available_movements`
returns. Cycling is **circular**: pressing ++arrow-right++ at the end of the
list wraps to index 0, and ++arrow-left++ at index 0 wraps to the last entry.
No "end of list" stop state, no error, no `applied=false`.

The programmatic equivalent is gRPC `SetMovement` with the desired name -
which is also how a client *programmatically cycles* (it iterates over
`available_movements` itself and issues a `SetMovement` for each). There is
no "cycle by index" RPC by design: the client owns the iteration order so it
can do something other than wrap-forward if needed (skip Rest, randomise,
follow a predicted-class trajectory, etc.).

## The TOML config

The actual joint poses live in a TOML file, not in code:

- On first run VHI generates a default `user://movements.toml`
  (`MovementConfigGenerator`).
- `MovementConfigLoader` reads it into the in-memory pose set.
- A `FileSystemWatcher` **hot-reloads** the file - edit the TOML and the hand
  updates without restarting VHI.
- The config path is the `ConfigFilePath` `[Export]` (default
  `user://movements.toml`); a different file can also be loaded at runtime
  from the control panel.

This is the seam for customisation: to add or tweak a *named* movement, edit
the TOML - no rebuild. See
[Add a custom movement](../how-to/add-a-custom-movement.md).

!!! info "Named movements vs. arbitrary poses"
    The TOML route is for movements you can **name ahead of time**. If you
    need to drive the control hand with an *arbitrary, runtime-generated* pose
    (a data glove, another model, generated trajectories), that's the
    [`Stream` mode](control-modes.md) path instead -
    see [Stream a custom pose](../how-to/stream-a-custom-pose.md).
