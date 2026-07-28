"""Bring a live VHI up and hand the tests a v2 gRPC stub.

The other files in this directory are interactive LSL monitors you run by hand.
``test_v2_contract.py`` is different: it is an automated check of what the rig
actually does, so it needs a running VHI and generated Python stubs.

Both are produced here rather than committed:

- The stubs come from ``proto/myogestic_vhi_v2.proto`` via ``grpcio-tools`` into a
  temp directory. Generating them at session start means the test can never drift
  from the contract — a proto edit is picked up on the next run instead of silently
  testing a stale copy.
- VHI is launched from source with the Godot .NET binary, so the test exercises the
  C# in ``src/`` rather than whatever release happens to be installed.

**Skip versus fail.** Missing *prerequisites* skip: no ``grpcio-tools``, no Godot
binary. Anything else fails. In particular, if Godot is present and the server does
not come up, that is a failure and not a skip — a suite that skips on breakage
reports green for a regression, which is worse than having no suite.

Run it::

    uv run --group test pytest tests/test_v2_contract.py -v

Set ``GODOT_BIN`` if your Godot .NET binary is not at the default macOS location.
An already-running VHI on the port is reused, so a manual session can be tested too.
"""

from __future__ import annotations

import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time

import pytest

REPO = pathlib.Path(__file__).resolve().parent.parent
PROTO = REPO / "proto" / "myogestic_vhi_v2.proto"
GRPC_PORT = 50051
#: Generous: Godot has to boot, load the scene, map bones and start Kestrel.
STARTUP_TIMEOUT_S = 90.0

_DEFAULT_GODOT = [
    "/Applications/Godot.app/Contents/MacOS/Godot",
    "/Applications/Godot_mono.app/Contents/MacOS/Godot",
]


def _godot_binary() -> str | None:
    """A Godot binary that can run C#, or None."""
    candidates = [os.environ.get("GODOT_BIN"), *_DEFAULT_GODOT]
    for candidate in candidates:
        if candidate and pathlib.Path(candidate).exists():
            return candidate
    return shutil.which("godot") or shutil.which("godot4")


def _port_open(port: int = GRPC_PORT) -> bool:
    with socket.socket() as s:
        s.settimeout(0.4)
        return s.connect_ex(("127.0.0.1", port)) == 0


@pytest.fixture(scope="session")
def v2_pb2():
    """The generated v2 protobuf modules, built fresh from the proto."""
    pytest.importorskip("grpc", reason="grpcio is not installed")
    pytest.importorskip("grpc_tools", reason="grpcio-tools is not installed")

    out = pathlib.Path(tempfile.mkdtemp(prefix="vhi-v2-stubs-"))
    result = subprocess.run(
        [
            sys.executable, "-m", "grpc_tools.protoc",
            f"--proto_path={PROTO.parent}",
            f"--python_out={out}",
            f"--grpc_python_out={out}",
            str(PROTO),
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, f"protoc failed:\n{result.stderr}"
    sys.path.insert(0, str(out))
    import myogestic_vhi_v2_pb2 as pb2
    import myogestic_vhi_v2_pb2_grpc as pb2_grpc

    return pb2, pb2_grpc


@pytest.fixture(scope="session")
def vhi_process():
    """A running VHI. Reuses one already on the port, else launches from source."""
    if _port_open():
        print(f"\n[conftest] reusing the VHI already listening on {GRPC_PORT}")
        yield None
        return

    godot = _godot_binary()
    if godot is None:
        pytest.skip(
            "no Godot .NET binary found — set GODOT_BIN to one that can run C# "
            "(the plain build cannot)"
        )

    log = pathlib.Path(tempfile.mkdtemp(prefix="vhi-run-")) / "vhi.log"
    with log.open("w") as handle:
        proc = subprocess.Popen(
            [godot, "--path", str(REPO)],
            stdout=handle,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
        )

    deadline = time.monotonic() + STARTUP_TIMEOUT_S
    while time.monotonic() < deadline:
        if _port_open():
            break
        if proc.poll() is not None:
            pytest.fail(
                f"VHI exited with code {proc.returncode} before serving gRPC.\n"
                f"{log.read_text()[-3000:]}"
            )
        time.sleep(0.5)
    else:
        proc.kill()
        # Deliberately a failure, not a skip: Godot was present and VHI did not
        # come up, which is exactly the regression this suite exists to catch.
        pytest.fail(
            f"VHI did not serve gRPC within {STARTUP_TIMEOUT_S:.0f}s. Has the C# been "
            f"built (`dotnet build VHI_godot.csproj`)?\n{log.read_text()[-3000:]}"
        )

    yield proc
    proc.kill()
    proc.wait(timeout=30)


@pytest.fixture(scope="session")
def v2(v2_pb2, vhi_process):
    """A v2 stub on a live VHI, plus the generated message module."""
    import grpc

    pb2, pb2_grpc = v2_pb2
    channel = grpc.insecure_channel(f"127.0.0.1:{GRPC_PORT}")
    stub = pb2_grpc.VhiCanonicalControlStub(channel)

    # The port opens before the scene finishes wiring, so wait for a real answer
    # rather than for the socket.
    deadline = time.monotonic() + 30.0
    last: Exception | None = None
    while time.monotonic() < deadline:
        try:
            stub.Declare(pb2.DeclareRequest(standard_version="1"), timeout=3.0)
            break
        except Exception as e:  # noqa: BLE001 - retried until the deadline
            last = e
            time.sleep(0.5)
    else:
        pytest.fail(f"v2 service never answered Declare: {last!r}")

    yield stub, pb2
    channel.close()


@pytest.fixture(scope="session")
def movements(v2):
    """The movement names this build offers, discovered rather than assumed."""
    stub, pb2 = v2
    reply = stub.Declare(
        pb2.DeclareRequest(
            standard_version="1",
            dofs=[
                pb2.DofDeclaration(
                    name="hand.grasp", kind=pb2.DISCRETE, states=["definitely-not-a-movement"]
                )
            ],
        ),
        timeout=5.0,
    )
    # The refusal message lists what the hand does have — the only discovery path
    # v2 offers, which is itself worth pinning.
    message = reply.verdicts[0].message
    inside = message.split("offers [")[-1].rstrip("]")
    return [name.strip() for name in inside.split(",") if name.strip()]
