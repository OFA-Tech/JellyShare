"""Zip the plugin DLLs from a `dotnet publish` folder into jellyshare_<version>.zip (stdlib only).

Only the files listed under `artifacts:` in build.yaml go into the zip, and they must be JellyShare.*.dll:
Jellyfin already ships Jellyfin.Controller, Jellyfin.Model, Microsoft.Extensions.* and the rest, and meta.json is
written by Jellyfin on install. A publish folder holding such host assemblies is refused.

    py build/package.py --publish-dir <dir> --version 0.1.0.0 --out dist
"""
import argparse
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")


def read_artifacts(build_yaml):
    artifacts = []
    in_list = False
    with open(build_yaml, encoding="utf-8") as f:
        for line in f:
            if re.match(r"^artifacts:\s*$", line):
                in_list = True
                continue
            if in_list:
                m = re.match(r"^\s+-\s+\"?([^\"\s]+)\"?\s*$", line)
                if m:
                    artifacts.append(m.group(1))
                elif line.strip():
                    break
    if not artifacts:
        raise ValueError("no artifacts in " + build_yaml)
    return artifacts


ARTIFACT_RE = re.compile(r"^JellyShare\.[A-Za-z0-9.]+\.dll$")
# Provided by the Jellyfin host; a copy in the publish output means a reference leaks its runtime assets.
HOST_DLL_PREFIXES = ("Microsoft.Extensions.", "Microsoft.AspNetCore.", "Jellyfin.", "MediaBrowser.")


def check_artifacts(artifacts):
    """Only JellyShare.*.dll may ship: Jellyfin loads every DLL in the plugin folder."""
    bad = [a for a in artifacts if not ARTIFACT_RE.match(a)]
    if bad:
        raise ValueError("artifacts must be JellyShare.*.dll: " + ", ".join(bad))
    return artifacts


def check_publish_dir(publish_dir):
    """Fail when the publish output carries host assemblies (they would conflict with Jellyfin's own)."""
    leaked = sorted(n for n in os.listdir(publish_dir)
                    if n.lower().endswith(".dll") and n.startswith(HOST_DLL_PREFIXES))
    if leaked:
        raise ValueError("publish output contains host assemblies: " + ", ".join(leaked))


def make_zip(publish_dir, artifacts, out_path):
    """Write a flat zip with fixed timestamps so the same DLL always gives the same checksum."""
    check_artifacts(artifacts)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name in artifacts:
            src = os.path.join(publish_dir, name)
            if not os.path.isfile(src):
                raise FileNotFoundError(src)
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(src, "rb") as f:
                z.writestr(info, f.read())
    return out_path


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--publish-dir", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--out", default="dist", help="output folder")
    p.add_argument("--build-yaml", default=os.path.join(ROOT, "build.yaml"))
    a = p.parse_args(argv)
    if not VERSION_RE.match(a.version):
        p.error("version must look like 0.1.0.0")
    out = os.path.join(a.out, "jellyshare_%s.zip" % a.version)
    check_publish_dir(a.publish_dir)
    make_zip(a.publish_dir, read_artifacts(a.build_yaml), out)
    print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
