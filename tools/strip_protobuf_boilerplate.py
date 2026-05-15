#!/usr/bin/env python3
"""
Strip protobuf-generated boilerplate from the ``Myogestic.Vhi.V1.*`` pages.

``Grpc.Tools`` emits a fixed set of plumbing members on every generated
message class - serialization (``WriteTo`` / ``MergeFrom`` /
``CalculateSize``), value-equality (``Equals`` / ``GetHashCode``), cloning
(``Clone``), debug printing (``ToString``), the static ``Parser`` and
``Descriptor`` properties, the ``<FieldName>FieldNumber`` constants, and
the default / copy constructors. None of them are interesting to a reader of
the API reference - the actual message fields (``MovementName``, ``Cycle``,
``Mode``, ...) are. This pass drops the noise and leaves the user-facing
fields intact.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Method / property / field names that are protobuf plumbing on every message.
PLUMBING = {
    "WriteTo", "MergeFrom", "CalculateSize",
    "Equals", "GetHashCode", "Clone", "ToString",
    "Parser", "Descriptor",
}


# Match a section keyed off its anchor in the Myogestic.Vhi.V1 namespace.
# The anchor pattern is:
#   <a name='Myogestic.Vhi.V1.<ClassName>.<MemberName>(<args>)?'></a>
# - methods have (args), properties / fields don't, and parameter anchors
#   would have a trailing .<paramName> which we explicitly exclude.
SECTION_RE = re.compile(
    r"^<a name='Myogestic\.Vhi\.V1\.(\w+)\.(\w+)(?:\([^']*\))?'></a>\n.*?(?=^<a name='|^### |\Z)",
    re.MULTILINE | re.DOTALL,
)

MULTI_BLANK_RE = re.compile(r"\n{3,}")

# Whole "### Explicit Interface Implementations" section - protobuf message
# classes carry IBufferMessage / IMessage / IDeepCloneable implementations
# the user should not care about. Drop the section entirely.
EXPLICIT_IMPL_SECTION_RE = re.compile(
    r"^### Explicit Interface Implementations\n.*?(?=^### |\Z)",
    re.MULTILINE | re.DOTALL,
)

ANCHOR_LINE_RE = re.compile(r"^<a name='([^']+)'></a>$")
# A *parameter* anchor's name is the method-signature anchor + ``.paramname`` -
# e.g. ``Foo.Method(arg1, arg2).paramname``. The closing ``)`` followed by a
# dotted identifier at end-of-string is the giveaway. Other anchors (class,
# method, property, field, *enum value*) don't have that shape.
PARAM_ANCHOR_NAME_RE = re.compile(r"\)\.[A-Za-z_]\w*$")


def strip_orphan_anchors(text: str) -> tuple[str, int]:
    r"""Remove parameter anchors left behind when their parent method was
    stripped. Enum-value anchors (e.g. ``ControlMode.Movement``) are *not*
    parameter anchors and stay - DefaultDocumentation renders them as
    ``<anchor>`` + ``\`Name\` 0`` + description, without a ``## `` heading.
    """
    lines = text.split("\n")
    out: list[str] = []
    removed = 0
    i = 0
    while i < len(lines):
        line = lines[i]
        match = ANCHOR_LINE_RE.match(line)
        if match and PARAM_ANCHOR_NAME_RE.search(match.group(1)):
            # Skip this anchor and every following line up to the next
            # anchor / level-2/3 heading.
            i += 1
            while i < len(lines):
                cur = lines[i]
                if ANCHOR_LINE_RE.match(cur):
                    break
                if cur.startswith("## ") or cur.startswith("### "):
                    break
                i += 1
            removed += 1
            continue
        out.append(line)
        i += 1
    return "\n".join(out), removed


def should_strip(class_name: str, member_name: str) -> bool:
    """Return True iff a member is protobuf-generated boilerplate."""
    if member_name in PLUMBING:
        return True
    # Constructor on a message class is generated.
    if member_name == class_name:
        return True
    # `<FieldName>FieldNumber` constants are generated per field.
    if member_name.endswith("FieldNumber"):
        return True
    return False


def strip(text: str) -> tuple[str, int]:
    removed = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal removed
        if should_strip(match.group(1), match.group(2)):
            removed += 1
            return ""
        return match.group(0)

    new_text = SECTION_RE.sub(replace, text)

    # Drop the entire Explicit Interface Implementations section if present.
    if EXPLICIT_IMPL_SECTION_RE.search(new_text):
        new_text = EXPLICIT_IMPL_SECTION_RE.sub("", new_text)
        removed += 1

    # Sweep up orphan param anchors left behind when their parent method was
    # removed.
    new_text, orphan_count = strip_orphan_anchors(new_text)
    removed += orphan_count

    # Also strip the now-empty "### Methods" / "### Properties" / "### Fields"
    # / "### Constructors" headings that protobuf classes are left with after
    # all their members have been removed. An empty section is just the
    # heading followed by another heading or EOF.
    new_text = re.sub(
        r"^### (Methods|Properties|Fields|Constructors|Explicit Interface Implementations)\n+(?=^## |^### |\Z)",
        "",
        new_text,
        flags=re.MULTILINE,
    )

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
    print(f"  stripped {total} protobuf-boilerplate section(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
