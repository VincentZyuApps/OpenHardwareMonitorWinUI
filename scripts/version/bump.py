from __future__ import annotations

import argparse
import codecs
import os
import sys
import uuid
import xml.etree.ElementTree as element_tree
from pathlib import Path

from check import (
    MANAGED_FIELDS,
    PROPS,
    collect_managed_values,
    expected_values,
    validate_props,
    validate_values,
)


def render_props(source: str, version: str) -> str:
    expected = expected_values(version)
    root = element_tree.fromstring(source)
    collect_managed_values(root)

    rendered = source
    for name in MANAGED_FIELDS:
        opening = f"<{name}>"
        closing = f"</{name}>"
        if rendered.count(opening) != 1 or rendered.count(closing) != 1:
            raise ValueError(f"Managed field {name} must use one plain <{name}> element")
        value_start = rendered.index(opening) + len(opening)
        value_end = rendered.index(closing, value_start)
        rendered = rendered[:value_start] + expected[name] + rendered[value_end:]

    validate_values(collect_managed_values(element_tree.fromstring(rendered)), version)
    return rendered


def read_utf8(path: Path) -> tuple[str, bool]:
    data = path.read_bytes()
    has_bom = data.startswith(codecs.BOM_UTF8)
    return data.decode("utf-8-sig"), has_bom


def write_atomic_utf8(path: Path, text: str, has_bom: bool) -> None:
    temporary_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    data = text.encode("utf-8")
    if has_bom:
        data = codecs.BOM_UTF8 + data
    try:
        temporary_path.write_bytes(data)
        validate_props(temporary_path)
        os.replace(temporary_path, path)
    finally:
        temporary_path.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Update every product-version field managed in Directory.Build.props."
    )
    parser.add_argument("version", help="Target semantic version, for example 4.0.1-alpha.2")
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify that every managed field already matches the target without writing files.",
    )
    args = parser.parse_args()

    expected_values(args.version)
    if args.check:
        validate_props(PROPS, args.version)
        print(args.version)
        return 0

    source, has_bom = read_utf8(PROPS)
    rendered = render_props(source, args.version)
    if rendered != source:
        write_atomic_utf8(PROPS, rendered, has_bom)
    validate_props(PROPS, args.version)
    print(args.version)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError, element_tree.ParseError) as error:
        print(f"version bump failed: {error}", file=sys.stderr)
        raise SystemExit(1)
