#!/usr/bin/env python3
"""Coverage floor gate, with a delta-vs-baseline report for pull requests.

Merges Cobertura reports and checks line coverage per assembly against the
floors in eng/ci/floors-lines.json. Exits 1 when any floor is breached, or when
coverage has dropped more than `ratchetTolerance` points below the recorded
`baseline` (so a PR cannot quietly give back the coverage the floors do not yet
protect).

Usage: check-coverage.py <floors.json> <glob-of-cobertura-files> [summary-file] [--comment-file PATH]

  summary-file     markdown is appended here (the GitHub job summary)
  --comment-file   markdown written here for a sticky PR comment (see ci.yml)

Ratchet: when coverage improves, raise `baseline` (and, once it is stable, the floor)
in eng/ci/floors-lines.json. The report prints a hint when coverage is well above the floor.
"""
import argparse
import glob
import json
import sys
import xml.etree.ElementTree as ET

COMMENT_MARKER = "<!-- flowledger-coverage-delta -->"
RAISE_HINT_MARGIN = 5.0


def source_key(sources: list[str], filename: str) -> str:
    """Repo-relative key for a source file. Coverlet writes `filename` relative to the report's own <source> root, and that root differs
    between test projects (the repo root in one report, its `src/` folder in another), so the same file used to be counted twice under two keys
    and the merged percentage came out far below any single report. Resolving against the root makes every report agree."""
    for source in sources:
        full = (source.replace("\\", "/").rstrip("/") + "/" + filename.replace("\\", "/")).replace("//", "/")
        cut = full.find("/src/")
        if cut >= 0:
            return full[cut + 1:]
    return filename.replace("\\", "/")


def measure(files: list[str]) -> dict[str, tuple[int, int]]:
    """assembly -> (lines hit, lines total); reports are merged per file/line by max hits."""
    data: dict[str, dict[str, dict[int, int]]] = {}
    for path in files:
        root = ET.parse(path).getroot()
        sources = [s.text or "" for s in root.iter("source")]
        for pkg in root.iter("package"):
            name = pkg.get("name", "")
            for cls in pkg.iter("class"):
                lines = data.setdefault(name, {}).setdefault(source_key(sources, cls.get("filename", "")), {})
                for ln in cls.iter("line"):
                    n = int(ln.get("number"))
                    lines[n] = max(lines.get(n, 0), int(ln.get("hits")))
    return {
        asm: (sum(1 for v in files_.values() for h in v.values() if h > 0), sum(len(v) for v in files_.values()))
        for asm, files_ in data.items()
    }


def evaluate(floors: dict, measured: dict[str, tuple[int, int]]) -> tuple[str, bool]:
    baseline = floors.get("baseline", {})
    tolerance = float(floors.get("ratchetTolerance", 1.0))
    rows = [
        "| Assembly | Line coverage | Baseline | Delta | Floor | Status |",
        "|---|---|---|---|---|---|",
    ]
    hints = []
    failed = False
    for assembly, floor in floors["lineFloors"].items():
        counted = measured.get(assembly)
        if not counted:
            rows.append(f"| {assembly} | n/a | n/a | n/a | {floor}% | MISSING |")
            failed = True
            continue
        hit, total = counted
        pct = 100.0 * hit / total if total else 100.0
        base = baseline.get(assembly)
        delta = f"{pct - base:+.1f}" if base is not None else "n/a"
        status = "ok"
        if pct < floor:
            status = "BELOW FLOOR"
        elif base is not None and pct < base - tolerance:
            status = f"DROPPED MORE THAN {tolerance:g} POINT(S) BELOW BASELINE"
        failed |= status != "ok"
        base_text = f"{base:.1f}%" if base is not None else "n/a"
        rows.append(f"| {assembly} | {pct:.1f}% | {base_text} | {delta} | {floor}% | {status} |")
        if pct >= floor + RAISE_HINT_MARGIN:
            hints.append(f"`{assembly}` is {pct - floor:.1f} points above its floor: consider raising the floor in eng/ci/floors-lines.json.")
        if base is not None and pct > base + tolerance:
            hints.append(f"`{assembly}` improved on its baseline: record {pct:.1f} as the new baseline.")

    text = "\n".join(["### Coverage floors", ""] + rows) + "\n"
    if hints:
        text += "\n" + "\n".join(f"- {h}" for h in hints) + "\n"
    return text, failed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("floors")
    ap.add_argument("pattern")
    ap.add_argument("summary", nargs="?")
    ap.add_argument("--comment-file")
    args = ap.parse_args()

    with open(args.floors, encoding="utf-8") as fh:
        floors = json.load(fh)
    files = glob.glob(args.pattern, recursive=True)
    if not files:
        print("::error::No Cobertura files found for coverage gate")
        return 1

    text, failed = evaluate(floors, measure(files))
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as fh:
            fh.write(text)
    if args.comment_file:
        with open(args.comment_file, "w", encoding="utf-8") as fh:
            fh.write(COMMENT_MARKER + "\n" + text)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
