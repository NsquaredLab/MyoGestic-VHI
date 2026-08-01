"""Machine-check what the v2 standard service actually does to the rig.

Until this file existed, the only assertion about VHI's rig behaviour lived in
MyoGestic's test suite as a mapping *read out of this source* — which cannot catch a
change made here. These tests drive a live VHI and check what it reports back.

The interesting ones are the sweeps. ``SweepControl`` drives one named DOF across its
range and reports which bones moved and by how many signed degrees, read back off the
skeleton. That turns three claims into assertions:

- **Identity.** ``index`` moves the index bones and nothing else.
- **Direction.** Standard ``+1`` produces the sign flexion produces.
- **Symmetry.** ``-1`` produces exactly the negative, which is what "the extension
  half renders" means — the property the signed ``[-1, 1]`` domain rests on, and one
  that no recording could establish because no operator ever extended.

What they cannot check is that ``WaveBone_7`` is the bone a human calls the index
finger. That is the FBX's business; pinning the *name* means a re-rig shows up here
as a failure rather than as a hand that moves the wrong finger.
"""

from __future__ import annotations

import math
import pathlib
import re
import time

import pytest

#: Standard name -> the bones it must move, and the signed degrees at standard +1.
#:
#: **Literal calibration, not derived from anything the renderer also reads.** Every earlier
#: version of this table was computed from `MovementPoses` — and so was the renderer, so the
#: two agreed with each other and both disagreed with the hand. Twice: once when the suite
#: took its numbers from a live sweep, and once when it took them from the pose library
#: without accounting for `ApplyMovementPose` negating every row on the way to the bone. A
#: held `Movements.Fist` puts bone 4 at `+85°`; the table said `-85`.
#:
#: Positive X is flexion on this rig. The independent anchor for that claim is not here — it
#: is `test_a_named_flexion_publishes_standard_plus_one`, which holds the movement whose
#: *name* says what it is and reads the ground-truth stream. A human named it; no renderer
#: computed it.
#:
#: Thumb abduction has only two entries on purpose: it drives all three thumb bones
#: through channel 1, but the distal bone's Z gain is 0, so it cannot move. The wrist has
#: one, because bone 0 is a single joint that every digit hangs off.
EXPECTED = {
    "vhi.prediction.thumb.flexion": {
        "WaveBone_3": 45.0,
        "WaveBone_4": 55.0,
        "WaveBone_5": 80.0,
    },
    # Abduction is away from the palm — the opposite of the fist's thumb, which comes
    # across the fingers. So these are the negatives of `Fist`'s thumb Z.
    "vhi.prediction.thumb.abduction": {"WaveBone_3": 30.0, "WaveBone_4": -35.0},
    "vhi.prediction.index": {
        "WaveBone_7": 85.0,
        "WaveBone_8": 75.0,
        "WaveBone_9": 60.0,
    },
    "vhi.prediction.middle": {
        "WaveBone_12": 85.0,
        "WaveBone_13": 85.0,
        "WaveBone_14": 60.0,
    },
    "vhi.prediction.ring": {
        "WaveBone_17": 85.0,
        "WaveBone_18": 85.0,
        "WaveBone_19": 60.0,
    },
    "vhi.prediction.little": {
        "WaveBone_22": 85.0,
        "WaveBone_23": 85.0,
        "WaveBone_24": 60.0,
    },
    # The wrist: one bone, three axes. Bone 0 parents every digit chain, so this is the
    # whole hand turning. All three are calibrations of `StandardPose.Wrist` — X's
    # magnitude comes from `Movements.WristUpDown`, but no bone's local basis can be
    # deduced from another's, so none of the three signs is derived.
    "vhi.prediction.wrist.flexion": {"WaveBone_1": 30.0},
    "vhi.prediction.wrist.abduction": {"WaveBone_1": 20.0},
    # Rotation pins a *choice*: nothing in the movement library touches joint 0's Y axis,
    # so both the magnitude and the sign were picked. See `StandardPose.Wrist`.
    "vhi.prediction.wrist.rotation": {"WaveBone_1": 179.0},
}

STANDARD_DOFS = tuple(EXPECTED)


# --- discrete DOFs -------------------------------------------------------------


def test_setcontrol_applies_a_discrete_state(v2, movements):
    stub, pb2 = v2
    ack = stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": movements[0]}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


def test_setcontrol_rejects_an_unresolvable_state(v2):
    stub, pb2 = v2
    ack = stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": "no-such-movement"}), timeout=10.0
    )
    assert not ack.applied
    assert "vhi.control.gesture" in ack.rejected


# --- SetControl, continuous ----------------------------------------------------


def test_setcontrol_applies_continuous_values(v2):
    stub, pb2 = v2
    ack = stub.SetControl(
        pb2.SetControlRequest(continuous={n: 0.0 for n in STANDARD_DOFS}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


def test_setcontrol_rejects_an_unknown_name(v2):
    stub, pb2 = v2
    ack = stub.SetControl(pb2.SetControlRequest(continuous={"wrist.rotation": 1.0}), timeout=10.0)
    assert not ack.applied
    assert "wrist.rotation" in ack.rejected


@pytest.mark.parametrize("bad", [float("nan"), float("inf"), float("-inf")])
def test_setcontrol_rejects_a_non_finite_value(v2, bad):
    """A non-finite value becomes a full-scale deflection once multiplied by a gain."""
    stub, pb2 = v2
    ack = stub.SetControl(pb2.SetControlRequest(continuous={"vhi.prediction.index.flexion": bad}), timeout=10.0)
    assert not ack.applied
    assert "vhi.prediction.index.flexion" in ack.rejected


# --- SweepControl: the rig itself ----------------------------------------------


@pytest.mark.parametrize("name", STANDARD_DOFS)
def test_a_sweep_moves_exactly_the_bones_the_name_denotes(v2, name):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    moved = {o.element for o in reply.observed}
    assert moved == set(EXPECTED[name]), f"{name} moved {sorted(moved)}"
    assert reply.matched_expectation


@pytest.mark.parametrize("name", STANDARD_DOFS)
def test_a_sweep_turns_each_bone_the_documented_amount(v2, name):
    """Locks the per-bone gains: a changed gain silently rescales the whole DOF."""
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    assert reply.observed, f"{name} moved nothing — an empty sweep must not pass"
    for observation in reply.observed:
        expected = EXPECTED[name][observation.element]
        assert observation.degrees_at_hi == pytest.approx(expected, abs=0.5), observation.element


@pytest.mark.parametrize("name", STANDARD_DOFS)
def test_the_extension_half_is_the_exact_mirror(v2, name):
    """No clamping anywhere: this is what makes the standard domain signed.

    Never observable from a recording — the reference sessions only ever contain
    flexion, because no operator extended.
    """
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    assert reply.observed, f"{name} moved nothing — an empty sweep must not pass"
    for o in reply.observed:
        assert o.degrees_at_lo == pytest.approx(-o.degrees_at_hi, abs=0.5), o.element
        assert not math.isclose(o.degrees_at_hi, 0.0, abs_tol=0.5)


def test_a_one_directional_sweep_leaves_the_other_half_alone(v2):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name="vhi.prediction.index.flexion", duration_s=1.0, both_directions=False),
        timeout=25.0,
    )
    assert reply.completed, reply.message
    assert reply.observed
    for o in reply.observed:
        assert o.degrees_at_hi != 0.0
        assert o.degrees_at_lo == 0.0


def test_a_sweep_of_an_unrenderable_dof_is_refused(v2):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name="wrist.rotation", duration_s=1.0), timeout=15.0
    )
    assert not reply.completed
    assert "wrist.rotation" in reply.message


def test_the_hand_is_left_at_rest_after_a_sweep(v2):
    """A verification tool must not leave a limb deflected."""
    stub, pb2 = v2
    stub.SweepControl(
        pb2.SweepControlRequest(name="vhi.prediction.index.flexion", duration_s=1.0, both_directions=True),
        timeout=25.0,
    )
    after = stub.SweepControl(
        pb2.SweepControlRequest(name="vhi.prediction.thumb.flexion", duration_s=1.0, both_directions=False),
        timeout=25.0,
    )
    # thumb.flexion's sweep only reports thumb bones; had the index sweep left the
    # hand flexed, index bones would show up in this unrelated sweep's scan.
    assert {o.element for o in after.observed} <= set(EXPECTED["vhi.prediction.thumb.flexion"])


# --- v1 must be gone ----------------------------------------------------------


def test_the_legacy_v1_service_is_no_longer_served(vhi_process):
    """The removal, asserted rather than assumed.

    Called over a raw channel with no generated stub, which is the point: the v1
    contract file is deleted, so there is nothing to generate from. A client that still
    speaks v1 now gets UNIMPLEMENTED — precisely the signal a v2 client reads to notice
    it is talking to a build that does not speak its language.
    """
    import grpc

    with grpc.insecure_channel("127.0.0.1:50051") as channel:
        call = channel.unary_unary(
            "/myogestic.vhi.v1.VhiControl/GetState",
            request_serializer=lambda _: b"",
            response_deserializer=lambda raw: raw,
        )
        with pytest.raises(grpc.RpcError) as excinfo:
            call(None, timeout=10.0)
    assert excinfo.value.code() == grpc.StatusCode.UNIMPLEMENTED, excinfo.value.code()


# --- the recording aid: same service, deliberately not control ------------------


@pytest.fixture
def aid(v2_pb2, vhi_process):
    """A second VhiControl stub on the same live VHI, on the same port.

    Recording RPCs live on the one service now, so this is not a different stub type
    from ``v2`` — just an independent channel, so tests can exercise a second client
    without the two sharing state that only a real second connection would expose.
    """
    import grpc

    pb2, pb2_grpc = v2_pb2
    channel = grpc.insecure_channel("127.0.0.1:50051")
    stub = pb2_grpc.VhiControlStub(channel)
    yield stub, pb2
    # Never leave a trajectory running for the next test.
    stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0)
    stub.SetRecordingSession(pb2.SetRecordingSessionRequest(active=False), timeout=10.0)
    channel.close()


def test_recording_session_state_is_served_by_the_same_stub(aid):
    """One service now: recording RPCs are reached through VhiControl, not a stub of their own."""
    stub, pb2 = aid
    state = stub.GetRecordingSessionState(pb2.GetRecordingSessionStateRequest(), timeout=10.0)
    assert list(state.available_movements), "the aid discovers movements on its own"


def test_the_recording_session_gate_round_trips(aid):
    stub, pb2 = aid
    assert stub.SetRecordingSession(
        pb2.SetRecordingSessionRequest(active=True), timeout=10.0
    ).applied
    assert stub.GetRecordingSessionState(pb2.GetRecordingSessionStateRequest(), timeout=10.0).recording_session_active
    assert stub.SetRecordingSession(
        pb2.SetRecordingSessionRequest(active=False), timeout=10.0
    ).applied
    assert not stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).recording_session_active


def test_a_recording_trajectory_runs_and_reports_itself(aid):
    stub, pb2 = aid
    movement = stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements[1]
    ack = stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movement, frequency_hz=1.0), timeout=10.0
    )
    assert ack.applied, ack.message
    state = stub.GetRecordingSessionState(pb2.GetRecordingSessionStateRequest(), timeout=10.0)
    assert state.trajectory_running
    assert state.trajectory_movement == movement


def test_a_second_trajectory_is_refused_rather_than_swapped(aid):
    """A recording is aligned against the running trajectory; swapping corrupts it."""
    stub, pb2 = aid
    movements = stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements
    assert stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[1]), timeout=10.0
    ).applied
    second = stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[2]), timeout=10.0
    )
    assert not second.applied
    assert "already running" in second.message


def test_an_unknown_movement_is_refused_with_what_is_available(aid):
    stub, pb2 = aid
    ack = stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement="not-a-movement"), timeout=10.0
    )
    assert not ack.applied
    assert "offers" in ack.message


def test_stopping_a_trajectory_is_idempotent(aid):
    """Teardown calls this without knowing whether anything is running."""
    stub, pb2 = aid
    assert stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0).applied
    assert stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0).applied
    assert not stub.GetRecordingSessionState(pb2.GetRecordingSessionStateRequest(), timeout=10.0).trajectory_running


def test_a_running_trajectory_owns_the_control_hand(aid, v2):
    """The aid must not be silently overridden mid-recording — nor silently override.

    This is the guard that keeps a recording aid from changing what a discrete DOF
    means: it does not redefine "hold this state", it refuses to be interrupted, and
    says so.
    """
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements
    assert aid_stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[1]), timeout=10.0
    ).applied

    ack = control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": movements[2]}), timeout=10.0
    )
    assert not ack.applied
    assert "recording trajectory is running" in ack.rejected["vhi.control.gesture"]


def test_discrete_control_works_again_once_the_trajectory_stops(aid, v2):
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements
    aid_stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[1]), timeout=10.0
    )
    aid_stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0)
    ack = control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": movements[2]}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


def test_continuous_control_is_unaffected_by_a_running_trajectory(aid, v2):
    """The trajectory drives the *control* hand; continuous DOFs drive the predicted one."""
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements
    aid_stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[1]), timeout=10.0
    )
    ack = control_stub.SetControl(
        pb2.SetControlRequest(continuous={"vhi.prediction.index.flexion": 0.5}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


# --- layer 3: presentation blending, and what it must NOT be ---------------------


def test_blending_does_not_change_the_commanded_value(v2):
    """Layer 3 is cosmetic. If it altered the value it would be layer 1 in disguise.

    A sweep reads the rig back after each excursion, so if blending changed *what* was
    commanded rather than only how it is approached, the reported degrees would differ
    between blend on and blend off.
    """
    stub, pb2 = v2
    readings = {}
    for blend in (False, True):
        stub.SetPresentation(
            pb2.SetPresentationRequest(blend=blend, blend_speed=25.0), timeout=10.0
        )
        reply = stub.SweepControl(
            pb2.SweepControlRequest(name="vhi.prediction.index.flexion", duration_s=1.5, both_directions=True),
            timeout=25.0,
        )
        assert reply.completed, reply.message
        readings[blend] = {o.element: o.degrees_at_hi for o in reply.observed}
    stub.SetPresentation(pb2.SetPresentationRequest(blend=False), timeout=10.0)
    assert readings[False], "a blend comparison over no observations proves nothing"
    assert readings[False].keys() == readings[True].keys()
    for element, degrees in readings[False].items():
        assert readings[True][element] == pytest.approx(degrees, abs=0.5), element


def test_the_recording_state_reports_the_current_movement(v2, aid, movements):
    """The palette highlights it, and must not need a second stub to."""
    aid_stub, pb2 = aid
    control_stub, _ = v2
    target = movements[1]
    assert control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": target}), timeout=10.0
    ).applied
    state = aid_stub.GetRecordingSessionState(pb2.GetRecordingSessionStateRequest(), timeout=10.0)
    assert state.current_movement == target


# --- the control-pose stream: negotiated, not flipped ----------------------------


def test_the_control_hand_follows_the_control_pose_stream(v2, control_inlet):
    """Publish MyoGestic_ControlPose and the control hand renders it. No handshake.

    The predicted hand has always worked this way — publish MyoGestic_Output and it
    moves. The control hand required a Declare(control_pose=true) whose only effect was
    a mode flip, so the same idea needed a ceremony on one stream and not the other.

    Covers both edges: while the outlet is delivering, the hand renders it; once the
    outlet is gone and `ControlPoseStaleAfterSeconds` has elapsed, the hand must give
    itself back to its own movements rather than hold the last streamed pose forever —
    otherwise a producer that dies mid-recording leaves `VHI_Control` emitting a
    plausible held gesture indistinguishable from an operator deliberately holding it.
    """
    pylsl = pytest.importorskip("pylsl")
    info = pylsl.StreamInfo("MyoGestic_ControlPose", "Control", 9, 60, "float32", "presence")
    outlet = pylsl.StreamOutlet(info)
    frame = [0.0] * 9
    frame[2] = 1.0  # index flexion
    sample = None
    try:
        deadline = time.time() + 25.0
        while time.time() < deadline:
            outlet.push_sample(frame)
            control_inlet.flush()
            time.sleep(0.5)
            sample, _ = control_inlet.pull_sample(timeout=2.0)
            if sample and sample[2] > 0.9:
                break
        assert sample, "VHI_Control never delivered a sample"
    finally:
        del outlet
    assert sample[2] == pytest.approx(1.0, abs=0.05), f"index not driven: {sample}"

    # Falling edge. The outlet above is gone, so ControlPoseLive drops once
    # ControlPoseStaleAfterSeconds of silence has passed — read from the C# rather than
    # restated, so a retuned timeout cannot make this wait too short to observe it.
    stale_after_s = float(
        re.search(
            r"ControlPoseStaleAfterSeconds = ([\d.]+)f",
            (SOURCE / "LSLCommunicationController.cs").read_text(),
        ).group(1)
    )
    time.sleep(stale_after_s + 2.0)
    control_inlet.flush()
    sample, _ = control_inlet.pull_sample(timeout=5.0)
    assert sample is not None, "VHI_Control stopped publishing once the stream went stale"
    assert sample[2] == pytest.approx(0.0, abs=0.05), (
        f"index still held {stale_after_s + 2.0:.0f}s after the stream went stale: {sample} "
        "— the control hand did not give itself back to the movement state machine"
    )


# --- Direction: standard +1 renders what the DOF name denotes ------------------
#
# This section has been wrong twice, in opposite directions, and both times it passed.
#
#   1. The expectation was filled in from a live sweep of a renderer that negated every
#      channel on ingest. The suite agreed with the rig; both disagreed with the names.
#   2. The expectation was then derived from `MovementPoses` instead — the rig's own
#      library of named postures, which looks like ground truth and is not. Those rows
#      reach the bone through `ApplyMovementPose`, which negated them, so a held
#      `Movements.Fist` put bone 4 at `+85°` while the table read `-85`. The renderer read
#      the table the same wrong way, so again the two agreed and the hand was backwards.
#
# Neither failure is detectable from inside one renderer. `VHI_Predict`'s read-back cannot
# help either — it is the algebraic inverse of the conversion that rendered the pose, so it
# round-trips whichever way the pair points.
#
# The anchor below is therefore the **control hand**: VHI's own ground-truth renderer,
# holding the movement whose *name* says what it is. `Movements.Index` is index flexion
# because it is called that and an operator watched it. Driving the predicted hand at
# standard +1 and requiring the same bones to land in the same place compares two renderers
# and consults no table, so a sign error has to be made identically in both to survive.

SOURCE = pathlib.Path(__file__).resolve().parent.parent / "src"

#: A sweep reports `element` as a bone *name*; the pose library is indexed by joint. This
#: is `HandSkeleton.BoneNames`, read rather than restated — both hands share it.
BONE_NAMES = re.findall(r'"(WaveBone_\d+)"', (SOURCE / "HandSkeleton.cs").read_text())

#: The flexion DOFs: every advertised control except the thumb's second axis. Not
#: `endswith(".flexion")` — a single-axis digit carries no suffix, because `index` cannot
#: mean anything else. Only the thumb, with two axes, names them.
#: The wrist is excluded: no named movement in the library flexes it alongside the digits,
#: so it has no ground-truth counterpart here. Its calibration is asserted from `EXPECTED`.
FLEXION_DOFS = tuple(
    name
    for name in STANDARD_DOFS
    if not name.endswith(".abduction") and ".wrist" not in name
)

#: Movement the control hand can hold -> the VHI_Control channel it must drive to +1, and
#: the channels it must leave alone. Both names are the rig's own; nothing is computed.
#: `Movements.Index` is index flexion because it is called that and an operator watched it,
#: which is the only claim in this file that no renderer also makes.
NAMED_FLEXIONS = {
    "Thumb": 0,
    "Index": 2,
    "Middle": 3,
    "Ring": 4,
    "Pinky": 5,
}

#: `ControlHandSkeleton`'s outlet, in channel order.
CONTROL_CHANNELS = (
    "ThumbFlexion", "ThumbAbduction", "IndexFlexion", "MiddleFlexion", "RingFlexion",
    "PinkyFlexion", "WristFlexion", "WristAbduction", "WristRotation",
)


def _sweep(v2, name):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    assert reply.observed, f"{name} moved nothing — an empty sweep proves nothing"
    return {BONE_NAMES.index(o.element): o for o in reply.observed}


def test_the_bone_names_were_actually_parsed():
    """Guards the regex: a rename upstream must fail loudly, not vacuously."""
    assert len(BONE_NAMES) == 16, BONE_NAMES


@pytest.mark.parametrize("name", FLEXION_DOFS)
def test_standard_plus_one_flexes_the_digit(v2, name):
    """Standard +1 closes the hand; it does not open it.

    Bends toward the palm are positive on this rig. Stated as a literal here rather than
    read from `MovementPoses`, whose rows are the opposite of what they render.
    """
    for joint, observed in _sweep(v2, name).items():
        assert observed.degrees_at_hi > 0.0, (
            f"{name} joint {joint} ({observed.element}): standard +1 rendered "
            f"{observed.degrees_at_hi:+.1f}°, which bends away from the palm"
        )


def test_standard_plus_one_abducts_away_from_the_fist(v2):
    """The thumb's second axis, and why it opposes the fist.

    A fist wraps the thumb *across* the palm — adduction. The DOF is named abduction, so
    standard +1 must render the other way. This is the channel that a blanket negation
    gets right by accident while inverting all five flexion DOFs, which is how two
    successive direction bugs both survived review.
    """
    observed = _sweep(v2, "vhi.prediction.thumb.abduction")
    by_bone = {o.element: o.degrees_at_hi for o in observed.values()}
    assert by_bone == pytest.approx(EXPECTED["vhi.prediction.thumb.abduction"], abs=0.5)
    # And the sign is genuinely opposite the closed hand's, not merely a matching number.
    fist = {"WaveBone_3": -30.0, "WaveBone_4": 35.0}
    for bone, degrees in by_bone.items():
        assert degrees * fist[bone] < 0.0, (
            f"thumb abduction {bone}: standard +1 rendered {degrees:+.1f}°, the same side "
            f"as the fist's {fist[bone]:+.1f}° — that is adduction"
        )


@pytest.fixture(scope="module")
def control_inlet(vhi_process):
    """An open inlet on VHI_Control, or a skip if pylsl/the outlet is unavailable."""
    pylsl = pytest.importorskip("pylsl", reason="the direction anchor reads VHI_Control")
    streams = [s for s in pylsl.resolve_streams(wait_time=8.0) if s.name() == "VHI_Control"]
    if not streams:
        pytest.skip("VHI_Control did not resolve")
    inlet = pylsl.StreamInlet(streams[0])
    try:
        yield inlet
    finally:
        inlet.close_stream()


def _hold(stub, pb2, inlet, movement):
    """Hold `movement` on the control hand and return its settled VHI_Control frame."""
    stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(
            movement=movement, frequency_hz=0.5, hold_time_s=25.0, rest_time_s=0.0
        ),
        timeout=10.0,
    )
    try:
        time.sleep(5.0)  # ramp in, then hold
        inlet.flush()
        sample, _ = inlet.pull_sample(timeout=5.0)
        assert sample is not None, f"no VHI_Control sample while holding {movement}"
        return sample
    finally:
        stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0)


@pytest.mark.parametrize("movement", sorted(NAMED_FLEXIONS))
def test_a_named_flexion_publishes_standard_plus_one(v2, control_inlet, movement):
    """The non-circular anchor. `Movements.Index` is flexion because it is *called* that.

    Every other direction check in this file consults something the renderer also consults
    — the pose table, or an inverse of the renderer's own conversion — and therefore agrees
    with it whichever way it points. This one starts from a human-assigned name and asks
    what the ground-truth stream says while the hand is in it. A renderer bending the wrong
    way publishes `-1` here.

    It is also what a user actually does: train on `VHI_Control`, drive
    `vhi.prediction.*`. If these two disagree by a sign, every model needs its weights
    flipped by hand — which is the bug this pins.
    """
    stub, pb2 = v2
    channel = NAMED_FLEXIONS[movement]
    sample = _hold(stub, pb2, control_inlet, movement)

    assert sample[channel] == pytest.approx(1.0, abs=0.05), (
        f"holding {movement}, {CONTROL_CHANNELS[channel]} published "
        f"{sample[channel]:+.2f} — a named flexion is standard +1, so this hand bends "
        f"the opposite way from the DOF that shares its name"
    )
    others = {
        CONTROL_CHANNELS[i]: v
        for i, v in enumerate(sample)
        if i != channel and abs(v) > 0.05
    }
    assert not others, f"{movement} also moved {others}"


def test_the_named_fist_is_the_standard_fist(v2, control_inlet):
    """A fist is five flexions and an *ad*ducted thumb — `[1, -1, 1, 1, 1, 1]`.

    The thumb is the digit that catches a whole-hand sign error. Bend every joint the wrong
    way and four fingers still curl into something fist-shaped; the thumb is the only one
    whose flexion is not symmetric front-to-back, so it is where a backwards hand stops
    looking plausible.
    """
    stub, pb2 = v2
    sample = _hold(stub, pb2, control_inlet, "Fist")
    assert list(sample[:6]) == pytest.approx([1.0, -1.0, 1.0, 1.0, 1.0, 1.0], abs=0.05), (
        f"a held Fist published {[round(v, 2) for v in sample[:6]]}"
    )


def test_direction_is_the_same_on_every_repeat(v2):
    """Three runs, one answer — the property a sign that depends on state would break."""
    runs = [
        {joint: round(o.degrees_at_hi, 3) for joint, o in _sweep(v2, "vhi.prediction.index").items()}
        for _ in range(3)
    ]
    assert runs[0] == runs[1] == runs[2], runs


# --- the wrist ------------------------------------------------------------------


#: Joint 0's extremes per named wrist movement, from the library. `[0]` is the movement's
#: max state and `[1]` its min — the wrist is the only entry that defines both, because
#: "up/down" and "left/right" are two-sided where a fist is not.
WRIST = {
    movement: (
        tuple(float(v) for v in (x0, y0, z0)),
        tuple(float(v) for v in (x1, y1, z1)),
    )
    for movement, x0, y0, z0, x1, y1, z1 in re.findall(
        r"poses\[Movements\.(Wrist\w+)\]\[0\]\[0\] = \[\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\s*\];"
        r"\s*\n\s*poses\[Movements\.\1\]\[0\]\[1\] = \[\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\s*\];",
        (SOURCE / "MovementDefinitions.cs").read_text(),
    )
}


def test_the_wrist_movements_were_actually_parsed():
    """Guards the regex, so the two tests below cannot pass by asserting nothing."""
    assert set(WRIST) == {"WristUpDown", "WristLeftRight"}, WRIST
    assert WRIST["WristUpDown"][0][0] == -30.0
    assert WRIST["WristLeftRight"][0][2] == -20.0


def test_standard_plus_one_flexes_the_wrist_by_the_documented_amount(v2):
    """The wrist's X anchor: the library's extreme, with flexion's sign.

    `Movements.WristUpDown` gives the magnitude (30°) but names neither side, and no other
    bone's local basis settles joint 0's — the previous version of this test deduced the
    sign from the fingers, which was not evidence. It is a calibration: standard +1 flexes
    at the positive extreme, matching the digits.
    """
    magnitude = abs(WRIST["WristUpDown"][0][0])
    observed = _sweep(v2, "vhi.prediction.wrist.flexion")
    assert set(observed) == {0}, f"the wrist moved joints {sorted(observed)}"
    assert observed[0].degrees_at_hi == pytest.approx(magnitude, abs=0.5)


def test_wrist_abduction_uses_the_librarys_magnitude(v2):
    """The Z axis: magnitude derived, sign chosen.

    `Movements.WristLeftRight` defines ±20° and calls neither side abduction, so only the
    magnitude is evidence here. The sign is `StandardPose.Wrist`'s documented choice —
    taken by analogy with flexion — and this pins it so that flipping it is a deliberate
    edit with a failing test attached, rather than a silent change of direction.
    """
    magnitude = abs(WRIST["WristLeftRight"][0][2])
    observed = _sweep(v2, "vhi.prediction.wrist.abduction")
    assert set(observed) == {0}
    assert observed[0].degrees_at_hi == pytest.approx(magnitude, abs=0.5)


def test_wrist_rotation_pins_a_choice_not_a_derivation(v2):
    """Rotation is the one wrist axis with no evidence behind it.

    `Movements.WristUpDown` and `WristLeftRight` define joint 0's X and Z; nothing in the
    library touches its Y. So the range and the sign were both picked — 90 degrees for the
    human range, negative so standard +1 is pronation — and this test exists to make
    changing either a deliberate edit with a failing assertion attached, rather than a
    silent change to what +1 means.

    It also checks the axis is isolated: rotation must turn bone 0 and nothing else, and
    must not leak into the flexion or abduction read-back, which share that bone.
    """
    observed = _sweep(v2, "vhi.prediction.wrist.rotation")
    assert set(observed) == {0}, f"rotation moved joints {sorted(observed)}"
    assert observed[0].degrees_at_hi == pytest.approx(179.0, abs=0.5)
    assert observed[0].degrees_at_lo == pytest.approx(-179.0, abs=0.5)
    # 179, not 180, and the one degree is load-bearing: `GetEuler` returns angles in
    # (-180, +180], so -180 and +180 are the same orientation and the decode picks the
    # positive one. At exactly 180 the pose is right and the *read-back inverts* — a
    # commanded +1 reports as -1. Measured, not assumed.
    assert abs(observed[0].degrees_at_hi) < 180.0


# --- a channel is an address ------------------------------------------------------


def test_a_pose_frame_lands_on_the_channel_the_manifest_names(v2):
    """Write channel 2 and the index moves. No labels, no routing, no negotiation.

    `vhi.prediction.index` *is* channel 2 — the manifest says so and both ends read it
    from that one table. This used to be negotiable: a client could compact its frame and
    label the channels, and the renderer worked out the mapping by asking the stream for
    its labels. Asking meant liblsl's `info()`, the only thing that starts an
    `info_receiver` thread, and cancelling one mid-request is what crashed this renderer
    three times. The compaction saved three floats a frame.
    """
    pylsl = pytest.importorskip("pylsl")
    # No declaration: the manifest already says vhi.prediction.index is channel 2 and
    # vhi.prediction.middle is channel 3, and both ends read that from one table — that
    # is the whole claim this test makes.

    info = pylsl.StreamInfo("MyoGestic_Output", "Control", 9, 60, "float32", "pose-frame")
    outlet = pylsl.StreamOutlet(info)
    frame = [0.0] * 9
    frame[2] = 1.0   # index
    frame[3] = 1.0   # middle
    predict, sample = None, None
    try:
        deadline = time.time() + 25.0
        while time.time() < deadline:
            outlet.push_sample(frame)
            if predict is None:
                found = [s for s in pylsl.resolve_streams(wait_time=1.0)
                         if s.name() == "VHI_Predict"]
                if found:
                    predict = pylsl.StreamInlet(found[0])
                continue
            predict.flush()
            time.sleep(0.5)
            sample, _ = predict.pull_sample(timeout=2.0)
            if sample and sample[2] > 0.9:
                break
            time.sleep(0.2)
        assert sample, "VHI_Predict never delivered a sample"
    finally:
        if predict is not None:
            predict.close_stream()
        del outlet

    assert sample[2] == pytest.approx(1.0, abs=0.05), f"index not driven: {sample}"
    assert sample[3] == pytest.approx(1.0, abs=0.05), f"middle not driven: {sample}"
    assert abs(sample[0]) < 0.05 and abs(sample[1]) < 0.05, f"thumb moved: {sample}"
