#!/usr/bin/env python3
"""Add or update one AutoSubs release in the Jellyfin plugin catalog manifest."""

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--zip", dest="archive", required=True, type=Path)
    parser.add_argument("--tag", required=True, help="Git tag (e.g., v0.1.0.0)")
    parser.add_argument("--repo", required=True, help="GitHub repository owner/name")
    parser.add_argument("--manifest", type=Path, default=Path("manifest.json"))
    args = parser.parse_args()

    if not args.tag.startswith("v"):
        parser.error("tag must start with v")
    version = args.tag[1:]
    if len(version.split(".")) != 4 or not all(part.isdigit() for part in version.split(".")):
        parser.error("plugin version must be X.Y.Z.W")
    if len(args.repo.split("/")) != 2:
        parser.error("--repo must have the form owner/name")
    if not args.archive.is_file():
        parser.error(f"release ZIP not found: {args.archive}")

    meta = json.loads(Path("meta.json").read_text(encoding="utf-8"))
    if version != meta["version"]:
        parser.error(f"tag version {version} does not match meta.json {meta['version']}")
    checksum = hashlib.md5(args.archive.read_bytes()).hexdigest()  # Jellyfin repository format

    release = {
        "version": version,
        "changelog": "Initial preview release: recursive local subtitle discovery, including VobSub pairs.",
        "targetAbi": meta["targetAbi"],
        "sourceUrl": f"https://github.com/{args.repo}/releases/download/{args.tag}/{args.archive.name}",
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }

    if args.manifest.exists():
        catalog = json.loads(args.manifest.read_text(encoding="utf-8"))
        if not isinstance(catalog, list):
            parser.error("existing manifest.json must contain a JSON array")
    else:
        catalog = []
    matching = [entry for entry in catalog if entry.get("guid") == meta["guid"]]
    if len(matching) > 1:
        parser.error("multiple manifest entries with the same plugin GUID")
    if matching:
        plugin = matching[0]
    else:
        plugin = {"guid": meta["guid"], "versions": []}
        catalog.append(plugin)
    plugin.update({
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": args.repo.split("/", 1)[0],
        "category": meta["category"],
    })
    plugin["versions"] = [release] + [
        entry for entry in plugin["versions"]
        if not (entry.get("version") == version and entry.get("targetAbi") == meta["targetAbi"])
    ]
    args.manifest.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Updated {args.manifest}: {version}, MD5 {checksum}")


if __name__ == "__main__":
    main()
