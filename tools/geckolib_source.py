"""
Find the geckolib checkout that these tools read from.

The tools here check this port against the Python original, so they need its source
tree - the generated pack modules to extract from, and the captured snapshots to
compare against. Where that checkout lives is a property of the machine rather than
of this repository, so it is looked up instead of hard coded:

1. the tool's own argument, where it has one (``--packs``, ``--snapshots``)
2. the ``GECKOLIB_ROOT`` environment variable
3. a checkout next to this one, ``../geckolib`` or ``../github/geckolib``
"""

from __future__ import annotations

import os
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

ENV_VAR = "GECKOLIB_ROOT"


def _is_geckolib(path: Path) -> bool:
    """Does this look like a geckolib checkout rather than any old directory?"""
    return (path / "src" / "geckolib" / "__init__.py").is_file()


def find_geckolib(explicit: Path | str | None = None) -> Path:
    """
    Locate the geckolib checkout, or exit explaining how to say where it is.

    Call this after parsing arguments, not at import time: a tool given an explicit
    path should not need geckolib to be findable at all.
    """
    candidates: list[Path] = []

    if explicit is not None:
        candidates.append(Path(explicit))

    from_env = os.environ.get(ENV_VAR)
    if from_env:
        candidates.append(Path(from_env))

    candidates.append(REPO_ROOT.parent / "geckolib")
    candidates.append(REPO_ROOT.parent / "github" / "geckolib")

    for candidate in candidates:
        if _is_geckolib(candidate):
            return candidate.resolve()

    looked_in = "\n".join(f"  {candidate}" for candidate in candidates)
    msg = (
        "Cannot find a geckolib checkout. Looked in:\n"
        f"{looked_in}\n"
        f"Set {ENV_VAR} to the root of your geckolib clone, or pass the path on the "
        "command line."
    )
    raise SystemExit(msg)


def packs_dir(explicit: Path | str | None = None) -> Path:
    """The directory holding geckolib's generated spa pack modules."""
    return find_geckolib(explicit) / "src" / "geckolib" / "driver" / "packs"


def snapshots_dir(explicit: Path | str | None = None) -> Path:
    """The directory holding geckolib's captured spa snapshots."""
    return find_geckolib(explicit) / "tests" / "snapshots"


def source_dir(explicit: Path | str | None = None) -> Path:
    """geckolib's ``src`` directory, which is where ``python -m geckolib`` is run."""
    return find_geckolib(explicit) / "src"
