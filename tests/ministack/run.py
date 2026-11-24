"""Run a test command with the pinned MiniStack process on an isolated loopback endpoint."""

from contextlib import closing, contextmanager
from http.client import HTTPConnection, HTTPException
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time


class Interruption:
    def __init__(self):
        self.signum = None

    def request(self, signum, _frame):
        if self.signum is None:
            self.signum = signum

    def check(self):
        if self.signum is not None:
            raise SystemExit(128 + self.signum)


def signal_group(process, signum):
    try:
        os.killpg(process.pid, signum)
    except ProcessLookupError:
        pass
    except PermissionError:
        if process.poll() is None:
            raise


def stop_process(process):
    signal_group(process, signal.SIGTERM)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    # The leader may exit while workers from its process group are still running.
    signal_group(process, signal.SIGKILL)
    process.wait()


@contextmanager
def managed_process(command, **kwargs):
    process = subprocess.Popen(command, start_new_session=True, **kwargs)
    try:
        yield process
    finally:
        stop_process(process)


def run_command(command, environment, interruption, check=False):
    interruption.check()
    with managed_process(command, env=environment) as process:
        while True:
            interruption.check()
            try:
                status = process.wait(timeout=0.25)
                break
            except subprocess.TimeoutExpired:
                continue
    interruption.check()
    if check and status != 0:
        raise subprocess.CalledProcessError(status, command)
    return status


def loopback_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def wait_for_health(process, port, interruption):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        interruption.check()
        if process.poll() is not None:
            raise RuntimeError("MiniStack exited before becoming healthy.")
        try:
            with closing(HTTPConnection("127.0.0.1", port, timeout=1)) as connection:
                connection.request("GET", "/_ministack/health")
                with connection.getresponse() as response:
                    if response.status == 200:
                        return
        except (OSError, HTTPException):
            pass
        time.sleep(0.1)
    raise RuntimeError("MiniStack did not become healthy in 30 seconds.")


def run(command, interruption):
    environment = {
        key: value
        for key, value in os.environ.items()
        if not key.upper().startswith(("AWS_", "MINISTACK_"))
        and key.upper() != "SCALESET_MINISTACK_ENDPOINT"
    }
    local_environment = {
        key: value
        for key, value in environment.items()
        if key.upper() not in {"HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"}
    }
    local_environment.update(
        AWS_ACCESS_KEY_ID="000000000000",
        AWS_SECRET_ACCESS_KEY="test-only-secret",
        AWS_CONFIG_FILE=os.devnull,
        AWS_SHARED_CREDENTIALS_FILE=os.devnull,
        AWS_EC2_METADATA_DISABLED="true",
    )

    requirements = Path(__file__).resolve().with_name("requirements.txt")
    with tempfile.TemporaryDirectory(prefix="scaleset-ministack-") as temporary:
        directory = Path(temporary)
        venv = directory / "venv"
        run_command(
            [sys.executable, "-I", "-m", "venv", venv],
            environment, interruption, check=True,
        )
        executable = venv / "bin" / "python"
        run_command(
            [executable, "-I", "-m", "pip", "--isolated", "install",
             "--disable-pip-version-check", "--index-url", "https://pypi.org/simple",
             "--requirement", requirements],
            dict(environment, PIP_CONFIG_FILE=os.devnull), interruption, check=True,
        )

        port = loopback_port()
        endpoint = f"http://127.0.0.1:{port}"
        emulator_environment = dict(
            local_environment, BIND_HOST="127.0.0.1", GATEWAY_PORT=str(port),
            MINISTACK_HOST="127.0.0.1", PERSIST_STATE="0", AUTH="false",
        )
        log_path = directory / "ministack.log"
        interruption.check()
        with log_path.open("w", encoding="utf-8") as log, managed_process(
            [executable, "-I", "-m", "ministack"], cwd=directory,
            env=emulator_environment, stdout=log, stderr=subprocess.STDOUT,
        ) as process:
            try:
                wait_for_health(process, port, interruption)
                print(f"MiniStack ready at {endpoint}", flush=True)
                return run_command(
                    command, dict(local_environment, AWS_ENDPOINT_URL=endpoint,
                                  SCALESET_MINISTACK_ENDPOINT=endpoint),
                    interruption,
                )
            except RuntimeError:
                print(log_path.read_text(encoding="utf-8"), file=sys.stderr)
                raise


if __name__ == "__main__":
    if sys.version_info < (3, 11):
        sys.exit("Use Python 3.11 or newer for the pinned MiniStack test environment.")
    if len(sys.argv) < 2:
        sys.exit("Usage: python3 tests/ministack/run.py <test-command> [args...]")

    interruption = Interruption()
    signal.signal(signal.SIGINT, interruption.request)
    signal.signal(signal.SIGTERM, interruption.request)
    status = run(sys.argv[1:], interruption)
    interruption.check()
    sys.exit(status)
