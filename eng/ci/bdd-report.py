#!/usr/bin/env python3
"""Extract the TinyBDD scenario output from TRX files into one readable markdown report.

Usage: bdd-report.py <glob-of-trx> <output.md> [summary-file]

TinyBDD writes "Feature: ... / Scenario: ... / Given ... [OK] 12 ms" lines to the test output, which the TRX logger keeps
in each result's StdOut. This groups those blocks by feature so a reviewer can read the scenarios (and the failing step)
without opening a TRX. Exits 0 even when no scenario output is found (a warning is printed), so it never fails a build;
the test run itself is the gate. Pass a third argument to also append the report to that file (the GitHub job summary).
"""
import glob
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def collect(files: list[str]) -> dict[str, list[tuple[str, str, str]]]:
    """feature -> [(scenario, outcome, step text)]"""
    features: dict[str, list[tuple[str, str, str]]] = {}
    for path in files:
        for result in ET.parse(path).getroot().iter("{%s}UnitTestResult" % NS["t"]):
            out = result.find("t:Output/t:StdOut", NS)
            if out is None or not (out.text or "").lstrip().startswith("Feature:"):
                continue
            lines = [ln.rstrip() for ln in out.text.splitlines() if ln.strip()]
            feature = lines[0].removeprefix("Feature:").strip()
            scenario = next((ln.removeprefix("Scenario:").strip() for ln in lines if ln.startswith("Scenario:")), result.get("testName", ""))
            steps = "\n".join(ln for ln in lines if not ln.startswith(("Feature:", "Scenario:")))
            features.setdefault(feature, []).append((scenario, result.get("outcome", "Unknown"), steps))
    return features


def render(features: dict[str, list[tuple[str, str, str]]]) -> str:
    total = sum(len(v) for v in features.values())
    failed = sum(1 for v in features.values() for _, outcome, _ in v if outcome != "Passed")
    out = ["## BDD scenarios", "", f"{total} scenario(s) in {len(features)} feature(s); {failed} not passed.", ""]
    for feature in sorted(features):
        out += [f"### {feature}", ""]
        for scenario, outcome, steps in sorted(features[feature]):
            out += [f"**{scenario}**: {outcome}", "", "```text", steps, "```", ""]
    return "\n".join(out)


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    files = glob.glob(sys.argv[1], recursive=True)
    features = collect(files)
    if not features:
        print(f"::warning::No TinyBDD scenario output found in {len(files)} TRX file(s)")
    text = render(features)
    with open(sys.argv[2], "w", encoding="utf-8") as fh:
        fh.write(text)
    if len(sys.argv) > 3:
        with open(sys.argv[3], "a", encoding="utf-8") as fh:
            fh.write(text + "\n")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
