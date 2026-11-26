#!/usr/bin/env python3
"""Fail when a TRX run has unexpected skipped tests or too few executed tests.

Usage: check-trx.py <glob-of-trx> [--allow-skip REGEX] [--min-executed N]

A skipped test (outcome NotExecuted) is tolerated only when its name matches
--allow-skip (for example the live-sandbox tests that need real credentials).
This stops Docker-gated suites from silently turning into skips in CI.
"""
import argparse
import glob
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("pattern")
    ap.add_argument("--allow-skip", default=None)
    ap.add_argument("--min-executed", type=int, default=1)
    args = ap.parse_args()

    allow = re.compile(args.allow_skip) if args.allow_skip else None
    files = glob.glob(args.pattern, recursive=True)
    if not files:
        print(f"::error::No TRX files match {args.pattern}")
        return 1

    executed = 0
    unexpected = []
    allowed = 0
    for path in files:
        for r in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", NS):
            outcome = r.get("outcome")
            name = r.get("testName", "")
            if outcome == "NotExecuted":
                if allow and allow.search(name):
                    allowed += 1
                else:
                    unexpected.append(f"{name} ({path})")
            else:
                executed += 1

    print(f"executed={executed} skipped-allowed={allowed} skipped-unexpected={len(unexpected)}")
    rc = 0
    if unexpected:
        for u in unexpected[:50]:
            print(f"::error::Unexpected skipped test: {u}")
        if len(unexpected) > 50:
            print(f"::error::... and {len(unexpected) - 50} more skipped tests")
        rc = 1
    if executed < args.min_executed:
        print(f"::error::Only {executed} tests executed; expected at least {args.min_executed}")
        rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
