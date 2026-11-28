import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "verify_release_versions", Path(__file__).resolve().parents[1] / "verify-release-versions.py")
versions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(versions)


class ReleaseVersionsTests(unittest.TestCase):
    def verify(self, published, current, policy, **extra):
        versions.verify_transition(
            dict(name="entry", published=published, current=current, policy=policy, **extra),
            {"Protocol minor": 6},
        )

    def test_existing_policies_without_reserved_drafts(self):
        for before, after, policy in ((4, 5, "increment"), (2, 2, "unchanged"),
                                     (None, 1, "initial"), (None, 6, "floor")):
            with self.subTest(policy=policy):
                self.verify(before, after, policy)

    def test_reserved_unpublished_protocol_and_abi_drafts(self):
        self.verify(4, 6, "increment", reservedDraftVersions=[5])
        self.verify(None, 2, "initial", reservedDraftVersions=[1])

    def test_missing_draft_or_attempt_to_reuse_it_fails(self):
        for before, after, policy, drafts in ((4, 6, "increment", []),
                                              (4, 5, "increment", [5]),
                                              (None, 2, "initial", []),
                                              (None, 1, "initial", [1])):
            with self.subTest(before=before, after=after, policy=policy):
                with self.assertRaisesRegex(ValueError, "Invalid release calibration"):
                    self.verify(before, after, policy, reservedDraftVersions=drafts)

    def test_draft_versions_cannot_be_arbitrary_or_duplicated(self):
        for drafts in ([5, 5], [6, 5], [4, 5], [5, 7]):
            with self.subTest(drafts=drafts):
                with self.assertRaisesRegex(ValueError, "Invalid release calibration"):
                    self.verify(4, 7, "increment", reservedDraftVersions=drafts)

    def test_draft_versions_must_be_integers_in_a_list(self):
        for drafts in (None, "5", [True], [5.0]):
            with self.subTest(drafts=drafts):
                with self.assertRaisesRegex(ValueError, "list of integers"):
                    self.verify(4, 6, "increment", reservedDraftVersions=drafts)

    def test_unchanged_and_floor_do_not_support_draft_reservations(self):
        with self.assertRaisesRegex(ValueError, "Invalid release calibration"):
            self.verify(2, 2, "unchanged", reservedDraftVersions=[1])
        with self.assertRaisesRegex(ValueError, "Invalid release calibration"):
            self.verify(None, 6, "floor", reservedDraftVersions=[5])
        with self.assertRaisesRegex(ValueError, "Invalid release calibration"):
            self.verify(None, 5, "floor")
