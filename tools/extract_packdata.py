"""
Extract the Gecko spa-pack definitions from geckolib's generated Python modules.

geckolib generates 187 modules under `geckolib/driver/packs/` from Gecko's
`SpaPackStruct_<rev>.xml`. That XML is gitignored and absent from the repository, and
no URL for it is recorded anywhere, so the generated Python is the only surviving copy
of the definitions.

Those modules are pure declarative data - no logic, no conditionals, no state - so they
can be parsed with `ast` and re-emitted as a single compact data file for the C# port to
read at runtime, instead of transliterating 190k lines of generated source.

Usage:
    python extract_packdata.py [--packs DIR] [--out FILE] [--summary FILE]
"""

from __future__ import annotations

import argparse
import ast
import gzip
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any

import geckolib_source

DEFAULT_OUT = Path(__file__).parent.parent / "GeckoLib.NET" / "Packs" / "packdata.json.gz"
DEFAULT_SUMMARY = Path(__file__).parent.parent / "GeckoLib.NET" / "Packs" / "packdata.summary.txt"

# The accessor constructors take DIFFERENT argument lists, so we have to dispatch on the
# class name. Reading them positionally would quietly store `rw` in `bitpos` for the Bool
# and Enum accessors. Signatures verified against geckolib/driver/accessor.py:267-372;
# the leading `self.struct` argument is dropped before these names are applied.
ACCESSOR_SIGNATURES: dict[str, tuple[str, tuple[str, ...]]] = {
    "GeckoByteStructAccessor": ("Byte", ("path", "pos", "rw")),
    "GeckoWordStructAccessor": ("Word", ("path", "pos", "rw")),
    "GeckoTimeStructAccessor": ("Time", ("path", "pos", "rw")),
    "GeckoTempStructAccessor": ("Temp", ("path", "pos", "rw")),
    "GeckoBoolStructAccessor": ("Bool", ("path", "pos", "bitpos", "rw")),
    "GeckoEnumStructAccessor": (
        "Enum",
        ("path", "pos", "bitpos", "items", "size", "maxitems", "rw"),
    ),
    # The generic base is never constructed by the current generator, but if a future
    # SpaPackStruct revision introduces a type packgen doesn't special-case it would show
    # up here. Handled so it fails loudly rather than being skipped.
    "GeckoStructAccessor": (
        None,
        ("path", "pos", "type", "bitpos", "items", "size", "maxitems", "rw"),
    ),
}

# Property name -> output key, per generated class.
PACK_PROPS = {
    "name": "name",
    "plateform_type": "plateformType",
    "plateform_segment": "plateformSegment",
    "revision": "revision",
}
CONFIG_PROPS = {"version": "version", "output_keys": "outputKeys"}
LOG_PROPS = {
    "version": "version",
    "begin": "begin",
    "end": "end",
    "output_keys": "outputKeys",
    "all_device_keys": "allDeviceKeys",
    "user_demand_keys": "userDemandKeys",
    "error_keys": "errorKeys",
}


class ExtractError(Exception):
    """Raised when a generated module does not have the shape we expect."""


def find_class(tree: ast.Module, name: str) -> ast.ClassDef | None:
    """Find a top-level class by name."""
    for node in tree.body:
        if isinstance(node, ast.ClassDef) and node.name == name:
            return node
    return None


def property_returns(cls: ast.ClassDef) -> dict[str, ast.expr]:
    """Map each @property name to the expression it returns."""
    out: dict[str, ast.expr] = {}
    for node in cls.body:
        if not isinstance(node, ast.FunctionDef):
            continue
        if not any(isinstance(d, ast.Name) and d.id == "property" for d in node.decorator_list):
            continue
        returns = [s for s in node.body if isinstance(s, ast.Return) and s.value is not None]
        if len(returns) != 1:
            raise ExtractError(f"property {node.name} has {len(returns)} return statements")
        out[node.name] = returns[0].value
    return out


def literal_props(cls: ast.ClassDef, wanted: dict[str, str], where: str) -> dict[str, Any]:
    """Evaluate the simple literal-returning properties of a generated class."""
    props = property_returns(cls)
    result: dict[str, Any] = {}
    for src, dst in wanted.items():
        if src not in props:
            raise ExtractError(f"{where}: missing property '{src}'")
        try:
            result[dst] = ast.literal_eval(props[src])
        except ValueError as err:
            raise ExtractError(f"{where}: property '{src}' is not a literal") from err
    return result


def extract_accessors(cls: ast.ClassDef, where: str) -> list[dict[str, Any]]:
    """Extract the accessor table from a GeckoConfigStruct/GeckoLogStruct class."""
    props = property_returns(cls)
    if "accessors" not in props:
        raise ExtractError(f"{where}: no 'accessors' property")
    node = props["accessors"]
    if not isinstance(node, ast.Dict):
        raise ExtractError(f"{where}: 'accessors' does not return a dict literal")

    accessors: list[dict[str, Any]] = []
    for key_node, value_node in zip(node.keys, node.values):
        if not isinstance(key_node, ast.Constant) or not isinstance(key_node.value, str):
            raise ExtractError(f"{where}: accessor key is not a string literal")
        key = key_node.value
        if not isinstance(value_node, ast.Call) or not isinstance(value_node.func, ast.Name):
            raise ExtractError(f"{where}: accessor '{key}' is not a direct constructor call")

        class_name = value_node.func.id
        if class_name not in ACCESSOR_SIGNATURES:
            raise ExtractError(f"{where}: accessor '{key}' uses unknown class {class_name}")
        kind, arg_names = ACCESSOR_SIGNATURES[class_name]

        if value_node.keywords:
            raise ExtractError(f"{where}: accessor '{key}' uses keyword arguments")
        # Drop the leading `self.struct` argument.
        args = value_node.args[1:]
        if len(args) != len(arg_names):
            raise ExtractError(
                f"{where}: accessor '{key}' ({class_name}) has {len(args)} arguments,"
                f" expected {len(arg_names)}"
            )

        fields: dict[str, Any] = {}
        for arg_name, arg_node in zip(arg_names, args):
            try:
                fields[arg_name] = ast.literal_eval(arg_node)
            except ValueError as err:
                raise ExtractError(
                    f"{where}: accessor '{key}' argument '{arg_name}' is not a literal"
                ) from err

        if kind is None:
            # Generic base class - the type is an explicit argument.
            kind = fields.pop("type")

        fields["key"] = key
        fields["kind"] = kind
        accessors.append(fields)

    return accessors


def main() -> int:
    """Extract the pack data."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--packs",
        type=Path,
        default=None,
        help="geckolib's generated pack modules. Found automatically if not given.",
    )
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY)
    args = parser.parse_args()

    packs = args.packs or geckolib_source.packs_dir()

    if not packs.is_dir():
        print(f"error: packs directory not found: {packs}", file=sys.stderr)
        return 2

    files = sorted(p for p in packs.glob("*.py") if p.name != "__init__.py")
    if not files:
        print(f"error: no pack modules found in {packs}", file=sys.stderr)
        return 2

    # Enum item lists repeat heavily across packs, so intern them into a shared table.
    enum_tables: list[list[str]] = []
    enum_index: dict[tuple[str, ...], int] = {}

    def intern(items: list[str]) -> int:
        as_tuple = tuple(items)
        if as_tuple not in enum_index:
            enum_index[as_tuple] = len(enum_tables)
            enum_tables.append(items)
        return enum_index[as_tuple]

    packs: dict[str, Any] = {}
    configs: dict[str, Any] = {}
    logs: dict[str, Any] = {}
    kind_counts: Counter[str] = Counter()
    summary_rows: list[tuple[str, str, int]] = []
    revisions: set[str] = set()

    for path in files:
        # The runtime looks modules up by filename stem - "inxe", "inxe-cfg-60",
        # "inxe-log-58" (async_spastruct.py:214-240) - so that is the key we emit.
        stem = path.stem
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))

        pack_cls = find_class(tree, "GeckoPack")
        config_cls = find_class(tree, "GeckoConfigStruct")
        log_cls = find_class(tree, "GeckoLogStruct")

        try:
            if pack_cls is not None:
                entry = literal_props(pack_cls, PACK_PROPS, stem)
                revisions.add(entry["revision"])
                packs[stem] = entry
                summary_rows.append((stem, "pack", 0))

            elif config_cls is not None:
                entry = literal_props(config_cls, CONFIG_PROPS, stem)
                raw = extract_accessors(config_cls, stem)
                entry["accessors"] = [encode(a, intern, kind_counts) for a in raw]
                configs[stem] = entry
                summary_rows.append((stem, "config", len(raw)))

            elif log_cls is not None:
                entry = literal_props(log_cls, LOG_PROPS, stem)
                raw = extract_accessors(log_cls, stem)
                entry["accessors"] = [encode(a, intern, kind_counts) for a in raw]
                logs[stem] = entry
                summary_rows.append((stem, "log", len(raw)))

            else:
                raise ExtractError("no GeckoPack/GeckoConfigStruct/GeckoLogStruct class")

        except ExtractError as err:
            print(f"error: {path.name}: {err}", file=sys.stderr)
            return 1

    if len(revisions) != 1:
        print(f"warning: multiple SpaPackStruct revisions present: {sorted(revisions)}", file=sys.stderr)

    data = {
        "revision": sorted(revisions)[0] if revisions else None,
        "enumTables": enum_tables,
        "packs": packs,
        "configs": configs,
        "logs": logs,
    }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    raw_json = json.dumps(data, separators=(",", ":")).encode("utf-8")
    with gzip.open(args.out, "wb", compresslevel=9) as handle:
        handle.write(raw_json)

    total = sum(kind_counts.values())
    lines = [
        f"SpaPackStruct revision : {data['revision']}",
        f"modules                : {len(files)} ({len(packs)} pack,"
        f" {len(configs)} config, {len(logs)} log)",
        f"accessors              : {total}",
        f"unique enum item-lists : {len(enum_tables)}",
        "",
        "accessors by kind",
        *(f"  {k:<6} {v:>6}" for k, v in sorted(kind_counts.items())),
        "",
        "accessors by module",
        *(f"  {stem:<28} {kind:<7} {count:>5}" for stem, kind, count in summary_rows),
        "",
    ]
    args.summary.parent.mkdir(parents=True, exist_ok=True)
    args.summary.write_text("\n".join(lines), encoding="utf-8")

    print(f"modules   : {len(files)}")
    print(f"accessors : {total}")
    for kind, count in sorted(kind_counts.items()):
        print(f"  {kind:<6} {count:>6}")
    print(f"enum lists: {len(enum_tables)} unique")
    print(f"written   : {args.out} ({len(raw_json) / 1e6:.2f} MB -> {args.out.stat().st_size / 1e3:.0f} KB gzipped)")
    print(f"            {args.summary}")
    return 0


def encode(
    fields: dict[str, Any],
    intern: Any,
    kind_counts: Counter[str],
) -> list[Any]:
    """Encode one accessor as a compact positional record."""
    kind = fields["kind"]
    kind_counts[kind] += 1
    items = fields.get("items")
    if items is not None and not isinstance(items, list):
        # geckolib also accepts a "a|b|c" string (accessor.py:66-70); the generator always
        # emits a list, but normalise just in case a future revision does not.
        items = items.split("|")
    return [
        fields["key"],
        fields["path"],
        kind,
        fields["pos"],
        fields.get("bitpos"),
        intern(items) if items is not None else None,
        fields.get("size"),
        fields.get("maxitems"),
        fields.get("rw"),
    ]


if __name__ == "__main__":
    sys.exit(main())
