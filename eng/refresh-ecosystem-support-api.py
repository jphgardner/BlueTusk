"""Update only the two additive existing-family contracts used by the expansion.

Shipped API hashes and all unrelated candidate hashes remain unchanged.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
targets = (
    ("eng/provider-api-freeze.json", "reviewedAdditions", "src/BlueTusk.Data/PublicAPI.Unshipped.txt"),
    ("eng/live-api-freeze.json", "files", "src/BlueTusk.Live/PublicAPI.Unshipped.txt"),
)
for manifest_name, section, relative in targets:
    path = ROOT / manifest_name
    manifest = json.loads(path.read_text(encoding="utf-8-sig"))
    entries = [entry for entry in manifest[section] if entry["path"] == relative]
    if len(entries) != 1:
        raise ValueError(f"Expected exactly one scoped candidate entry for {relative}")
    content = (ROOT / relative).read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    entries[0]["sha256"] = hashlib.sha256(content.encode("utf-8")).hexdigest()
    path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Updated scoped additive candidate hash: {relative}")
