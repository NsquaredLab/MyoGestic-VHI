# Virtual Hand Interface

The **Virtual Hand Interface (VHI)** is a real-time 3D hand visualiser for
HD-sEMG (high-density surface electromyography) experiments, built with
**Godot 4.6 (.NET)** and **C#** for the [N-squared Lab](https://www.nsquared.tf.fau.de/)
at FAU Erlangen-Nürnberg.

It renders **two hands** side by side:

- a **predicted hand** - driven continuously by your model's output, streamed
  in over [Lab Streaming Layer (LSL)](concepts/lsl-streams.md);
- a **control hand** - a ground-truth reference that plays predefined movements,
  or is itself stream-driven, and is commanded over a [gRPC control
  plane](concepts/grpc-control.md).

Together they let an experimenter *cue* a movement and *see* what a myocontrol
model predicts for it, frame by frame.

![The Virtual Hand Interface - the control hand (left) holding a cued movement, the predicted hand (right) following a model's output streamed over LSL.](images/vhi-overview.png)

## How it fits together

![How VHI fits together with MyoGestic: MyoGestic's Operator GUI sends discrete commands over gRPC to VHI's Control hand; its Myocontrol model sends the MyoGestic_Output LSL stream to the Predicted hand; VHI publishes VHI_Control and VHI_Predict back over LSL.](images/architecture.svg){ style="max-width:780px;width:100%;display:block;margin:1em auto;" }

VHI splits its communication by *what kind of data it is*:

- **Continuous time-series** (hand poses, predictions) flow over **LSL** - so
  they land in the experiment's recording alongside the EMG, on one clock.
- **Discrete commands** ("play this movement", "freeze", "switch mode") flow
  over **gRPC** - typed, acknowledged, with presence signalling.

MyoGestic can also drive the **control hand** continuously over LSL via the
optional `MyoGestic_ControlPose` inlet - useful for streaming custom poses
(data gloves, trajectory generators, …) instead of playing a named movement.
That path activates when the control hand's driver mode is set to `Stream` -
see [Control-hand modes](concepts/control-modes.md).

See [gRPC control plane](concepts/grpc-control.md) for the why behind the
gRPC / LSL split.

## Where to go next

<div class="grid cards" markdown>

- :material-rocket-launch: **[Getting Started](getting-started.md)** -
  install Godot, restore, run, and see a hand move.

- :material-sitemap: **[Concepts](concepts/index.md)** -
  the two hands, the LSL streams, the gRPC control plane, control modes,
  and the movement set.

- :material-tools: **[How-to guides](how-to/index.md)** -
  drive VHI from MyoGestic, stream a custom pose, add a movement, build &
  export.

- :material-book-open-variant: **[Reference](reference/index.md)** -
  the full gRPC API, every LSL stream, and all configuration knobs.

</div>

!!! note "New here?"
    Start with [Getting Started](getting-started.md). If you are integrating
    VHI from MyoGestic, jump straight to
    [Drive VHI from MyoGestic](how-to/drive-from-myogestic.md).
