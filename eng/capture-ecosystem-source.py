"""Fingerprint candidate inputs without including ignored build outputs or credentials."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent.parent


def git(*arguments: str) -> bytes:
    return subprocess.run(["git", *arguments], cwd=ROOT, check=True, capture_output=True).stdout


def capture() -> dict:
    names = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0")) - {b""})
    tree = hashlib.sha256()
    count = total = 0
    for encoded_name in names:
        name = encoded_name.decode("utf-8")
        path = ROOT / name
        resolved = path.resolve()
        if not resolved.is_relative_to(ROOT):
            raise ValueError("A candidate input resolves outside the checkout.")
        if not path.is_file():
            # A deletion affects the fingerprint through the omitted tracked path.
            continue
        size = path.stat().st_size
        if size > 128 * 1024 * 1024:
            raise ValueError("A candidate input exceeds the source-file byte limit.")
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                digest.update(block)
        tree.update(len(encoded_name).to_bytes(8, "big"))
        tree.update(encoded_name)
        tree.update(size.to_bytes(8, "big"))
        tree.update(digest.digest())
        count += 1
        total += size
    return {
        "formatVersion": 1,
        "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
        "commit": git("rev-parse", "HEAD").decode("ascii").strip(),
        "dirty": bool(git("status", "--porcelain", "--untracked-files=normal")),
        "sourceTreeSha256": tree.hexdigest(),
        "fileCount": count,
        "sourceBytes": total,
        "productionQualified": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    output = Path(options.output).resolve()
    if not output.is_relative_to(ROOT):
        raise ValueError("Evidence output must remain inside the checkout.")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(capture(), indent=2) + "\n", encoding="utf-8", newline="\n")
    print("Captured candidate source fingerprint; no build or production qualification inferred.")


if __name__ == "__main__":
    main()
