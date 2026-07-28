"""Machine-check what the v2 canonical service actually does to the rig.

Until this file existed, the only assertion about VHI's rig behaviour lived in
MyoGestic's test suite as a mapping *read out of this source* — which cannot catch a
change made here. These tests drive a live VHI and check what it reports back.

The interesting ones are the sweeps. ``SweepControl`` drives one named DOF across its
range and reports which bones moved and by how many signed degrees, read back off the
skeleton. That turns three claims into assertions:

- **Identity.** ``index.flexion`` moves the index bones and nothing else.
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

import pytest

#: Canonical name -> the bones it must move, and the signed degrees at canonical +1.
#:
#: Read off `PredictedHandSkeleton.jointMovements` and confirmed against a live sweep.
#: A canonical +1 is sent to the rig negated, so the rotation is `-gain`.
#:
#: Thumb abduction has only two entries on purpose: it drives all three thumb bones
#: through channel 1, but the distal bone's Z gain is 0, so it cannot move.
EXPECTED = {
    "thumb.flexion": {"WaveBone_3": 45.0, "WaveBone_4": 55.0, "WaveBone_5": 80.0},
    "thumb.abduction": {"WaveBone_3": -30.0, "WaveBone_4": 35.0},
    "index.flexion": {"WaveBone_7": 85.0, "WaveBone_8": 75.0, "WaveBone_9": 60.0},
    "middle.flexion": {"WaveBone_12": 85.0, "WaveBone_13": 85.0, "WaveBone_14": 60.0},
    "ring.flexion": {"WaveBone_17": 85.0, "WaveBone_18": 85.0, "WaveBone_19": 60.0},
    "little.flexion": {"WaveBone_22": 85.0, "WaveBone_23": 85.0, "WaveBone_24": 60.0},
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
    reply = _declare(stub, pb2, "index.flexion")
    assert reply.continuous_encoding != pb2.ENCODING_UNSPECIFIED
    assert reply.continuous_encoding in (pb2.CANONICAL, pb2.LEGACY_NEGATED)


def test_no_dead_channel_is_ever_named(v2):
    """Channels 6-8 are read by nothing. Naming one is how four wrong tables began."""
    stub, pb2 = v2
    order = list(_declare(stub, pb2, *CANONICAL_DOFS).continuous_channel_order)
    assert len(order) == 6
    assert not [name for name in order if "wrist" in name]


def test_a_dof_this_hand_lacks_is_refused_with_what_it_has(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "wrist.rotation")
    assert not reply.accepted
    verdict = reply.verdicts[0]
    assert not verdict.renderable
    assert "index.flexion" in verdict.message, "a refusal must be actionable"


def test_one_unrenderable_dof_fails_the_whole_declaration(v2):
    """All-or-nothing: a client must not half-render and believe it succeeded."""
    stub, pb2 = v2
    reply = _declare(stub, pb2, "index.flexion", "wrist.rotation")
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
    reply = _declare(stub, pb2, "hand.grasp", kind=pb2.DISCRETE, states=states)
    assert reply.accepted, reply.verdicts[0].message
    assert "control-hand movements" in reply.verdicts[0].renders_as


def test_discrete_states_resolve_case_insensitively(v2, movements):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "hand.grasp", kind=pb2.DISCRETE, states=[movements[0].upper()])
    assert reply.accepted, reply.verdicts[0].message


def test_a_discrete_dof_with_an_unknown_state_is_refused(v2, movements):
    """Partly-resolvable is not partly-renderable — it silently does nothing."""
    stub, pb2 = v2
    reply = _declare(
        stub, pb2, "hand.grasp", kind=pb2.DISCRETE, states=[movements[0], "no-such-movement"]
    )
    assert not reply.accepted
    verdict = reply.verdicts[0]
    assert "no-such-movement" in verdict.message
    assert movements[0] in verdict.message, "the refusal must list what is available"


def test_a_discrete_dof_with_no_states_is_refused(v2):
    stub, pb2 = v2
    reply = _declare(stub, pb2, "hand.grasp", kind=pb2.DISCRETE, states=[])
    assert not reply.accepted


def test_setcontrol_applies_a_discrete_state(v2, movements):
    stub, pb2 = v2
    ack = stub.SetControl(
        pb2.SetControlRequest(discrete={"hand.grasp": movements[0]}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


def test_setcontrol_rejects_an_unresolvable_state(v2):
    stub, pb2 = v2
    ack = stub.SetControl(
        pb2.SetControlRequest(discrete={"hand.grasp": "no-such-movement"}), timeout=10.0
    )
    assert not ack.applied
    assert "hand.grasp" in ack.rejected


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
    ack = stub.SetControl(pb2.SetControlRequest(continuous={"index.flexion": bad}), timeout=10.0)
    assert not ack.applied
    assert "index.flexion" in ack.rejected


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
    for o in reply.observed:
        assert o.degrees_at_lo == pytest.approx(-o.degrees_at_hi, abs=0.5), o.element
        assert not math.isclose(o.degrees_at_hi, 0.0, abs_tol=0.5)


def test_a_one_directional_sweep_leaves_the_other_half_alone(v2):
    stub, pb2 = v2
    reply = stub.SweepControl(
        pb2.SweepControlRequest(name="index.flexion", duration_s=1.0, both_directions=False),
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
        pb2.SweepControlRequest(name="index.flexion", duration_s=1.0, both_directions=True),
        timeout=25.0,
    )
    after = stub.SweepControl(
        pb2.SweepControlRequest(name="thumb.flexion", duration_s=1.0, both_directions=False),
        timeout=25.0,
    )
    # thumb.flexion's sweep only reports thumb bones; had the index sweep left the
    # hand flexed, index bones would show up in this unrelated sweep's scan.
    assert {o.element for o in after.observed} <= set(EXPECTED["thumb.flexion"])


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
        pb2.SetControlRequest(discrete={"hand.grasp": movements[2]}), timeout=10.0
    )
    assert not ack.applied
    assert "training program is running" in ack.rejected["hand.grasp"]


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
        pb2.SetControlRequest(discrete={"hand.grasp": movements[2]}), timeout=10.0
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
        pb2.SetControlRequest(continuous={"index.flexion": 0.5}), timeout=10.0
    )
    assert ack.applied, dict(ack.rejected)


# --- layer 3: presentation blending, and what it must NOT be ---------------------


def test_presentation_blending_can_be_configured(v2):
    stub, pb2 = v2
    assert stub.SetPresentation(
        pb2.SetPresentationRequest(blend=True, blend_speed=8.0), timeout=10.0
    ).applied
    assert _declare(stub, pb2, "index.flexion").blends_presentation
    assert stub.SetPresentation(pb2.SetPresentationRequest(blend=False), timeout=10.0).applied
    assert not _declare(stub, pb2, "index.flexion").blends_presentation


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
            pb2.SweepControlRequest(name="index.flexion", duration_s=1.5, both_directions=True),
            timeout=25.0,
        )
        assert reply.completed, reply.message
        readings[blend] = {o.element: o.degrees_at_hi for o in reply.observed}
    stub.SetPresentation(pb2.SetPresentationRequest(blend=False), timeout=10.0)
    assert readings[False].keys() == readings[True].keys()
    for element, degrees in readings[False].items():
        assert readings[True][element] == pytest.approx(degrees, abs=0.5), element


def test_the_training_state_reports_the_current_movement(v2, aid, movements):
    """The palette highlights it, and must not need the v1 control service to."""
    aid_stub, pb2 = aid
    control_stub, _ = v2
    target = movements[1]
    assert control_stub.SetControl(
        pb2.SetControlRequest(discrete={"hand.grasp": target}), timeout=10.0
    ).applied
    state = aid_stub.GetTrainingState(pb2.GetTrainingStateRequest(), timeout=10.0)
    assert state.current_movement == target
