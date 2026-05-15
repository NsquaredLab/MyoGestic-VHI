#!/usr/bin/env python3
"""
Strip Godot lifecycle method sections from DefaultDocumentation output.

The Node-derived classes inherit ``_Ready``, ``_Process``, ``_PhysicsProcess``,
``_ExitTree`` and friends from Godot. They are public (so DefaultDocumentation
emits a page section for each one) but they are framework callbacks - not
VHI's API surface. The class-level summary already describes what happens in
``_Ready`` / ``_Process`` for nodes where it matters; the individual entries
just add noise.

This pass removes those sections (anchor + heading + signature + any trailing
subsections) for the lifecycle methods in :data:`LIFECYCLE`.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Godot Node lifecycle callbacks that VHI nodes may override.
LIFECYCLE = {
    "_Ready",
    "_Process",
    "_PhysicsProcess",
    "_ExitTree",
    "_EnterTree",
    "_Notification",
    "_Input",
    "_UnhandledInput",
    "_UnhandledKeyInput",
}

# Match an entire method section keyed off its anchor. The anchor sits one
# line before the level-2 heading; capturing from the anchor through to the
# next anchor / next level-3 section header / EOF takes the whole entry.
SECTION_RE = re.compile(
    r"^<a name='[^']*\.(_\w+)\([^']*\)'></a>\n.*?(?=^<a name='|^### |\Z)",
    re.MULTILINE | re.DOTALL,
)

# Collapse runs of 3+ newlines back down to a single blank line (so the
# stripped output reads cleanly without yawning gaps).
MULTI_BLANK_RE = re.compile(r"\n{3,}")


def strip(text: str) -> tuple[str, int]:
    removed = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal removed
        method = match.group(1)
        if method in LIFECYCLE:
            removed += 1
            return ""
        return match.group(0)

    new_text = SECTION_RE.sub(replace, text)
    if removed:
        new_text = MULTI_BLANK_RE.sub("\n\n", new_text)
    return new_text, removed


def main(argv: list[str]) -> int:
    total = 0
    for arg in argv:
        path = Path(arg)
        original = path.read_text()
        new, removed = strip(original)
        if removed:
            path.write_text(new)
            total += removed
    print(f"  stripped {total} Godot lifecycle method section(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
