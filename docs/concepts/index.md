# Concepts

How VHI is put together and why. Read these in order the first time; after
that they stand alone.

- **[Architecture](architecture.md)** - the scene tree, how a 26-bone FBX maps
  to a 16-joint subskeleton, and the threading model that keeps LSL and gRPC
  off Godot's main thread.
- **[The two hands](hands.md)** - the control hand vs. the predicted hand:
  what each one is for and what drives it.
- **[LSL streams](lsl-streams.md)** - the two inlets VHI consumes and the two
  outlets it publishes, with the 9-DOF channel layout.
- **[gRPC control plane](grpc-control.md)** - why discrete commands go over
  gRPC while continuous poses stay on LSL, and how the in-process server works.
- **[Control-hand modes](control-modes.md)** - `Movement`, `Stream`, and
  `Idle`; how the control hand arbitrates between its possible drivers.
- **[Movements](movements.md)** - the predefined movement set, the AI vs.
  Classifier modes, and the TOML config that defines the poses.
