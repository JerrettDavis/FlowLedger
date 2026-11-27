#!/usr/bin/env python3
"""Lists system invariants (docs/architecture/invariants.md) that no test claims via
[Trait("Invariant", "INV-xx")]. Fails when an invariant outside KNOWN_GAPS has no test, so the
mapping cannot silently rot. Shrink KNOWN_GAPS as coverage lands; never grow it without an issue.
"""
import pathlib
import re
import sys

KNOWN_GAPS: set[str] = set()  # invariants with no test yet; never grow this without an issue

root = pathlib.Path(__file__).resolve().parent.parent
doc = (root / "docs" / "architecture" / "invariants.md").read_text(encoding="utf-8")
declared = sorted(set(re.findall(r"\bINV-\d{2}\b", doc)))

tagged = set()
for f in (root / "tests").rglob("*.cs"):
    if "obj" in f.parts or "bin" in f.parts:
        continue
    tagged.update(re.findall(r'Trait\("Invariant",\s*"(INV-\d{2})"\)', f.read_text(encoding="utf-8-sig")))

missing = [i for i in declared if i not in tagged]
for i in missing:
    print(f"{i}: no test tagged" + (" (known gap)" if i in KNOWN_GAPS else ""))
unexpected = [i for i in missing if i not in KNOWN_GAPS]
print(f"{len(declared) - len(missing)}/{len(declared)} invariants have at least one tagged test")
sys.exit(1 if unexpected else 0)
