"""Tests for build/package.py and build/update_manifest.py (stdlib only).

Run: py -m unittest discover -s build -p "test_*.py"
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import package  # noqa: E402
import update_manifest as um  # noqa: E402

GUID = "8a55d113-5072-47cf-9827-f3c49f1c3971"
URL = "https://github.com/OFA-Tech/JellyShare/releases/download/v{v}/jellyshare_{v}.zip"


def base_manifest():
    return [{
        "guid": GUID,
        "name": "JellyShare",
        "description": "d",
        "overview": "o",
        "owner": "OFA-TECH",
        "category": "General",
        "versions": [],
    }]


class UpdateManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.zip_path = os.path.join(self.tmp.name, "jellyshare_0.1.0.0.zip")
        with zipfile.ZipFile(self.zip_path, "w") as z:
            z.writestr("JellyShare.Jellyfin.dll", b"dll-bytes")
        with open(self.zip_path, "rb") as f:
            self.md5 = hashlib.md5(f.read()).hexdigest()

    def add(self, manifest, version, zip_path=None, changelog="c", now="2026-11-01T10:20:30Z"):
        return um.add_version(
            manifest, GUID, version, zip_path or self.zip_path,
            URL.format(v=version), changelog, "12.0.0.0", now)

    def test_prepends_version_with_all_fields(self):
        m = self.add(base_manifest(), "0.1.0.0", changelog="First release")
        v = m[0]["versions"][0]
        self.assertEqual(v, {
            "version": "0.1.0.0",
            "changelog": "First release",
            "targetAbi": "12.0.0.0",
            "sourceUrl": URL.format(v="0.1.0.0"),
            "checksum": self.md5,
            "timestamp": "2026-11-01T10:20:30Z",
        })

    def test_checksum_is_lowercase_md5_hex_and_timestamp_iso_utc(self):
        v = self.add(base_manifest(), "0.1.0.0")[0]["versions"][0]
        self.assertRegex(v["checksum"], r"^[0-9a-f]{32}$")
        self.assertRegex(v["timestamp"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")

    def test_new_version_goes_first(self):
        m = self.add(base_manifest(), "0.1.0.0")
        m = self.add(m, "0.1.0.1")
        self.assertEqual([v["version"] for v in m[0]["versions"]], ["0.1.0.1", "0.1.0.0"])

    def test_rerun_same_version_replaces_not_duplicates(self):
        m = self.add(base_manifest(), "0.1.0.0", changelog="a", now="2026-11-01T10:00:00Z")
        m = self.add(m, "0.1.0.1")
        with zipfile.ZipFile(self.zip_path, "w") as z:
            z.writestr("JellyShare.Jellyfin.dll", b"other")
        m = self.add(m, "0.1.0.0", changelog="b", now="2026-11-02T10:00:00Z")
        versions = m[0]["versions"]
        self.assertEqual([v["version"] for v in versions], ["0.1.0.1", "0.1.0.0"])
        old = versions[1]
        self.assertEqual(old["changelog"], "b")
        self.assertEqual(old["timestamp"], "2026-11-02T10:00:00Z")
        self.assertEqual(old["checksum"], um.md5_file(self.zip_path))

    def test_rerun_identical_is_byte_identical(self):
        m1 = self.add(base_manifest(), "0.1.0.0")
        s1 = json.dumps(m1, sort_keys=True)
        m2 = self.add(m1, "0.1.0.0")
        self.assertEqual(s1, json.dumps(m2, sort_keys=True))

    def test_rejects_bad_version(self):
        for bad in ("v0.1.0.0", "0.1.0", "1.2.3.4-rc1", "", "0.1.0.0\n"):
            with self.assertRaises(ValueError, msg=bad):
                self.add(base_manifest(), bad)

    def test_unknown_guid_fails(self):
        with self.assertRaises(KeyError):
            um.add_version(base_manifest(), "00000000-0000-0000-0000-000000000000", "0.1.0.0",
                           self.zip_path, "u", "c", "12.0.0.0", "t")

    def test_other_packages_untouched(self):
        m = base_manifest() + [{"guid": "other", "name": "X", "versions": [{"version": "9"}]}]
        m = self.add(m, "0.1.0.0")
        self.assertEqual(m[1]["versions"], [{"version": "9"}])

    def test_cli_updates_file_in_place(self):
        path = os.path.join(self.tmp.name, "manifest.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(base_manifest(), f)
        clog = os.path.join(self.tmp.name, "changelog.txt")
        with open(clog, "w", encoding="utf-8") as f:
            f.write("- fix a\n- fix b\n")
        args = [sys.executable, os.path.join(HERE, "update_manifest.py"),
                "--manifest", path, "--guid", GUID, "--version", "0.1.0.0", "--zip", self.zip_path,
                "--source-url", URL.format(v="0.1.0.0"), "--changelog-file", clog]
        subprocess.run(args, check=True, capture_output=True)
        with open(path, encoding="utf-8") as f:
            text = f.read()
        v = json.loads(text)[0]["versions"][0]
        self.assertEqual(v["changelog"], "- fix a\n- fix b")
        self.assertEqual(v["targetAbi"], "12.0.0.0")
        self.assertEqual(v["checksum"], self.md5)
        self.assertRegex(v["timestamp"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
        self.assertTrue(text.endswith("\n"))
        subprocess.run(args, check=True, capture_output=True)
        with open(path, encoding="utf-8") as f:
            self.assertEqual(len(json.load(f)[0]["versions"]), 1)

    def test_committed_manifest_matches_build_yaml_guid(self):
        root = os.path.dirname(HERE)
        with open(os.path.join(root, "manifest.json"), encoding="utf-8") as f:
            manifest = json.load(f)
        self.assertEqual(manifest[0]["guid"], GUID)
        with open(os.path.join(root, "build.yaml"), encoding="utf-8") as f:
            self.assertIn(GUID, f.read())


class PackageTests(unittest.TestCase):
    def test_zips_only_listed_artifacts_flat(self):
        with tempfile.TemporaryDirectory() as tmp:
            pub = os.path.join(tmp, "pub")
            os.makedirs(pub)
            for name in ("JellyShare.Jellyfin.dll", "JellyShare.Jellyfin.pdb",
                         "JellyShare.Jellyfin.deps.json", "Jellyfin.Controller.dll"):
                with open(os.path.join(pub, name), "wb") as f:
                    f.write(name.encode())
            out = os.path.join(tmp, "dist", "jellyshare_0.1.0.0.zip")
            package.make_zip(pub, ["JellyShare.Jellyfin.dll"], out)
            with zipfile.ZipFile(out) as z:
                self.assertEqual(z.namelist(), ["JellyShare.Jellyfin.dll"])

    def test_missing_artifact_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(FileNotFoundError):
                package.make_zip(tmp, ["JellyShare.Nope.dll"], os.path.join(tmp, "x.zip"))

    def test_zip_is_deterministic(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "JellyShare.A.dll"), "wb") as f:
                f.write(b"a")
            a, b = os.path.join(tmp, "1.zip"), os.path.join(tmp, "2.zip")
            package.make_zip(tmp, ["JellyShare.A.dll"], a)
            os.utime(os.path.join(tmp, "JellyShare.A.dll"), (1, 1))
            package.make_zip(tmp, ["JellyShare.A.dll"], b)
            self.assertEqual(um.md5_file(a), um.md5_file(b))

    def test_artifacts_read_from_build_yaml(self):
        root = os.path.dirname(HERE)
        arts = package.read_artifacts(os.path.join(root, "build.yaml"))
        self.assertEqual(arts, ["JellyShare.Jellyfin.dll", "JellyShare.Application.dll", "JellyShare.Domain.dll",
                                "JellyShare.Data.Peers.dll", "JellyShare.Data.Files.dll", "JellyShare.Media.dll"])

    def test_zips_every_jellyshare_assembly(self):
        with tempfile.TemporaryDirectory() as tmp:
            names = ["JellyShare.Jellyfin.dll", "JellyShare.Application.dll", "JellyShare.Domain.dll",
                     "JellyShare.Data.Peers.dll", "JellyShare.Data.Files.dll", "JellyShare.Media.dll"]
            for name in names + ["JellyShare.Domain.pdb", "JellyShare.Jellyfin.deps.json"]:
                with open(os.path.join(tmp, name), "wb") as f:
                    f.write(name.encode())
            out = os.path.join(tmp, "dist", "z.zip")
            package.make_zip(tmp, names, out)
            with zipfile.ZipFile(out) as z:
                self.assertEqual(z.namelist(), names)

    def test_only_jellyshare_dlls_may_be_artifacts(self):
        for bad in ("Jellyfin.Controller.dll", "Microsoft.Extensions.Logging.Abstractions.dll", "JellyShare.Jellyfin.pdb", "x.dll"):
            with self.assertRaises(ValueError, msg=bad):
                package.check_artifacts(["JellyShare.Jellyfin.dll", bad])

    def test_unlisted_jellyshare_assembly_in_publish_dir_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            for name in ("JellyShare.Jellyfin.dll", "JellyShare.Media.dll", "JellyShare.Media.pdb"):
                open(os.path.join(tmp, name), "wb").close()
            package.check_complete(tmp, ["JellyShare.Jellyfin.dll", "JellyShare.Media.dll"])
            with self.assertRaises(ValueError):
                package.check_complete(tmp, ["JellyShare.Jellyfin.dll"])

    def test_publish_dir_with_host_assemblies_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            for name in ("JellyShare.Jellyfin.dll", "JellyShare.Domain.dll"):
                open(os.path.join(tmp, name), "wb").close()
            package.check_publish_dir(tmp)
            open(os.path.join(tmp, "Microsoft.Extensions.Logging.Abstractions.dll"), "wb").close()
            with self.assertRaises(ValueError):
                package.check_publish_dir(tmp)

    def test_cli_publish_dir_with_host_assembly_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            for name in ("JellyShare.Jellyfin.dll", "JellyShare.Application.dll", "JellyShare.Domain.dll",
                         "Microsoft.Extensions.Hosting.Abstractions.dll"):
                open(os.path.join(tmp, name), "wb").close()
            res = subprocess.run([sys.executable, os.path.join(HERE, "package.py"), "--publish-dir", tmp,
                                  "--version", "0.1.0.0", "--out", os.path.join(tmp, "dist")], capture_output=True)
            self.assertNotEqual(res.returncode, 0)
            self.assertFalse(os.path.exists(os.path.join(tmp, "dist", "jellyshare_0.1.0.0.zip")))


if __name__ == "__main__":
    unittest.main()
