"""
Verify packdata.json.gz against geckolib's live generated modules.

The extracted pack data is the foundation of the C# port and the source XML no longer
exists, so this check is deliberately exhaustive: it imports every generated module,
constructs the real accessor objects, and compares all 21,586 of them field by field.

It checks the DERIVED values too - tag, length, struct format and bitmask - by
recomputing them from the extracted data using the same rules the C# will use. That way
this validates both the extraction and our understanding of geckolib's derivation rules
(accessor.py:53-89), which is where a silent misreading would otherwise hide.

Usage:
    python verify_packdata.py [--data FILE]
"""

from __future__ import annotations

import argparse
import gzip
import importlib
import json
import math
import sys
from pathlib import Path
from typing import Any

DEFAULT_DATA = Path(__file__).parent.parent / "GeckoLib.NET" / "Packs" / "packdata.json.gz"

# GeckoTempStructAccessor derives from GeckoWordStructAccessor, so geckolib reports its
# accessor_type as "Word" (accessor.py:287, :380). We keep Temp as a distinct kind because
# it changes the value conversion, but it must compare equal to "Word" here.
KIND_TO_ACCESSOR_TYPE = {
    "Byte": "Byte",
    "Word": "Word",
    "Time": "Time",
    "Bool": "Bool",
    "Enum": "Enum",
    "Temp": "Word",
}


def derive(kind: str, pos: int, bitpos: int | None, size: int | None, maxitems: int | None):
    """Recompute length, struct format and bitmask the way geckolib does."""
    length, fmt = 1, ">B"
    if size is not None:
        length = size
        if length == 2:
            fmt = ">H"
    if KIND_TO_ACCESSOR_TYPE[kind] in ("Word", "Time"):
        length, fmt = 2, ">H"

    bitmask = None
    if bitpos is not None:
        bitmask = 1
    if maxitems is not None:
        bitmask = (1 << math.ceil(math.log2(max(int(maxitems), 2)))) - 1

    return length, fmt, bitmask


class Checker:
    """Accumulates mismatches so one run reports everything, not just the first failure."""

    def __init__(self) -> None:
        self.failures: list[str] = []
        self.accessors = 0
        self.modules = 0

    def eq(self, where: str, field: str, expected: Any, actual: Any) -> None:
        """Compare one field."""
        if expected != actual:
            self.failures.append(f"{where}: {field}: extracted={expected!r} geckolib={actual!r}")


def check_accessor(chk: Checker, where: str, record: list[Any], enum_tables: list[list[str]], live: Any) -> None:
    """Compare one extracted accessor record against the constructed geckolib object."""
    key, path, kind, pos, bitpos, enum_idx, size, maxitems, rw = record
    items = enum_tables[enum_idx] if enum_idx is not None else None
    length, fmt, bitmask = derive(kind, pos, bitpos, size, maxitems)

    chk.eq(where, "path", path, live.path)
    chk.eq(where, "tag", path.split("/")[-1], live.tag)
    chk.eq(where, "pos", pos, live.pos)
    chk.eq(where, "accessor_type", KIND_TO_ACCESSOR_TYPE[kind], live.accessor_type)
    chk.eq(where, "bitpos", bitpos, live.bitpos)
    chk.eq(where, "items", items, live.items)
    chk.eq(where, "maxitems", None if maxitems is None else int(maxitems), live.maxitems)
    chk.eq(where, "read_write", rw, live.read_write)
    chk.eq(where, "length", length, live.length)
    chk.eq(where, "format", fmt, live.format)
    chk.eq(where, "bitmask", bitmask, getattr(live, "bitmask", None))
    chk.accessors += 1

    # Temp accessors must be exactly the ones geckolib implements with the temperature
    # conversion; that distinction is invisible in accessor_type.
    is_temp = type(live).__name__ == "GeckoTempStructAccessor"
    chk.eq(where, "is_temp", kind == "Temp", is_temp)


def check_table(chk: Checker, stem: str, entry: dict, enum_tables: list[list[str]], live_cls: Any) -> None:
    """Compare a whole config/log accessor table."""
    live = live_cls(None).accessors
    extracted = entry["accessors"]

    if len(extracted) != len(live):
        chk.failures.append(f"{stem}: accessor count: extracted={len(extracted)} geckolib={len(live)}")

    extracted_keys = [r[0] for r in extracted]
    if extracted_keys != list(live.keys()):
        missing = set(live) - set(extracted_keys)
        extra = set(extracted_keys) - set(live)
        chk.failures.append(f"{stem}: key mismatch missing={sorted(missing)} extra={sorted(extra)}")
        return

    for record in extracted:
        check_accessor(chk, f"{stem}/{record[0]}", record, enum_tables, live[record[0]])


def main() -> int:
    """Verify the extracted pack data."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=DEFAULT_DATA)
    args = parser.parse_args()

    if not args.data.is_file():
        print(f"error: pack data not found: {args.data}", file=sys.stderr)
        print("run extract_packdata.py first", file=sys.stderr)
        return 2

    with gzip.open(args.data, "rb") as handle:
        data = json.loads(handle.read().decode("utf-8"))

    enum_tables = data["enumTables"]
    chk = Checker()

    for stem, entry in data["packs"].items():
        module = importlib.import_module(f"geckolib.driver.packs.{stem}")
        live = module.GeckoPack(None)
        chk.eq(stem, "name", entry["name"], live.name)
        chk.eq(stem, "plateformType", entry["plateformType"], live.plateform_type)
        chk.eq(stem, "plateformSegment", entry["plateformSegment"], live.plateform_segment)
        chk.eq(stem, "revision", entry["revision"], live.revision)
        chk.modules += 1

    for stem, entry in data["configs"].items():
        module = importlib.import_module(f"geckolib.driver.packs.{stem}")
        live_cls = module.GeckoConfigStruct
        chk.eq(stem, "version", entry["version"], live_cls(None).version)
        chk.eq(stem, "outputKeys", entry["outputKeys"], live_cls(None).output_keys)
        check_table(chk, stem, entry, enum_tables, live_cls)
        chk.modules += 1

    for stem, entry in data["logs"].items():
        module = importlib.import_module(f"geckolib.driver.packs.{stem}")
        live_cls = module.GeckoLogStruct
        live = live_cls(None)
        chk.eq(stem, "version", entry["version"], live.version)
        chk.eq(stem, "begin", entry["begin"], live.begin)
        chk.eq(stem, "end", entry["end"], live.end)
        chk.eq(stem, "outputKeys", entry["outputKeys"], live.output_keys)
        chk.eq(stem, "allDeviceKeys", entry["allDeviceKeys"], live.all_device_keys)
        chk.eq(stem, "userDemandKeys", entry["userDemandKeys"], live.user_demand_keys)
        chk.eq(stem, "errorKeys", entry["errorKeys"], live.error_keys)
        check_table(chk, stem, entry, enum_tables, live_cls)
        chk.modules += 1

    print(f"modules compared   : {chk.modules}")
    print(f"accessors compared : {chk.accessors}")

    if chk.failures:
        print(f"MISMATCHES         : {len(chk.failures)}", file=sys.stderr)
        for failure in chk.failures[:50]:
            print(f"  {failure}", file=sys.stderr)
        if len(chk.failures) > 50:
            print(f"  ... and {len(chk.failures) - 50} more", file=sys.stderr)
        return 1

    print("mismatches         : 0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
