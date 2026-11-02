"""Add a release to the Jellyfin plugin repository manifest (stdlib only).

    py build/update_manifest.py --manifest manifest.json --version 0.1.0.0 \
        --zip dist/jellyshare_0.1.0.0.zip --source-url <release asset url> --changelog-file notes.txt

The version goes first in `versions`; running it again for the same version replaces that entry.
The checksum is the lowercase MD5 hex of the zip, as Jellyfin expects.
"""
import argparse
import datetime
import hashlib
import json
import re
import sys

VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+\Z")
DEFAULT_GUID = "8a55d113-5072-47cf-9827-f3c49f1c3971"


def md5_file(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def add_version(manifest, guid, version, zip_path, source_url, changelog, target_abi, timestamp):
    if not VERSION_RE.match(version):
        raise ValueError("version must look like 0.1.0.0, got %r" % version)
    package = next((p for p in manifest if p.get("guid") == guid), None)
    if package is None:
        raise KeyError("no package with guid " + guid)
    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": md5_file(zip_path),
        "timestamp": timestamp,
    }
    versions = package.setdefault("versions", [])
    for i, v in enumerate(versions):
        if v.get("version") == version:
            versions[i] = entry
            break
    else:
        versions.insert(0, entry)
    return manifest


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--manifest", required=True)
    p.add_argument("--guid", default=DEFAULT_GUID)
    p.add_argument("--version", required=True)
    p.add_argument("--zip", required=True)
    p.add_argument("--source-url", required=True)
    p.add_argument("--changelog-file", help="text file with the changelog (default: 'Release <version>')")
    p.add_argument("--target-abi", default="12.0.0.0")
    p.add_argument("--timestamp", help="ISO-8601 UTC, default now")
    a = p.parse_args(argv)

    changelog = "Release " + a.version
    if a.changelog_file:
        with open(a.changelog_file, encoding="utf-8") as f:
            changelog = f.read().strip() or changelog

    with open(a.manifest, encoding="utf-8") as f:
        manifest = json.load(f)
    add_version(manifest, a.guid, a.version, a.zip, a.source_url, changelog, a.target_abi,
                a.timestamp or utc_now())
    with open(a.manifest, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
