"""
Cross-check GeckoLib.NET's device layer against geckolib, snapshot by snapshot.

tools/snapshot_conformance.py proves the two implementations decode the same raw values.
This goes a level up: it builds geckolib's facade and GeckoLib.NET's facade over the same
captured snapshot and diffs what they conclude - which pumps exist, whether they are
single or two speed, what the heater is doing, which sensors are present.

That is where a port goes wrong in ways raw values do not show, because availability is
inferred from how the pack says its outputs are wired rather than read from a field.

Usage:
    python device_conformance.py [--snapshots DIR] [--exe PATH] [--verbose]
"""

from __future__ import annotations

import argparse
import asyncio
import subprocess
import sys
import tempfile
from pathlib import Path

from geckolib import GeckoAsyncFacade, GeckoAsyncStructure, GeckoAsyncTaskMan
from geckolib.utils.snapshot import GeckoSnapshot

import geckolib_source

DEFAULT_EXE = (
    Path(__file__).parent.parent / "GeckoConsoleApp" / "bin" / "Debug" / "net462" / "GeckoConsoleApp.exe"
)

PUMP_TYPE_NAMES = {0: "None", 1: "SingleSpeed", 2: "TwoSpeed", 3: "VariableSpeed"}


class OfflineSpa:
    """Enough of a spa for the facade, built from a snapshot instead of a connection."""

    def __init__(self, snapshot: GeckoSnapshot) -> None:
        """Initialize from a parsed snapshot."""
        self.struct = GeckoAsyncStructure(None)
        self.struct.replace_status_block_segment(0, snapshot.bytes)
        self.plateform_key = snapshot.packtype.lower()
        self.config_version = snapshot.config_version
        self.log_version = snapshot.log_version

    @property
    def accessors(self) -> dict:
        """Get the accessors."""
        return self.struct.accessors

    @property
    def is_responding_to_pings(self) -> bool:
        """The facade's update loop checks this; offline we simply never respond."""
        return False

    async def async_init(self) -> None:
        """Load the pack definitions and build the accessors."""
        await self.struct.load_pack_class(self.plateform_key)
        await self.struct.load_config_module(self.config_version)
        await self.struct.load_log_module(self.log_version)
        await self.struct.check_for_accessories()
        self.struct.build_accessors()


def fmt(value: object) -> str:
    """Render a value the way the C# dump does, so the two are comparable."""
    if isinstance(value, bool):
        return "True" if value else "False"
    if isinstance(value, float):
        return f"{value:.1f}"
    return str(value)


def dump_with_geckolib(facade: GeckoAsyncFacade) -> dict[str, str]:
    """Read everything the C# side reports, out of geckolib's facade."""
    out: dict[str, str] = {}

    for pump in (facade.pump_1, facade.pump_2, facade.pump_3, facade.pump_4, facade.pump_5):
        out[f"pump.{pump.key}.available"] = fmt(pump.is_available)
        if not pump.is_available:
            continue
        out[f"pump.{pump.key}.type"] = PUMP_TYPE_NAMES[pump.pump_type.value]
        out[f"pump.{pump.key}.mode"] = fmt(pump.mode)
        out[f"pump.{pump.key}.on"] = fmt(pump.is_on)

    out["blower.available"] = fmt(facade.blower.is_available)
    if facade.blower.is_available:
        out["blower.mode"] = fmt(facade.blower.mode)
        out["blower.on"] = fmt(facade.blower.is_on)

    out["waterfall.available"] = fmt(facade.waterfall.is_available)
    if facade.waterfall.is_available:
        out["waterfall.on"] = fmt(facade.waterfall.is_on)

    out["bubblegen.available"] = fmt(facade.bubblegenerator.is_available)

    for light in (facade.light, facade.light2):
        out[f"light.{light.key}.available"] = fmt(light.is_available)
        if not light.is_available:
            continue
        out[f"light.{light.key}.state"] = fmt(light.state)
        out[f"light.{light.key}.on"] = fmt(light.is_on)

    heater = facade.water_heater
    out["heater.available"] = fmt(heater.is_available)
    if heater.is_available:
        out["heater.unit"] = fmt(heater.temperature_unit)
        out["heater.current"] = fmt(heater.current_temperature)
        out["heater.target"] = fmt(heater.target_temperature)
        out["heater.real_target"] = fmt(heater.real_target_temperature)
        out["heater.min"] = fmt(heater.min_temp)
        out["heater.max"] = fmt(heater.max_temp)
        out["heater.operation"] = fmt(heater.current_operation)

    for sensor in facade.sensors:
        out[f"sensor.{sensor.name}"] = fmt(sensor.state)
    for sensor in facade.binary_sensors:
        out[f"binary.{sensor.name}"] = fmt(sensor.is_on)

    out["error.state"] = fmt(facade.error_sensor.state)

    out["eco.available"] = fmt(facade.eco_mode is not None)
    if facade.eco_mode is not None:
        out["eco.on"] = fmt(facade.eco_mode.is_on)

    out["standby.available"] = fmt(facade.standby is not None)
    if facade.standby is not None:
        out["standby.on"] = fmt(facade.standby.is_on)

    out["lockmode.available"] = fmt(facade.lockmode.is_available)
    if facade.lockmode.is_available:
        out["lockmode.state"] = fmt(facade.lockmode.state)

    out["heatpump.available"] = fmt(facade.heatpump.is_available)
    if facade.heatpump.is_available:
        out["heatpump.state"] = fmt(facade.heatpump.state)

    out["ingrid.available"] = fmt(facade.ingrid.is_available)
    if facade.ingrid.is_available:
        out["ingrid.state"] = fmt(facade.ingrid.state)

    out["watercare.available"] = fmt(facade.water_care.is_available)
    out["reminders.available"] = fmt(facade.reminders_manager.is_available)

    out["counts.pumps"] = fmt(len(facade.pumps))
    out["counts.blowers"] = fmt(len(facade.blowers))
    out["counts.lights"] = fmt(len(facade.lights))
    out["counts.keypad_buttons"] = fmt(len(facade.keypad.buttons))
    out["spa.in_use"] = fmt(any(d.is_on for d in facade.all_config_change_devices))

    return out


def dump_with_geckolib_net(exe: Path, block: Path, snapshot: GeckoSnapshot) -> dict[str, str]:
    """Ask the C# console app to build its facade over the same bytes."""
    result = subprocess.run(  # noqa: S603
        [
            str(exe),
            "--block", str(block),
            "--pack", snapshot.packtype,
            "--config", str(snapshot.config_version),
            "--log", str(snapshot.log_version),
            "--devices",
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


def same(left: str, right: str) -> bool:
    """
    Are these the same value?

    Numbers are compared numerically: geckolib's fallback temperature limits are ints
    ("40") where the port uses doubles ("40.0"), which is a difference in how they are
    written down rather than in what they mean.
    """
    if left == right:
        return True
    try:
        return float(left) == float(right)
    except ValueError:
        return False


def compare(name: str, expected: dict[str, str], actual: dict[str, str], *, verbose: bool) -> list[str]:
    """Compare two device dumps and describe every difference."""
    problems: list[str] = []

    for key in sorted(set(expected) | set(actual)):
        if key not in actual:
            problems.append(f"{name}: {key}: missing from C# (geckolib={expected[key]!r})")
        elif key not in expected:
            problems.append(f"{name}: {key}: unexpected in C# ({actual[key]!r})")
        elif not same(expected[key], actual[key]):
            problems.append(f"{name}: {key}: geckolib={expected[key]!r} C#={actual[key]!r}")

    if verbose and not problems:
        print(f"  {name}: {len(expected)} device values match")

    return problems


async def main() -> int:
    """Run the device conformance check."""
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
    problems: list[str] = []
    compared = 0
    values = 0

    taskman = GeckoAsyncTaskMan()
    await taskman.__aenter__()

    try:
        with tempfile.TemporaryDirectory() as workspace:
            block_path = Path(workspace) / "block.bin"

            for path in files:
                for snapshot in GeckoSnapshot.parse_log_file(str(path)):
                    name = f"{path.stem}[{snapshot.packtype} {snapshot.config_version}/{snapshot.log_version}]"

                    spa = OfflineSpa(snapshot)
                    try:
                        await spa.async_init()
                    except ModuleNotFoundError as err:
                        print(f"  skipped {name}: {err}")
                        continue

                    facade = GeckoAsyncFacade(spa, taskman)
                    try:
                        expected = dump_with_geckolib(facade)
                    finally:
                        await facade.disconnect()

                    block_path.write_bytes(snapshot.bytes)
                    try:
                        actual = dump_with_geckolib_net(args.exe, block_path, snapshot)
                    except RuntimeError as err:
                        problems.append(f"{name}: {err}")
                        continue

                    problems.extend(compare(name, expected, actual, verbose=args.verbose))
                    compared += 1
                    values += len(expected)
    finally:
        await taskman.__aexit__(None)

    print(f"snapshots compared : {compared}")
    print(f"device values      : {values}")

    if problems:
        print(f"MISMATCHES         : {len(problems)}", file=sys.stderr)
        for problem in problems[:60]:
            print(f"  {problem}", file=sys.stderr)
        if len(problems) > 60:
            print(f"  ... and {len(problems) - 60} more", file=sys.stderr)
        return 1

    print("mismatches         : 0")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
