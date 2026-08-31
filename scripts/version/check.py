from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as element_tree
from pathlib import Path


NUMERIC_IDENTIFIER = r"(?:0|[1-9]\d*)"
NON_NUMERIC_IDENTIFIER = r"(?:\d*[A-Za-z-][0-9A-Za-z-]*)"
PRERELEASE_IDENTIFIER = rf"(?:{NUMERIC_IDENTIFIER}|{NON_NUMERIC_IDENTIFIER})"
BUILD_IDENTIFIER = r"(?:[0-9A-Za-z-]+)"
VERSION_PATTERN = re.compile(
    rf"^(?P<major>{NUMERIC_IDENTIFIER})\."
    rf"(?P<minor>{NUMERIC_IDENTIFIER})\."
    rf"(?P<patch>{NUMERIC_IDENTIFIER})"
    rf"(?:-(?P<prerelease>{PRERELEASE_IDENTIFIER}(?:\.{PRERELEASE_IDENTIFIER})*))?"
    rf"(?:\+(?P<build>{BUILD_IDENTIFIER}(?:\.{BUILD_IDENTIFIER})*))?$"
)
ROOT = Path(__file__).resolve().parents[2]
PROPS = ROOT / "Directory.Build.props"
MANAGED_FIELDS = (
    "VersionPrefix",
    "Version",
    "AssemblyVersion",
    "FileVersion",
    "InformationalVersion",
)


def expected_values(version: str) -> dict[str, str]:
    match = VERSION_PATTERN.fullmatch(version)
    if match is None:
        raise ValueError(f"Invalid semantic version: {version!r}")
    core_parts = (match["major"], match["minor"], match["patch"])
    if any(int(part) > 65534 for part in core_parts):
        raise ValueError(f"Version components must fit .NET AssemblyVersion (0-65534): {version!r}")
    core = ".".join(core_parts)
    return {
        "VersionPrefix": core,
        "Version": version,
        "AssemblyVersion": core + ".0",
        "FileVersion": core + ".0",
        "InformationalVersion": version,
    }


def collect_managed_values(root: element_tree.Element) -> dict[str, str]:
    values: dict[str, str] = {}
    for node in root.iter():
        name = node.tag.rsplit("}", 1)[-1]
        if name not in MANAGED_FIELDS:
            continue
        if name in values:
            raise ValueError(f"Duplicate managed version field: {name}")
        values[name] = (node.text or "").strip()
    missing = [name for name in MANAGED_FIELDS if name not in values]
    if missing:
        raise ValueError(f"Missing managed version field(s): {', '.join(missing)}")
    return values


def validate_values(values: dict[str, str], target_version: str | None = None) -> str:
    version = values["Version"]
    if not VERSION_PATTERN.fullmatch(version):
        raise ValueError(f"Invalid Version: {version!r}")
    if target_version is not None and version != target_version:
        raise ValueError(f"Version must be {target_version!r}, got {version!r}")
    expected = expected_values(target_version or version)
    for name, value in expected.items():
        if values.get(name) != value:
            raise ValueError(f"{name} must be {value!r}, got {values.get(name)!r}")
    return version


def validate_props(path: Path = PROPS, target_version: str | None = None) -> str:
    root = element_tree.parse(path).getroot()
    return validate_values(collect_managed_values(root), target_version)


def main() -> int:
    version = validate_props()
    print(version)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, element_tree.ParseError) as error:
        print(f"version check failed: {error}", file=sys.stderr)
        raise SystemExit(1)
