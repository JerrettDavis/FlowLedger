#!/usr/bin/env python3
"""Coverage floor gate: merges every Cobertura report and fails if the line coverage of a gated
assembly drops below its floor. Floors sit just under the measured baseline so only regressions fail.

usage: check-coverage.py <root-dir> [Assembly=floorPercent ...]
"""
import glob
import sys
import xml.etree.ElementTree as ET

root = sys.argv[1]
floors = {"FlowLedger.Domain": 65.0, "FlowLedger.Application": 70.0}
for arg in sys.argv[2:]:
    name, _, value = arg.partition("=")
    floors[name] = float(value)

hits = {name: {} for name in floors}
for report in glob.glob(f"{root}/**/coverage.cobertura.xml", recursive=True):
    for pkg in ET.parse(report).getroot().iter("package"):
        name = pkg.get("name")
        if name not in hits:
            continue
        for cls in pkg.iter("class"):
            fname = cls.get("filename")
            for line in cls.iter("line"):
                key = (fname, int(line.get("number")))
                hits[name][key] = max(hits[name].get(key, 0), int(line.get("hits")))

failed = False
for name, floor in floors.items():
    lines = hits[name]
    if not lines:
        print(f"::error::no coverage data for {name}")
        failed = True
        continue
    pct = 100.0 * sum(1 for h in lines.values() if h > 0) / len(lines)
    status = "ok" if pct >= floor else "BELOW FLOOR"
    print(f"{name}: {pct:.1f}% line coverage (floor {floor:.1f}%) {status}")
    failed |= pct < floor
sys.exit(1 if failed else 0)
