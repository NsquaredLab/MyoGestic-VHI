using System.Threading.Tasks;
using Grpc.Core;
using Myogestic.Vhi.V1;

namespace Vhi;

/// <summary>
/// gRPC service implementation for the VHI control plane. Each RPC marshals its
/// work onto Godot's main thread (via <see cref="GrpcControlServer"/>) because it
/// mutates the scene; the unary return value is the command ack/reject.
/// </summary>
public class VhiControlService : VhiControl.VhiControlBase
{
    private readonly ControlHandSkeleton controlHand;
    private readonly PredictedHandSkeleton predictedHand;
    private readonly GrpcControlServer server;

    public VhiControlService(
        ControlHandSkeleton controlHand,
        PredictedHandSkeleton predictedHand,
        GrpcControlServer server)
    {
        this.controlHand = controlHand;
        this.predictedHand = predictedHand;
        this.server = server;
    }

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetMovement"/>
    public override Task<CommandAck> SetMovement(SetMovementRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            if (RejectIfNotMovementMode("SetMovement", out var reject))
                return reject;
            bool applied = controlHand.SetMovement(request.MovementName, request.Cycle);
            var ack = MakeAck(applied);
            if (!applied)
                ack.Message = $"unknown movement '{request.MovementName}'";
            return ack;
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.Freeze"/>
    public override Task<CommandAck> Freeze(FreezeRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            if (RejectIfNotMovementMode("Freeze", out var reject))
                return reject;
            controlHand.SetFrozen(request.Frozen);
            return MakeAck(true);
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetSpeed"/>
    public override Task<CommandAck> SetSpeed(SetSpeedRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            if (RejectIfNotMovementMode("SetSpeed", out var reject))
                return reject;
            controlHand.SetSpeed(request.FrequencyHz, request.HoldTimeS, request.RestTimeS);
            return MakeAck(true);
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetSmoothing"/>
    public override Task<CommandAck> SetSmoothing(SetSmoothingRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            predictedHand.SetSmoothing(request.Enabled, request.SmoothingSpeed);
            return MakeAck(true);
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetChirality"/>
    public override Task<CommandAck> SetChirality(SetChiralityRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            var ack = MakeAck(false);
            ack.Message = "chirality control is not implemented in VHI";
            return ack;
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetSessionActive"/>
    public override Task<CommandAck> SetSessionActive(SetSessionActiveRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            // Orthogonal to DriverMode — just gates VHI's local keyboard control.
            controlHand.SessionActive = request.Active;
            return MakeAck(true);
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.SetControlMode"/>
    public override Task<CommandAck> SetControlMode(SetControlModeRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            controlHand.SetDriverMode(FromProto(request.Mode));
            return MakeAck(true);
        });

    /// <inheritdoc cref="Myogestic.Vhi.V1.VhiControl.VhiControlBase.GetState"/>
    public override Task<StateReply> GetState(GetStateRequest request, ServerCallContext context)
        => server.InvokeOnMainThread(() =>
        {
            var reply = new StateReply
            {
                CurrentState = controlHand.GetAnimationState(),
                CurrentMovement = controlHand.GetCurrentMovementName(),
                SessionActive = controlHand.SessionActive,
                Mode = controlHand.GetModeName(),
                ControlMode = ToProto(controlHand.DriverMode),
            };
            reply.AvailableMovements.AddRange(controlHand.GetAvailableMovements());
            return reply;
        });

    private CommandAck MakeAck(bool applied) => new()
    {
        Applied = applied,
        CurrentState = controlHand.GetAnimationState(),
        CurrentMovement = controlHand.GetCurrentMovementName(),
        Message = "",
    };

    // Movement / Freeze / SetSpeed only apply in Movement mode. When the control
    // hand is in Stream or Idle mode, reject with a clear message instead of
    // silently no-op'ing. Returns true (and sets `reject`) when the call should
    // be rejected.
    private bool RejectIfNotMovementMode(string command, out CommandAck reject)
    {
        if (controlHand.DriverMode == ControlHandDriverMode.Movement)
        {
            reject = null;
            return false;
        }
        reject = MakeAck(false);
        reject.Message =
            $"{command} ignored — control hand is in {controlHand.DriverMode} mode; " +
            "call SetControlMode(MOVEMENT) first";
        return true;
    }

    private static ControlMode ToProto(ControlHandDriverMode mode) => mode switch
    {
        ControlHandDriverMode.Stream => ControlMode.Stream,
        ControlHandDriverMode.Idle => ControlMode.Idle,
        _ => ControlMode.Movement,
    };

    private static ControlHandDriverMode FromProto(ControlMode mode) => mode switch
    {
        ControlMode.Stream => ControlHandDriverMode.Stream,
        ControlMode.Idle => ControlHandDriverMode.Idle,
        _ => ControlHandDriverMode.Movement,
    };
}
