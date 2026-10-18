"""Negative and positive synthetic ZIP fixtures for package identity checks."""

import importlib.util
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-package-identities.py"
SPEC = importlib.util.spec_from_file_location("verify_package_identities", MODULE_PATH)
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)

ID = "SharpLink.Client"
VERSION = "3.0.0"
COMMIT = "a" * 40


def package(path: Path, package_id=ID, version=VERSION, commit=COMMIT, include_repository=True):
    repository = f'<repository type="git" commit="{commit}" />' if include_repository else ""
    nuspec = (
        '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
        f"<metadata><id>{package_id}</id><version>{version}</version>{repository}</metadata></package>"
    )
    with ZipFile(path, "w") as archive:
        archive.writestr(f"{ID}.nuspec", nuspec)


class PackageIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.nupkg = root / f"{ID}.{VERSION}.nupkg"
        self.snupkg = root / f"{ID}.{VERSION}.snupkg"
        package(self.nupkg)
        package(self.snupkg, include_repository=False)

    def check(self):
        VERIFY.verify_pair(self.nupkg, self.snupkg, ID, VERSION, COMMIT)

    def test_valid_pair(self):
        self.check()

    def test_wrong_nupkg_id(self):
        package(self.nupkg, package_id="Unrelated.Package")
        with self.assertRaisesRegex(ValueError, "nuspec identity"):
            self.check()

    def test_wrong_nupkg_version(self):
        package(self.nupkg, version="99.0.0")
        with self.assertRaisesRegex(ValueError, "nuspec identity"):
            self.check()

    def test_wrong_symbol_version(self):
        package(self.snupkg, version="2.0.3")
        with self.assertRaisesRegex(ValueError, "nuspec identity"):
            self.check()

    def test_wrong_symbol_commit(self):
        package(self.snupkg, commit="b" * 40)
        with self.assertRaisesRegex(ValueError, "repository commit"):
            self.check()

    def test_wrong_symbol_filename(self):
        renamed = self.snupkg.with_name(f"{ID}.2.0.3.snupkg")
        self.snupkg.rename(renamed)
        with self.assertRaisesRegex(ValueError, "exact filename"):
            VERIFY.verify_pair(self.nupkg, renamed, ID, VERSION, COMMIT)

    def test_missing_nuspec(self):
        with ZipFile(self.snupkg, "w") as archive:
            archive.writestr("readme", "not a package")
        with self.assertRaisesRegex(ValueError, "expected one root"):
            self.check()


if __name__ == "__main__":
    unittest.main()
