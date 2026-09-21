"""
Run the GeckoConsoleApp shell against a throwaway geckolib simulator.

The simulator is a cmd-style shell, so it needs stdin held open or it reads EOF and
exits. It also binds port 10022 with SO_REUSEADDR, so a second instance will happily
bind alongside a stale one and datagrams go to whichever the OS picks - which looks
exactly like a broken client. This starts one, waits for it to answer, runs the
commands, and always cleans up.

Usage:
    python run_against_simulator.py state exit
    python run_against_simulator.py --snapshot inXM-Heating.snapshot get RhWaterTemp exit
"""

from __future__ import annotations

import argparse
import socket
import subprocess
import sys
import time
from pathlib import Path

import geckolib_source

DEFAULT_EXE = (
    Path(__file__).parent.parent / "GeckoConsoleApp" / "bin" / "Debug" / "net462" / "GeckoConsoleApp.exe"
)
INTOUCH2_PORT = 10022


def simulator_is_answering(timeout: float = 0.4) -> bool:
    """Broadcast a discovery HELLO and see whether anything replies."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        probe.bind(("0.0.0.0", 0))  # noqa: S104
        probe.settimeout(timeout)
        probe.sendto(b"<HELLO>1</HELLO>", ("127.0.0.1", INTOUCH2_PORT))
        try:
            data, _ = probe.recvfrom(4096)
        except socket.timeout:
            return False
        except ConnectionResetError:
            # Windows turns the ICMP "port unreachable" from an unused port into a reset
            # on the next receive. It means nothing is listening, which is what we asked.
            return False
        return data.startswith(b"<HELLO>")


def main() -> int:
    """Start a simulator, run the shell against it, and tidy up."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--snapshot", default="../tests/snapshots/default.snapshot")
    parser.add_argument("commands", nargs="*", help="Shell commands to run, in order.")
    args = parser.parse_args()

    if not args.exe.is_file():
        print(f"error: console app not built: {args.exe}", file=sys.stderr)
        return 2

    if simulator_is_answering():
        print("error: something is already serving port 10022 - stop it first", file=sys.stderr)
        return 2

    simulator = subprocess.Popen(  # noqa: S603
        [sys.executable, "-u", "-m", "geckolib", "simulator"],
        cwd=str(geckolib_source.source_dir()),
        stdin=subprocess.PIPE,  # held open, or the simulator's command loop exits
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        errors="replace",
    )

    try:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if simulator_is_answering():
                break
        else:
            print("error: the simulator never answered discovery", file=sys.stderr)
            return 3

        commands = [*args.commands]
        if not commands or commands[-1] != "exit":
            commands.append("exit")

        result = subprocess.run(  # noqa: S603
            [str(args.exe), "127.0.0.1"],
            input="\n".join(commands) + "\n",
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
        )

        print(result.stdout, end="")
        if result.stderr:
            print(result.stderr, end="", file=sys.stderr)
        return result.returncode

    finally:
        simulator.kill()
        simulator.wait(timeout=10)


if __name__ == "__main__":
    sys.exit(main())
