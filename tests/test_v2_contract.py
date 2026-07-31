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


def _declare(stub, pb2, *names, kind=None, states=None):
    kind = kind if kind is not None else pb2.CONTINUOUS
    dofs = [
        pb2.DofDeclaration(name=n, kind=kind, lo=-1.0, hi=1.0, rest=0.0, states=states or [])
        for n in names
    ]
    return stub.Declare(
        pb2.DeclareRequest(standard_version="1", dofs=dofs, client_name="contract-test"),
        timeout=10.0,
    )


# --- Declare -------------------------------------------------------------------


def test_the_six_standard_dofs_are_renderable(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, *STANDARD_DOFS)
    assert reply.accepted
    assert [v.name for v in reply.verdicts] == list(STANDARD_DOFS)
    for verdict in reply.verdicts:
        assert verdict.renderable, verdict.message
        assert verdict.renders_as, f"{verdict.name} must say what it drives"


def test_declare_reports_the_channel_order_and_stream(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, *STANDARD_DOFS)
    assert list(reply.continuous_channel_order) == list(STANDARD_DOFS)
    assert reply.continuous_stream_name == "MyoGestic_Output"
    assert reply.standard_version == "1"


def test_every_named_channel_is_actually_rendered(v2):
    """The order may only name channels this hand drives.

    This began as "channels 6-8 are dead, so nothing may name a wrist" — naming a dead
    channel being how four wrong pose tables spread. All nine render now, so the claim can
    no longer be about which names are absent; it is that every name present is renderable,
    which is the property that mattered all along.
    """
    stub, pb2 = v2
    reply = _declare(stub, pb2, *STANDARD_DOFS)
    order = list(reply.continuous_channel_order)
    assert len(order) == len(STANDARD_DOFS) == 9
    assert all(v.renderable for v in reply.verdicts), [v.message for v in reply.verdicts]
    # Declaring the order itself must also be accepted: a name in it that this hand could
    # not drive would be exactly the old bug in a new place.
    assert _declare(stub, pb2, *order).accepted


def test_a_dof_this_hand_lacks_is_refused_with_what_it_has(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "wrist.rotation")
    assert not reply.accepted
    verdict = reply.verdicts[0]
    assert not verdict.renderable
    assert "vhi.prediction" in verdict.message, "a refusal must be actionable"


def test_one_unrenderable_dof_fails_the_whole_declaration(v2):
    """All-or-nothing: a client must not half-render and believe it succeeded."""
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.prediction.index", "wrist.rotation")
    assert not reply.accepted
    assert [v.renderable for v in reply.verdicts] == [True, False]


def test_an_empty_declaration_is_not_accepted(v2):
    stub, pb2 = v2
    assert not stub.Declare(pb2.DeclareRequest(standard_version="1"), timeout=5.0).accepted


# --- discrete DOFs -------------------------------------------------------------


def test_a_discrete_dof_renders_as_movements(v2, movements):
    """The point of the discrete work: no v1 SetMovement needed to command a state."""
    stub, pb2 = v2
    states = [m.lower() for m in movements[:3]]
    reply = _declare(stub, pb2, "vhi.control.gesture", kind=pb2.DISCRETE, states=states)
    assert reply.accepted, reply.verdicts[0].message
    assert "control-hand movements" in reply.verdicts[0].renders_as


def test_discrete_states_resolve_case_insensitively(v2, movements):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.control.gesture", kind=pb2.DISCRETE, states=[movements[0].upper()])
    assert reply.accepted, reply.verdicts[0].message


def test_a_discrete_dof_with_an_unknown_state_is_refused(v2, movements):
    """Partly-resolvable is not partly-renderable — it silently does nothing."""
    stub, pb2 = v2
    reply = _declare(
        stub, pb2, "vhi.control.gesture", kind=pb2.DISCRETE, states=[movements[0], "no-such-movement"]
    )
    assert not reply.accepted
    verdict = reply.verdicts[0]
    assert "no-such-movement" in verdict.message
    assert movements[0] in verdict.message, "the refusal must list what is available"


def test_a_discrete_dof_with_no_states_is_refused(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.control.gesture", kind=pb2.DISCRETE, states=[])
    assert not reply.accepted


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
    speaks v1 now gets UNIMPLEMENTED — precisely the signal v2's Declare handshake reads
    to decide it is talking to a build that does not speak its language.
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


def test_presentation_blending_can_be_configured(v2):
    stub, pb2 = v2
    assert stub.SetPresentation(
        pb2.SetPresentationRequest(blend=True, blend_speed=8.0), timeout=10.0
    ).applied
    assert _declare(stub, pb2, "vhi.prediction.index").blends_presentation
    assert stub.SetPresentation(pb2.SetPresentationRequest(blend=False), timeout=10.0).applied
    assert not _declare(stub, pb2, "vhi.prediction.index").blends_presentation


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


@pytest.fixture
def rest_control_hand(v2, aid):
    """Leave the control hand in Movement mode for whatever runs next.

    Declaring a control-pose stream switches the hand to Stream mode, and a later test
    commanding a discrete DOF would then be refused for a reason that has nothing to do
    with what it is testing.
    """
    yield
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetRecordingSessionState(
        pb2.GetRecordingSessionStateRequest(), timeout=10.0
    ).available_movements
    # Commanding a movement is only possible in Movement mode, so the aid's trajectory
    # start/stop is the way back: StopRecordingTrajectory rests via SetMovement.
    control_stub.SetControl(pb2.SetControlRequest(continuous={}), timeout=10.0)
    aid_stub.StartRecordingTrajectory(
        pb2.StartRecordingTrajectoryRequest(movement=movements[0]), timeout=10.0
    )
    aid_stub.StopRecordingTrajectory(pb2.StopRecordingTrajectoryRequest(), timeout=10.0)


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
#: is `PredictedHandSkeleton.boneNames`, read rather than restated.
BONE_NAMES = re.findall(r'"(WaveBone_\d+)"', (SOURCE / "PredictedHandSkeleton.cs").read_text())

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


# --- one vocabulary ---------------------------------------------------------------


def _advertised(v2, stream):
    stub, pb2 = v2
    manifest = stub.GetControlManifest(pb2.GetControlManifestRequest(), timeout=10.0)
    return {c.address for c in manifest.capabilities if c.stream_name == stream}


def test_the_reply_names_only_controls_the_manifest_advertises(v2):
    """`Declare`'s channel order and the manifest must speak one vocabulary.

    They drifted, and nothing here noticed: the aliases were trimmed from the manifest
    while the reply went on naming them, so a client building its frame from
    `continuous_channel_order` was handed five addresses its own loader would refuse.
    The order is derived from the manifest's own table now, and this is what says so.
    """
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.prediction.index")
    advertised = _advertised(v2, "MyoGestic_Output")
    unknown = [n for n in reply.continuous_channel_order if n not in advertised]
    assert not unknown, f"the reply names {unknown}, which the manifest does not advertise"


def test_the_control_pose_order_names_control_pose_controls(v2, rest_control_hand):
    """Not the predicted hand's. Both orders used to come from the prediction table.

    A client that declared a control-pose stream was told its channels were
    `vhi.prediction.*` — addresses on the *other* hand, which it would then have routed
    onto this one.
    """
    stub, pb2 = v2
    reply = stub.Declare(
        pb2.DeclareRequest(
            standard_version="1",
            client_name="control-pose-test",
            control_pose=True,
            dofs=[
                pb2.DofDeclaration(
                    name="vhi.prediction.index", kind=pb2.CONTINUOUS, lo=-1.0, hi=1.0, states=[]
                )
            ],
        ),
        timeout=10.0,
    )
    assert reply.accepted, reply.message
    order = list(reply.control_pose_channel_order)
    assert order, "declaring the stream must report its channel order"
    assert all(name.startswith("vhi.control.pose.") for name in order), order
    advertised = _advertised(v2, "MyoGestic_ControlPose")
    assert not [n for n in order if n not in advertised], order


def test_an_alias_is_still_accepted_though_unadvertised(v2):
    """The rename is not a removal: a map written against the old name keeps rendering.

    `resolve()` on the client side refuses what the manifest omits, so this is what a
    client reaches only by declaring the alias directly — but the renderer must not be
    the thing that breaks it.
    """
    stub, pb2 = v2
    assert "vhi.prediction.thumb" not in _advertised(v2, "MyoGestic_Output")
    assert _declare(stub, pb2, "vhi.prediction.thumb").accepted


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
