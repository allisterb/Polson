"""Builds the studio's place list from Natural Earth's populated places, for the audience picker.

    py -3 -I tools/build_places.py reference/data/ne_10m_populated_places_simple.zip src/studio/static/places.json

Natural Earth is public domain ("No permission is needed to use Natural Earth"; names from Wikidata, CC0), and is
ledgered in reference/README.md. The zip is read here, never unpacked into the repository: the table is a dBase file,
read by a small reader that checks every size against the file before using it, so a malformed file is an error rather
than a list missing half its places.

Each place is "City, Country", or "City, Region, Country" where two places share a name in one country (Springfield).
Countries are places too: an audience can be "Japan". Largest first, so a short prefix lists the likely place first.
"""

from __future__ import annotations

import io
import json
import struct
import sys
import unicodedata
import zipfile
from collections import Counter
from pathlib import Path

TABLE = "ne_10m_populated_places_simple.dbf"


def read_dbf(data: bytes) -> list[dict]:
    """The table's live rows, as dicts. Raises ValueError on a file whose header and length disagree."""
    if len(data) < 32:
        raise ValueError("too short for a dBase header")
    count, header_len, record_len = struct.unpack_from("<IHH", data, 4)
    fields, at = [], 32
    while at < header_len - 1 and data[at] != 0x0D:
        fields.append((data[at:at + 11].split(b"\0", 1)[0].decode("ascii"), chr(data[at + 11]), data[at + 16]))
        at += 32
    if sum(f[2] for f in fields) + 1 != record_len or header_len + count * record_len > len(data):
        raise ValueError("the table's header does not match its length")
    rows = []
    for i in range(count):
        start = header_len + i * record_len
        if data[start] == 0x2A:                                     # deleted
            continue
        row, pos = {}, start + 1
        for name, kind, length in fields:
            raw = data[pos:pos + length].decode("utf-8").strip()
            pos += length
            # Asterisks are dBase's overflow mark: a value too wide for its column, read as missing.
            row[name] = (None if not raw or raw == "-" or set(raw) == {"*"} else float(raw)) if kind in "NF" else raw
        rows.append(row)
    return rows


def clean(text: str) -> str:
    """A name as shown and as matched: NFC, with no invisible format characters (Unicode category Cf). Natural Earth
    5.1.2 carries a left-to-right mark after both Granadas, so "Granada, Spain" as typed never equalled the name kept."""
    return " ".join("".join(ch for ch in unicodedata.normalize("NFC", text or "") if unicodedata.category(ch) != "Cf").split())


def build(rows: list[dict]) -> dict:
    rows = [{**r, **{k: clean(r[k]) for k in ("name", "nameascii", "adm0name", "adm1name")}} for r in rows]
    places = [r for r in rows if r["name"] and r["adm0name"]]
    shared = Counter((r["name"], r["adm0name"]) for r in places)
    out, seen = [], set()
    for r in sorted(places, key=lambda r: -(r["pop_max"] or 0)):
        parts = [r["name"]] + ([r["adm1name"]] if shared[(r["name"], r["adm0name"])] > 1 and r["adm1name"] else []) + [r["adm0name"]]
        label = ", ".join(parts)
        if label in seen:                                           # the same place twice: keep the larger
            continue
        seen.add(label)
        ascii_name = r["nameascii"] if r["nameascii"] and r["nameascii"] != r["name"] else ""
        out.append([label, ascii_name, int(r["pop_max"] or 0), r["iso_a2"] if r["iso_a2"] not in ("", "-99") else ""])
    countries = sorted({(r["adm0name"], r["iso_a2"] if r["iso_a2"] not in ("", "-99") else "") for r in places})
    return {"source": "Natural Earth 1:10m populated places (simple), public domain", "places": out,
            "countries": [list(c) for c in countries]}


def main(zip_path: str, out_path: str) -> None:
    with zipfile.ZipFile(zip_path) as z:
        rows = read_dbf(z.read(TABLE))
    made = build(rows)
    Path(out_path).write_text(json.dumps(made, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"{len(made['places'])} places, {len(made['countries'])} countries -> {out_path} "
          f"({Path(out_path).stat().st_size:,} bytes)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
