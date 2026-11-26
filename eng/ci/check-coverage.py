#!/usr/bin/env python3
"""Coverage floor gate.

Merges Cobertura reports and checks line coverage per assembly against the
floors in eng/ci/floors-lines.json. Exits 1 when any floor is breached.
Usage: check-coverage.py <floors.json> <glob-of-cobertura-files> [summary-file]
"""
import glob
import json
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    floors = json.load(open(sys.argv[1], encoding="utf-8"))
    files = glob.glob(sys.argv[2], recursive=True)
    summary = sys.argv[3] if len(sys.argv) > 3 else None
    if not files:
        print("::error::No Cobertura files found for coverage gate")
        return 1

    # assembly -> filename -> {line: hits}; merge across reports by max hits.
    data: dict[str, dict[str, dict[int, int]]] = {}
    for path in files:
        for pkg in ET.parse(path).getroot().iter("package"):
            name = pkg.get("name", "")
            for cls in pkg.iter("class"):
                lines = data.setdefault(name, {}).setdefault(cls.get("filename", ""), {})
                for ln in cls.iter("line"):
                    n = int(ln.get("number"))
                    lines[n] = max(lines.get(n, 0), int(ln.get("hits")))

    rows = ["| Assembly | Line coverage | Floor | Status |", "|---|---|---|---|"]
    failed = False
    for assembly, floor in floors["lineFloors"].items():
        files_ = data.get(assembly)
        if not files_:
            rows.append(f"| {assembly} | n/a | {floor}% | MISSING |")
            failed = True
            continue
        total = sum(len(v) for v in files_.values())
        hit = sum(1 for v in files_.values() for h in v.values() if h > 0)
        pct = 100.0 * hit / total if total else 100.0
        ok = pct >= floor
        failed |= not ok
        rows.append(f"| {assembly} | {pct:.1f}% | {floor}% | {'ok' if ok else 'BELOW FLOOR'} |")

    text = "\n".join(["### Coverage floors", ""] + rows) + "\n"
    print(text)
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write(text)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
