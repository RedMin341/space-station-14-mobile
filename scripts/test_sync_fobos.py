"""Offline regression tests for the Fobos APK revision-sync gate."""

import unittest

from scripts import sync_fobos


OLD_CONTENT = "a" * 40
NEW_CONTENT = "b" * 40
ENGINE_SHA = "c" * 40


def fixture():
    return {
        "Content.Android/FobosBuild.cs": (
            'public const string Engine = "276.0.6";\n'
            f'public const string ContentRevision = "{OLD_CONTENT}";\n'
        ),
        "scripts/prepare-fobos.py": (
            f'CONTENT = "{OLD_CONTENT}"\n'
            f'ENGINE = "{ENGINE_SHA}"\n'
            'print(f"Fobos content {CONTENT}; engine 276.0.6 ({ENGINE})")\n'
        ),
        ".github/workflows/android-release.yml": (
            '      - name: Checkout pinned Fobos content\n'
            '        with:\n'
            f'          ref: {OLD_CONTENT}\n'
        ),
        "Content.Android/Content.Android.csproj": (
            '<ApplicationVersion>19</ApplicationVersion>\n'
            '<ApplicationDisplayVersion>0.1.18</ApplicationDisplayVersion>\n'
        ),
    }


class SyncFobosTests(unittest.TestCase):
    def test_unchanged_engine_and_content_does_not_rebuild(self):
        self.assertEqual(sync_fobos.plan(fixture(), "276.0.6", OLD_CONTENT, ENGINE_SHA), {})

    def test_content_only_change_rebuilds_apk_and_bumps_android_version(self):
        result = sync_fobos.plan(fixture(), "276.0.6", NEW_CONTENT, ENGINE_SHA)
        self.assertEqual(len(result), 4)
        self.assertIn(f'ContentRevision = "{NEW_CONTENT}"', result["Content.Android/FobosBuild.cs"])
        self.assertIn(f'CONTENT = "{NEW_CONTENT}"', result["scripts/prepare-fobos.py"])
        self.assertIn(f'ref: {NEW_CONTENT}', result[".github/workflows/android-release.yml"])
        self.assertIn('<ApplicationVersion>20</ApplicationVersion>', result["Content.Android/Content.Android.csproj"])
        self.assertIn('<ApplicationDisplayVersion>0.1.19</ApplicationDisplayVersion>', result["Content.Android/Content.Android.csproj"])

    def test_engine_change_still_rebuilds(self):
        result = sync_fobos.plan(fixture(), "276.0.7", NEW_CONTENT, "d" * 40)
        self.assertIn('Engine = "276.0.7"', result["Content.Android/FobosBuild.cs"])
        self.assertIn('ENGINE = "' + "d" * 40 + '"', result["scripts/prepare-fobos.py"])


if __name__ == "__main__":
    unittest.main()
