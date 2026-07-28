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


# --- v1 must keep working ------------------------------------------------------


def test_v1_is_still_served_on_the_same_port(v2_pb2, vhi_process):
    """The migration is additive: adding v2 must not disturb v1's routes."""
    import subprocess
    import sys
    import tempfile
    import pathlib

    import grpc

    v1_proto = pathlib.Path(__file__).resolve().parent.parent / "proto" / "myogestic_vhi.proto"
    out = pathlib.Path(tempfile.mkdtemp(prefix="vhi-v1-stubs-"))
    result = subprocess.run(
        [
            sys.executable, "-m", "grpc_tools.protoc",
            f"--proto_path={v1_proto.parent}",
            f"--python_out={out}", f"--grpc_python_out={out}", str(v1_proto),
        ],
        capture_output=True, text=True, check=False,
    )
    assert result.returncode == 0, result.stderr
    sys.path.insert(0, str(out))
    import myogestic_vhi_pb2 as v1
    import myogestic_vhi_pb2_grpc as v1_grpc

    with grpc.insecure_channel("127.0.0.1:50051") as channel:
        reply = v1_grpc.VhiControlStub(channel).GetState(v1.GetStateRequest(), timeout=10.0)
    assert reply.current_state
    assert list(reply.available_movements)
