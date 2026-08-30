from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as element_tree
from pathlib import Path


VERSION_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$")
ROOT = Path(__file__).resolve().parents[2]
PROPS = ROOT / "Directory.Build.props"


def main() -> int:
    root = element_tree.parse(PROPS).getroot()
    values = {node.tag.rsplit("}", 1)[-1]: (node.text or "").strip() for node in root.iter()}
    version = values.get("Version", "")
    if not VERSION_PATTERN.fullmatch(version):
        raise ValueError(f"Invalid Version in {PROPS}: {version!r}")
    core = version.split("-", 1)[0]
    expected = {"VersionPrefix": core, "AssemblyVersion": core + ".0", "FileVersion": core + ".0", "InformationalVersion": version}
    for name, value in expected.items():
        if values.get(name) != value:
            raise ValueError(f"{name} must be {value!r}, got {values.get(name)!r}")
    print(version)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, element_tree.ParseError) as error:
        print(f"version check failed: {error}", file=sys.stderr)
        raise SystemExit(1)
