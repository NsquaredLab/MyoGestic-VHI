#!/usr/bin/env python3
"""
Strip empty "Parameters" sections from DefaultDocumentation output.

DefaultDocumentation emits a "#### Parameters" subsection for every method that
has parameters, even when the source has no `<param>` doc tags. The result is
a heading followed by lines like ``` `delta` [System.Double](...) ``` - which
just restates the signature one line up and adds noise.

This pass keeps the Parameters section only when it actually carries prose
description for at least one parameter. Run it after DefaultDocumentation but
before mkdocs sees the files.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# A Parameters block runs from "#### Parameters" up to the next subsection
# heading (#### / ###), the next top-level heading (##), or end of file.
#
# Critically: DefaultDocumentation emits the *next* method's anchor as
# ``<a name='...'></a>`` on the line immediately before its ``## Method``
# heading. The lookahead must stop *at* that anchor (not include it) so we
# don't accidentally strip the next method's in-page link target along with
# the empty Parameters section.
PARAM_BLOCK_RE = re.compile(
    r"^#### Parameters\n(.*?)(?=^<a name='[^']+'></a>\n+## |^#### |^### |^## |\Z)",
    re.MULTILINE | re.DOTALL,
)


def has_description(block: str) -> bool:
    """True iff the block contains at least one line of prose description.

    Treats these as non-prose: blank lines, anchor lines (``<a name=...>``),
    the ``` `name` [Type](...) ``` signature line (starts with a backtick),
    stray headings, and DefaultDocumentation's ``Implements ...`` metadata
    line (which appears under inherited overrides and is *not* a parameter
    description).
    """
    for raw in block.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("<a name="):
            continue
        if line.startswith("`"):
            continue
        if line.startswith("#"):
            continue
        if line.startswith("Implements "):
            continue
        return True
    return False


def strip(text: str) -> tuple[str, int]:
    removed = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal removed
        block = match.group(1)
        if has_description(block):
            return match.group(0)
        removed += 1
        return ""

    return PARAM_BLOCK_RE.sub(replace, text), removed


def main(argv: list[str]) -> int:
    total = 0
    for arg in argv:
        path = Path(arg)
        original = path.read_text()
        new, removed = strip(original)
        if removed:
            path.write_text(new)
            total += removed
    print(f"  stripped {total} empty Parameters section(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
