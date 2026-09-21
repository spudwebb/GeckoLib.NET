"""
Cross-check GeckoLib.NET's decoding against geckolib, snapshot by snapshot.

geckolib ships 54 captured spa snapshots. Each one carries a full 1024-byte status block
plus the pack identity needed to interpret it, which makes them the best conformance
corpus available for the port: no spa, no network, and real data across several pack
families.

For each snapshot this loads it with geckolib, dumps every accessor value, asks the C#
console app to decode the same bytes, and diffs the two. A mismatch means the port
disagrees with the reference implementation about what the spa is saying.

Usage:
    python snapshot_conformance.py [--snapshots DIR] [--exe PATH] [--verbose]
"""

from __future__ import annotations

import argparse
import asyncio
import subprocess
import sys
import tempfile
from pathlib import Path

from geckolib.driver.async_spastruct import GeckoAsyncStructure
from geckolib.utils.snapshot import GeckoSnapshot

import geckolib_source

DEFAULT_EXE = (
    Path(__file__).parent.parent / "GeckoConsoleApp" / "bin" / "Debug" / "net462" / "GeckoConsoleApp.exe"
)


def format_value(value: object) -> str:
    """Render a value the way the C# dump does, so the two are comparable."""
    if isinstance(value, bool):
        return "True" if value else "False"
    if isinstance(value, float):
        return f"{value:.1f}"
    return str(value)


async def dump_with_geckolib(snapshot: GeckoSnapshot) -> dict[str, str]:
    """Build the accessors geckolib would and read every one of them."""
    struct = GeckoAsyncStructure(None)
    struct.replace_status_block_segment(0, snapshot.bytes)
    await struct.load_pack_class(snapshot.packtype.lower())
    await struct.load_config_module(snapshot.config_version)
    await struct.load_log_module(snapshot.log_version)
    await struct.check_for_accessories()
    struct.build_accessors()

    return {key: format_value(accessor.value) for key, accessor in struct.accessors.items()}


def dump_with_geckolib_net(exe: Path, block: Path, snapshot: GeckoSnapshot) -> dict[str, str]:
    """Ask the C# console app to decode the same bytes."""
    result = subprocess.run(  # noqa: S603
        [
            str(exe),
            "--block", str(block),
            "--pack", snapshot.packtype,
            "--config", str(snapshot.config_version),
            "--log", str(snapshot.log_version),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )

    if result.returncode != 0:
        msg = f"console app exited {result.returncode}: {result.stderr.strip()}"
        raise RuntimeError(msg)

    values: dict[str, str] = {}
    for line in result.stdout.splitlines():
        if "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key] = value
    return values


def compare(name: str, expected: dict[str, str], actual: dict[str, str], *, verbose: bool) -> list[str]:
    """Compare two accessor dumps and describe every difference."""
    problems: list[str] = []

    missing = sorted(set(expected) - set(actual))
    extra = sorted(set(actual) - set(expected))
    if missing:
        problems.append(f"{name}: {len(missing)} accessors missing from C#: {missing[:5]}")
    if extra:
        problems.append(f"{name}: {len(extra)} unexpected accessors in C#: {extra[:5]}")

    for key in sorted(set(expected) & set(actual)):
        if expected[key] != actual[key]:
            problems.append(f"{name}: {key}: geckolib={expected[key]!r} C#={actual[key]!r}")

    if verbose and not problems:
        print(f"  {name}: {len(expected)} accessors match")

    return problems


async def main() -> int:
    """Run the conformance check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--snapshots",
        type=Path,
        default=None,
        help="geckolib's captured snapshots. Found automatically if not given.",
    )
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    if not args.exe.is_file():
        print(f"error: console app not built: {args.exe}", file=sys.stderr)
        return 2

    snapshots = args.snapshots or geckolib_source.snapshots_dir()
    files = sorted(snapshots.glob("*.snapshot"))
    if not files:
        print(f"error: no snapshots in {snapshots}", file=sys.stderr)
        return 2

    problems: list[str] = []
    compared = 0
    accessors = 0
    skipped: list[str] = []

    with tempfile.TemporaryDirectory() as workspace:
        block_path = Path(workspace) / "block.bin"

        for path in files:
            for snapshot in GeckoSnapshot.parse_log_file(str(path)):
                name = f"{path.stem}[{snapshot.packtype} {snapshot.config_version}/{snapshot.log_version}]"

                try:
                    expected = await dump_with_geckolib(snapshot)
                except ModuleNotFoundError as err:
                    # geckolib itself has no definitions for this combination.
                    skipped.append(f"{name}: {err}")
                    continue

                block_path.write_bytes(snapshot.bytes)

                try:
                    actual = dump_with_geckolib_net(args.exe, block_path, snapshot)
                except RuntimeError as err:
                    problems.append(f"{name}: {err}")
                    continue

                problems.extend(compare(name, expected, actual, verbose=args.verbose))
                compared += 1
                accessors += len(expected)

    print(f"snapshots compared : {compared}")
    print(f"accessors compared : {accessors}")

    if skipped:
        print(f"skipped            : {len(skipped)} (no geckolib definitions)")
        for note in skipped:
            print(f"  {note}")

    if problems:
        print(f"MISMATCHES         : {len(problems)}", file=sys.stderr)
        for problem in problems[:50]:
            print(f"  {problem}", file=sys.stderr)
        if len(problems) > 50:
            print(f"  ... and {len(problems) - 50} more", file=sys.stderr)
        return 1

    print("mismatches         : 0")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
