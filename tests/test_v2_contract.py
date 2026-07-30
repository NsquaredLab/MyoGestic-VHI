"""Machine-check what the v2 canonical service actually does to the rig.

Until this file existed, the only assertion about VHI's rig behaviour lived in
MyoGestic's test suite as a mapping *read out of this source* — which cannot catch a
change made here. These tests drive a live VHI and check what it reports back.

The interesting ones are the sweeps. ``SweepControl`` drives one named DOF across its
range and reports which bones moved and by how many signed degrees, read back off the
skeleton. That turns three claims into assertions:

- **Identity.** ``index`` moves the index bones and nothing else.
- **Direction.** Canonical ``+1`` produces the sign flexion produces.
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

import pytest

#: Canonical name -> the bones it must move, and the signed degrees at canonical +1.
#:
#: Read off `PredictedHandSkeleton.jointMovements`, which *is* `MovementPoses[Fist]` — the
#: max-flexion pose. So canonical +1 on a flexion DOF renders the gain itself: negative
#: degrees, the same sign `Fist` uses and the opposite of `IndexExtension`'s `+20`.
#:
#: Abduction is the one channel whose sign is inverted, because the fist's thumb Z is
#: *adduction*. See `Vhi.CanonicalPose`, which both hands share.
#:
#: These were once the negatives of this, taken from a live sweep of a renderer that
#: negated every channel on ingest — so the suite agreed with the rig and both disagreed
#: with the DOF names. Derive this table from the movement library, never from a sweep.
#:
#: Thumb abduction has only two entries on purpose: it drives all three thumb bones
#: through channel 1, but the distal bone's Z gain is 0, so it cannot move. The wrist has
#: one, because bone 0 is a single joint that every digit hangs off.
EXPECTED = {
    "vhi.prediction.thumb.flexion": {
        "WaveBone_3": -45.0,
        "WaveBone_4": -55.0,
        "WaveBone_5": -80.0,
    },
    "vhi.prediction.thumb.abduction": {"WaveBone_3": -30.0, "WaveBone_4": 35.0},
    "vhi.prediction.index": {
        "WaveBone_7": -85.0,
        "WaveBone_8": -75.0,
        "WaveBone_9": -60.0,
    },
    "vhi.prediction.middle": {
        "WaveBone_12": -85.0,
        "WaveBone_13": -85.0,
        "WaveBone_14": -60.0,
    },
    "vhi.prediction.ring": {
        "WaveBone_17": -85.0,
        "WaveBone_18": -85.0,
        "WaveBone_19": -60.0,
    },
    "vhi.prediction.little": {
        "WaveBone_22": -85.0,
        "WaveBone_23": -85.0,
        "WaveBone_24": -60.0,
    },
    # The wrist: one bone, two axes. Bone 0 parents every digit chain, so this is the
    # whole hand turning. Read off `CanonicalPose.Wrist`, whose X comes from
    # `Movements.WristUpDown` and whose Z sign is a documented choice, not a derivation.
    "vhi.prediction.wrist.flexion": {"WaveBone_1": -30.0},
    "vhi.prediction.wrist.abduction": {"WaveBone_1": -20.0},
    # Rotation pins a *choice*, not a derivation: nothing in the movement library touches
    # joint 0's Y axis, so 90 degrees and its sign were picked. See `CanonicalPose.Wrist`.
    "vhi.prediction.wrist.rotation": {"WaveBone_1": -179.0},
}

CANONICAL_DOFS = tuple(EXPECTED)


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


def test_the_six_canonical_dofs_are_renderable(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, *CANONICAL_DOFS)
    assert reply.accepted
    assert [v.name for v in reply.verdicts] == list(CANONICAL_DOFS)
    for verdict in reply.verdicts:
        assert verdict.renderable, verdict.message
        assert verdict.renders_as, f"{verdict.name} must say what it drives"


def test_declare_reports_the_channel_order_and_stream(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, *CANONICAL_DOFS)
    assert list(reply.continuous_channel_order) == list(CANONICAL_DOFS)
    assert reply.continuous_stream_name == "MyoGestic_Output"
    assert reply.standard_version == "1"


def test_declare_states_how_to_encode_the_stream(v2):
    """An unspecified encoding is what made the first v2 build invert every joint."""
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.prediction.index")
    assert reply.continuous_encoding != pb2.ENCODING_UNSPECIFIED
    assert reply.continuous_encoding in (pb2.CANONICAL, pb2.LEGACY_NEGATED)


def test_every_named_channel_is_actually_rendered(v2):
    """The order may only name channels this hand drives.

    This began as "channels 6-8 are dead, so nothing may name a wrist" — naming a dead
    channel being how four wrong pose tables spread. All nine render now, so the claim can
    no longer be about which names are absent; it is that every name present is renderable,
    which is the property that mattered all along.
    """
    stub, pb2 = v2
    reply = _declare(stub, pb2, *CANONICAL_DOFS)
    order = list(reply.continuous_channel_order)
    assert len(order) == len(CANONICAL_DOFS) == 9
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
        pb2.SetControlRequest(continuous={n: 0.0 for n in CANONICAL_DOFS}), timeout=10.0
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


@pytest.mark.parametrize("name", CANONICAL_DOFS)
def test_a_sweep_moves_exactly_the_bones_the_name_denotes(v2, name):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    moved = {o.element for o in reply.observed}
    assert moved == set(EXPECTED[name]), f"{name} moved {sorted(moved)}"
    assert reply.matched_expectation


@pytest.mark.parametrize("name", CANONICAL_DOFS)
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


@pytest.mark.parametrize("name", CANONICAL_DOFS)
def test_the_extension_half_is_the_exact_mirror(v2, name):
    """No clamping anywhere: this is what makes the canonical domain signed.

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


# --- the recording aid: a separate service, and deliberately not control ---------


@pytest.fixture
def aid(v2_pb2, vhi_process):
    """A training-aid stub on the same live VHI, on the same port."""
    import grpc

    pb2, pb2_grpc = v2_pb2
    channel = grpc.insecure_channel("127.0.0.1:50051")
    stub = pb2_grpc.VhiTrainingAidStub(channel)
    yield stub, pb2
    # Never leave a program running for the next test.
    stub.StopTrainingProgram(pb2.StopTrainingProgramRequest(), timeout=10.0)
    stub.SetRecordingSession(pb2.SetRecordingSessionRequest(active=False), timeout=10.0)
    channel.close()


def test_the_aid_is_a_separate_service_on_the_same_port(aid):
    """Structural separation: control and recording are different services."""
    stub, pb2 = aid
    state = stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0)
    assert list(state.available_movements), "the aid discovers movements on its own"


def test_the_recording_session_gate_round_trips(aid):
    stub, pb2 = aid
    assert stub.SetRecordingSession(
        pb2.SetRecordingSessionRequest(active=True), timeout=10.0
    ).applied
    assert stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0).recording_session_active
    assert stub.SetRecordingSession(
        pb2.SetRecordingSessionRequest(active=False), timeout=10.0
    ).applied
    assert not stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).recording_session_active


def test_a_training_program_runs_and_reports_itself(aid):
    stub, pb2 = aid
    movement = stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements[1]
    ack = stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movement, frequency_hz=1.0), timeout=10.0
    )
    assert ack.applied, ack.message
    state = stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0)
    assert state.program_running
    assert state.program_movement == movement


def test_a_second_program_is_refused_rather_than_swapped(aid):
    """A recording is aligned against the running trajectory; swapping corrupts it."""
    stub, pb2 = aid
    movements = stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements
    assert stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[1]), timeout=10.0
    ).applied
    second = stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[2]), timeout=10.0
    )
    assert not second.applied
    assert "already running" in second.message


def test_an_unknown_movement_is_refused_with_what_is_available(aid):
    stub, pb2 = aid
    ack = stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement="not-a-movement"), timeout=10.0
    )
    assert not ack.applied
    assert "offers" in ack.message


def test_stopping_a_program_is_idempotent(aid):
    """Teardown calls this without knowing whether anything is running."""
    stub, pb2 = aid
    assert stub.StopTrainingProgram(pb2.StopTrainingProgramRequest(), timeout=10.0).applied
    assert stub.StopTrainingProgram(pb2.StopTrainingProgramRequest(), timeout=10.0).applied
    assert not stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0).program_running


def test_a_running_program_owns_the_control_hand(aid, v2):
    """The aid must not be silently overridden mid-recording — nor silently override.

    This is the guard that keeps a recording aid from changing what a discrete DOF
    means: it does not redefine "hold this state", it refuses to be interrupted, and
    says so.
    """
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements
    assert aid_stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[1]), timeout=10.0
    ).applied

    ack = control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": movements[2]}), timeout=10.0
    )
    assert not ack.applied
    assert "training program is running" in ack.rejected["vhi.control.gesture"]


def test_discrete_control_works_again_once_the_program_stops(aid, v2):
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements
    aid_stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[1]), timeout=10.0
    )
    aid_stub.StopTrainingProgram(pb2.StopTrainingProgramRequest(), timeout=10.0)
    ack = control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": movements[2]}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


def test_continuous_control_is_unaffected_by_a_running_program(aid, v2):
    """The program drives the *control* hand; continuous DOFs drive the predicted one."""
    aid_stub, pb2 = aid
    control_stub, _ = v2
    movements = aid_stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements
    aid_stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[1]), timeout=10.0
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


def test_the_training_state_reports_the_current_movement(v2, aid, movements):
    """The palette highlights it, and must not need the v1 control service to."""
    aid_stub, pb2 = aid
    control_stub, _ = v2
    target = movements[1]
    assert control_stub.SetControl(
        pb2.SetControlRequest(discrete={"vhi.control.gesture": target}), timeout=10.0
    ).applied
    state = aid_stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0)
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
    movements = aid_stub.GetTrainingState(
        pb2.GetTrainingStateRequest(), timeout=10.0
    ).available_movements
    # Commanding a movement is only possible in Movement mode, so the aid's program
    # start/stop is the way back: StopTrainingProgram rests via SetMovement.
    control_stub.SetControl(pb2.SetControlRequest(continuous={}), timeout=10.0)
    aid_stub.StartTrainingProgram(
        pb2.StartTrainingProgramRequest(movement=movements[0]), timeout=10.0
    )
    aid_stub.StopTrainingProgram(pb2.StopTrainingProgramRequest(), timeout=10.0)


def _declare_pose(stub, pb2, encoding, dofs=("vhi.prediction.index",), kind=None):
    kind = kind if kind is not None else pb2.CONTINUOUS
    return stub.Declare(
        pb2.DeclareRequest(
            standard_version="1",
            client_name="control-pose-test",
            control_pose_encoding=encoding,
            dofs=[
                pb2.DofDeclaration(name=n, kind=kind, lo=-1.0, hi=1.0, states=[])
                for n in dofs
            ],
        ),
        timeout=10.0,
    )


def test_not_declaring_a_control_pose_leaves_the_stream_unmentioned(v2):
    """The additive guarantee: an existing client's handshake is unchanged.

    Every client written before this field sends ENCODING_UNSPECIFIED by omission, and
    must see exactly what it saw before — no stream name, no order, no mode change.
    """
    stub, pb2 = v2
    reply = _declare(stub, pb2, "vhi.prediction.index")
    assert reply.accepted
    assert reply.control_pose_stream_name == ""
    assert list(reply.control_pose_channel_order) == []
    assert reply.control_pose_encoding == pb2.ENCODING_UNSPECIFIED


def test_declaring_a_canonical_control_pose_is_accepted(v2, rest_control_hand):
    stub, pb2 = v2
    reply = _declare_pose(stub, pb2, pb2.CANONICAL)
    assert reply.accepted, [v.message for v in reply.verdicts]
    assert reply.control_pose_stream_name == "MyoGestic_ControlPose"
    # The control hand's own vocabulary, not the predicted hand's. This asserted
    # `CANONICAL_DOFS` — the prediction names — and passed, because both orders were
    # filled from the prediction table.
    assert list(reply.control_pose_channel_order) == [
        name.replace("vhi.prediction.", "vhi.control.pose.") for name in CANONICAL_DOFS
    ]
    assert reply.control_pose_encoding == pb2.CANONICAL


def test_an_existing_producer_can_negotiate_without_changing_its_numbers(v2, rest_control_hand):
    """The compatibility path: get the handshake, keep renderer units."""
    stub, pb2 = v2
    reply = _declare_pose(stub, pb2, pb2.LEGACY_NEGATED)
    assert reply.accepted
    assert reply.control_pose_encoding == pb2.LEGACY_NEGATED


def test_the_reply_echoes_what_was_applied_not_what_was_asked(v2, rest_control_hand):
    """A client must be able to read the outcome rather than assume its request won."""
    stub, pb2 = v2
    for asked in (pb2.CANONICAL, pb2.LEGACY_NEGATED):
        assert _declare_pose(stub, pb2, asked).control_pose_encoding == asked


def test_a_control_pose_and_a_discrete_dof_are_refused_together(v2, rest_control_hand):
    """Two drivers for one hand — refused at the handshake, not per command.

    A discrete DOF renders as a control-hand movement and a streamed pose drives the
    same bones. v1 arbitrated this per command via ControlMode; saying no up front lets
    the client fix its configuration instead of watching things not happen.
    """
    stub, pb2 = v2
    reply = stub.Declare(
        pb2.DeclareRequest(
            standard_version="1",
            control_pose_encoding=pb2.CANONICAL,
            dofs=[
                pb2.DofDeclaration(name="vhi.prediction.index.flexion", kind=pb2.CONTINUOUS, lo=-1.0, hi=1.0),
                pb2.DofDeclaration(name="vhi.control.gesture", kind=pb2.DISCRETE, states=["rest", "fist"]),
            ],
        ),
        timeout=10.0,
    )
    assert not reply.accepted
    grasp = next(v for v in reply.verdicts if v.name == "vhi.control.gesture")
    assert not grasp.renderable
    assert "control-pose stream" in grasp.message
    # The continuous DOF is still fine — the refusal is specific, not a blanket no.
    assert next(v for v in reply.verdicts if v.name == "vhi.prediction.index.flexion").renderable


def test_a_control_pose_may_be_declared_with_no_dofs_at_all(v2, rest_control_hand):
    """Streaming the control hand is a legitimate thing to negotiate on its own."""
    stub, pb2 = v2
    reply = stub.Declare(
        pb2.DeclareRequest(standard_version="1", control_pose_encoding=pb2.CANONICAL),
        timeout=10.0,
    )
    assert reply.accepted
    assert reply.control_pose_encoding == pb2.CANONICAL


# --- Direction: canonical +1 renders what the DOF name denotes ------------------
#
# `EXPECTED` above is a table, and a table can be inverted by a careless edit as easily
# as the rig can — that is exactly what happened once: the renderer negated every
# channel on ingest, the table was filled in from a sweep of that renderer, and the
# suite agreed with the rig while both disagreed with the DOF names. So these tests take
# their expectation from `MovementPoses`, the rig's own library of what the hand looks
# like in a named posture, and never from a measurement.

SOURCE = pathlib.Path(__file__).resolve().parent.parent / "src"

#: A sweep reports `element` as a bone *name*; the pose library is indexed by joint. This
#: is `PredictedHandSkeleton.boneNames`, read rather than restated.
BONE_NAMES = re.findall(r'"(WaveBone_\d+)"', (SOURCE / "PredictedHandSkeleton.cs").read_text())

#: joint index -> the Euler degrees of `Movements.Fist`, the fully-closed hand. Flexion is
#: what closing a hand does, so this is the pose a canonical +1 flexion must produce.
FIST = {
    int(joint): tuple(float(axis) for axis in (x, y, z))
    for joint, x, y, z in re.findall(
        r"poses\[Movements\.Fist\]\[(\d+)\]\[0\] = \[\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\s*\]",
        (SOURCE / "MovementDefinitions.cs").read_text(),
    )
}

#: The flexion DOFs: every advertised control except the thumb's second axis. Not
#: `endswith(".flexion")` — a single-axis digit carries no suffix, because `index` cannot
#: mean anything else. Only the thumb, with two axes, names them.
#: The wrist is excluded: a fist does not move it, so `Movements.Fist` is no evidence
#: about its direction. It has its own anchor below.
FLEXION_DOFS = tuple(
    name
    for name in CANONICAL_DOFS
    if not name.endswith(".abduction") and ".wrist" not in name
)


def _sweep(v2, name):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name=name, duration_s=1.2, both_directions=True), timeout=25.0
    )
    assert reply.completed, reply.message
    assert reply.observed, f"{name} moved nothing — an empty sweep proves nothing"
    return {BONE_NAMES.index(o.element): o for o in reply.observed}


def test_the_pose_library_was_actually_parsed():
    """Guards the two regexes: a rename upstream must fail loudly, not vacuously."""
    assert len(BONE_NAMES) == 16, BONE_NAMES
    assert FIST, "no Fist pose parsed — the direction tests below would assert nothing"
    assert FIST[4][0] == -85.0, FIST[4]


@pytest.mark.parametrize("name", FLEXION_DOFS)
def test_canonical_plus_one_renders_the_fist_pose(v2, name):
    """The direction anchor. Canonical +1 closes the hand; it does not open it.

    A renderer that negates on ingest puts these bones at `+85°` — which is near
    `IndexExtension`'s `+20°` and on the opposite side of rest from `Fist`. That is a
    hand extending when it was told to flex, and it is what this catches.
    """
    for joint, observed in _sweep(v2, name).items():
        flexed = FIST[joint][0]
        assert flexed < 0.0, f"joint {joint}: the fist's X is not negative — re-read FIST"
        assert observed.degrees_at_hi == pytest.approx(flexed, abs=0.5), (
            f"{name} joint {joint} ({observed.element}): canonical +1 rendered "
            f"{observed.degrees_at_hi:+.1f}°, but a closed hand is {flexed:+.1f}°"
        )


def test_canonical_plus_one_abducts_away_from_the_fist(v2):
    """The one inverted channel, and why it is inverted.

    A fist wraps the thumb *across* the palm, so the library's thumb Z is adduction. The
    DOF is named abduction, so canonical +1 must render the other way — the reason
    `canonicalSign[1]` is `-1` while the five flexion channels are `+1`. Getting this
    right by negating everything, as the old ingest did, made abduction correct and all
    five flexion DOFs backwards.
    """
    for joint, observed in _sweep(v2, "vhi.prediction.thumb.abduction").items():
        adducted = FIST[joint][2]
        assert adducted != 0.0, f"joint {joint} has no Z gain and cannot abduct"
        assert observed.degrees_at_hi == pytest.approx(-adducted, abs=0.5), (
            f"thumb abduction joint {joint} ({observed.element}): canonical +1 rendered "
            f"{observed.degrees_at_hi:+.1f}°, but adduction is {adducted:+.1f}°"
        )


def test_direction_is_the_same_on_every_repeat(v2):
    """Three runs, one answer — the property a sign that depends on state would break."""
    runs = [
        {joint: round(o.degrees_at_hi, 3) for joint, o in _sweep(v2, "vhi.prediction.index").items()}
        for _ in range(3)
    ]
    assert runs[0] == runs[1] == runs[2], runs


def test_direction_does_not_depend_on_the_control_pose_declaration(v2, rest_control_hand):
    """The predicted hand's direction is not something a client can negotiate.

    Its conversion is deliberately ungated: `DeclareReply.continuous_encoding` reports
    CANONICAL unconditionally, so it must also *be* canonical unconditionally. When the
    ingest negation claimed in a comment to be "gated behind the handshake" while
    negating regardless, this is the asymmetry that made the direction look like it
    depended on whether the control-pose stream was declared.
    """
    stub, pb2 = v2
    rendered = {}
    for label, encoding in (
        ("predicted-only", pb2.ENCODING_UNSPECIFIED),
        ("predicted+control-pose", pb2.CANONICAL),
    ):
        reply = _declare_pose(stub, pb2, encoding)
        assert reply.accepted, reply.message
        assert reply.continuous_encoding == pb2.CANONICAL
        rendered[label] = {
            joint: round(o.degrees_at_hi, 3)
            for joint, o in _sweep(v2, "vhi.prediction.index").items()
        }
    assert rendered["predicted-only"] == rendered["predicted+control-pose"], rendered
    assert all(deg < 0.0 for deg in rendered["predicted-only"].values()), rendered


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
    reply = _declare_pose(stub, pb2, pb2.CANONICAL)
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
    assert WRIST["WristUpDown"][0][0] == 30.0
    assert WRIST["WristLeftRight"][0][2] == 20.0


def test_canonical_plus_one_flexes_the_wrist_by_the_documented_amount(v2):
    """The wrist's X anchor: the library's extreme, with flexion's sign.

    `Movements.WristUpDown` gives the magnitude (30°) but names neither side; the sign
    comes from the rule that holds across this rig — negative X is flexion. So a canonical
    +1 on wrist flexion is the negative extreme, not the positive one.
    """
    magnitude = abs(WRIST["WristUpDown"][0][0])
    observed = _sweep(v2, "vhi.prediction.wrist.flexion")
    assert set(observed) == {0}, f"the wrist moved joints {sorted(observed)}"
    assert observed[0].degrees_at_hi == pytest.approx(-magnitude, abs=0.5)


def test_wrist_abduction_uses_the_librarys_magnitude(v2):
    """The Z axis: magnitude derived, sign chosen.

    `Movements.WristLeftRight` defines ±20° and calls neither side abduction, so only the
    magnitude is evidence here. The sign is `CanonicalPose.Wrist`'s documented choice —
    taken by analogy with flexion — and this pins it so that flipping it is a deliberate
    edit with a failing test attached, rather than a silent change of direction.
    """
    magnitude = abs(WRIST["WristLeftRight"][0][2])
    observed = _sweep(v2, "vhi.prediction.wrist.abduction")
    assert set(observed) == {0}
    assert observed[0].degrees_at_hi == pytest.approx(-magnitude, abs=0.5)


def test_wrist_rotation_pins_a_choice_not_a_derivation(v2):
    """Rotation is the one wrist axis with no evidence behind it.

    `Movements.WristUpDown` and `WristLeftRight` define joint 0's X and Z; nothing in the
    library touches its Y. So the range and the sign were both picked — 90 degrees for the
    human range, negative so canonical +1 is pronation — and this test exists to make
    changing either a deliberate edit with a failing assertion attached, rather than a
    silent change to what +1 means.

    It also checks the axis is isolated: rotation must turn bone 0 and nothing else, and
    must not leak into the flexion or abduction read-back, which share that bone.
    """
    observed = _sweep(v2, "vhi.prediction.wrist.rotation")
    assert set(observed) == {0}, f"rotation moved joints {sorted(observed)}"
    assert observed[0].degrees_at_hi == pytest.approx(-179.0, abs=0.5)
    assert observed[0].degrees_at_lo == pytest.approx(179.0, abs=0.5)
    # 179, not 180, and the one degree is load-bearing: `GetEuler` returns angles in
    # (-180, +180], so -180 and +180 are the same orientation and the decode picks the
    # positive one. At exactly 180 the pose is right and the *read-back inverts* — a
    # commanded +1 reports as -1. Measured, not assumed.
    assert abs(observed[0].degrees_at_hi) < 180.0
