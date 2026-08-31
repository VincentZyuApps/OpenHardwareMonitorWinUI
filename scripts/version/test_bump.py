from __future__ import annotations

import unittest

from bump import render_props
from check import expected_values


PROPS_TEMPLATE = """<Project>\r
  <PropertyGroup>\r
    <VersionPrefix>4.0.0</VersionPrefix>\r
    <Version>4.0.0-alpha.1</Version>\r
    <AssemblyVersion>4.0.0.0</AssemblyVersion>\r
    <FileVersion>4.0.0.0</FileVersion>\r
    <InformationalVersion>4.0.0-alpha.1</InformationalVersion>\r
    <UnrelatedProperty>keep-me</UnrelatedProperty>\r
  </PropertyGroup>\r
</Project>\r
"""


class VersionBumpTests(unittest.TestCase):
    def test_expected_values_include_prerelease_only_where_supported(self) -> None:
        self.assertEqual(
            {
                "VersionPrefix": "4.0.1",
                "Version": "4.0.1-alpha.2",
                "AssemblyVersion": "4.0.1.0",
                "FileVersion": "4.0.1.0",
                "InformationalVersion": "4.0.1-alpha.2",
            },
            expected_values("4.0.1-alpha.2"),
        )

    def test_render_updates_only_managed_values_and_preserves_line_endings(self) -> None:
        rendered = render_props(PROPS_TEMPLATE, "4.0.1-alpha.2")

        self.assertIn("<VersionPrefix>4.0.1</VersionPrefix>", rendered)
        self.assertIn("<Version>4.0.1-alpha.2</Version>", rendered)
        self.assertIn("<AssemblyVersion>4.0.1.0</AssemblyVersion>", rendered)
        self.assertIn("<FileVersion>4.0.1.0</FileVersion>", rendered)
        self.assertIn("<InformationalVersion>4.0.1-alpha.2</InformationalVersion>", rendered)
        self.assertIn("<UnrelatedProperty>keep-me</UnrelatedProperty>", rendered)
        self.assertNotIn("\n", rendered.replace("\r\n", ""))

    def test_invalid_version_is_rejected_without_rendering(self) -> None:
        for version in ("v4.0.1", "4.0", "4.0.1-alpha..2", "4.0.1-alpha.02"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                render_props(PROPS_TEMPLATE, version)

    def test_semantic_version_build_metadata_does_not_change_binary_versions(self) -> None:
        values = expected_values("4.0.1-alpha.2+build.17")

        self.assertEqual("4.0.1", values["VersionPrefix"])
        self.assertEqual("4.0.1.0", values["AssemblyVersion"])
        self.assertEqual("4.0.1-alpha.2+build.17", values["InformationalVersion"])

    def test_dotnet_assembly_version_component_bounds_are_enforced(self) -> None:
        self.assertEqual("65534.0.0.0", expected_values("65534.0.0")["AssemblyVersion"])
        for version in ("65535.0.0", "4.70000.1", "4.0.65535"):
            with self.subTest(version=version), self.assertRaisesRegex(ValueError, "AssemblyVersion"):
                expected_values(version)

    def test_duplicate_managed_field_is_rejected(self) -> None:
        duplicate = PROPS_TEMPLATE.replace(
            "<Version>4.0.0-alpha.1</Version>",
            "<Version>4.0.0-alpha.1</Version><Version>4.0.0</Version>",
        )

        with self.assertRaisesRegex(ValueError, "Duplicate managed version field"):
            render_props(duplicate, "4.0.1")


if __name__ == "__main__":
    unittest.main()
