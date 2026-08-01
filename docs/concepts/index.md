# Concepts

How VHI is put together and why. Read these in order the first time; after
that they stand alone.

- **[Architecture](architecture.md)** - the scene tree, how a 26-bone FBX maps
  to a 16-joint subskeleton, and the threading model that keeps LSL and gRPC
  off Godot's main thread.
- **[The two hands](hands.md)** - the control hand vs. the predicted hand:
  what each one is for and what drives it.
- **[LSL streams](lsl-streams.md)** - the per-DOF inlets VHI consumes, the two
  whole-pose outlets it publishes, and why the two directions differ in shape.
- **[gRPC control plane](grpc-control.md)** - why discrete commands go over
  gRPC while continuous poses stay on LSL, and how the in-process server works.
- **[What drives the control hand](control-hand-drivers.md)** - stream presence
  decides between the pose streams and the movement state machine; what each one
  refuses while the other is live.
- **[Movements](movements.md)** - the predefined movement set, the AI vs.
  Classifier modes, and the TOML config that defines the poses.
