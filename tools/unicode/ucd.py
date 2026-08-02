#!/usr/bin/env python3
"""Shared tooling for the generators reading the Unicode Character Database.

The UCD files all have the same shape:

    0370..0373    ; Greek # L&  [4] GREEK CAPITAL LETTER HETA..

that is, a range, a value, a comment. All that differs is which file is read and
which value is looked for - which is why the reading sits here and not twice
next to each other.
"""

import re
import urllib.request
from pathlib import Path

LICENSE_HEADER = """/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Ratatoskr <https://www.github.com/Vanaheimr/Ratatoskr>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
"""

LINE = re.compile(r"^([0-9A-F]{4,6})(?:\.\.([0-9A-F]{4,6}))?\s*;\s*([^#]+?)\s*$")


def load(url, source=None):
    """The lines of a UCD file - from the net or from a local copy."""

    if source:
        return Path(source).read_text(encoding="utf-8").splitlines()

    with urllib.request.urlopen(url) as response:
        return response.read().decode("utf-8").splitlines()


def ranges(lines, value):
    """The ranges carrying this value, sorted ascending and merged.

    Lines with '@missing' describe default values for unassigned code points and
    stay out of it: what is unassigned does not occur in a JID anyway - the
    ladders from RFC 8264 and RFC 5892 reject it beforehand.
    """

    found = []

    for line in lines:

        line = line.split("#", 1)[0].strip()

        if not line or line.startswith("@"):
            continue

        match = LINE.match(line)

        if not match:
            raise SystemExit(f"unintelligible line {line!r}")

        if match.group(3) != value:
            continue

        first = int(match.group(1), 16)
        last  = int(match.group(2) or match.group(1), 16)

        found.append((first, last))

    if not found:
        raise SystemExit(f"value {value!r}: not a single range found")

    found.sort()

    merged = []

    for first, last in found:
        if merged and first <= merged[-1][1] + 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], last))
        else:
            merged.append((first, last))

    return merged


def emit(field, comment, values, visibility="internal"):
    """One table as a flat sequence of range bounds."""

    out = [f"    /// <summary>{comment} ({len(values)} ranges).</summary>",
           f"    {visibility} static readonly UInt32[] {field} = ["]

    line = "       "

    for first, last in values:
        piece = f" 0x{first:04X}, 0x{last:04X},"
        if len(line) + len(piece) > 96:
            out.append(line)
            line = "       "
        line += piece

    out.append(line)
    out.append("    ];")
    out.append("")

    return out


def write(target, text):
    """Write the file - with BOM and CRLF, like the rest of the project."""

    target.write_bytes(b"\xef\xbb\xbf" + text.replace("\n", "\r\n").encode("utf-8"))

    print(f"{target} written")
