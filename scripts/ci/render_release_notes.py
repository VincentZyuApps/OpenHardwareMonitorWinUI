from __future__ import annotations

import argparse
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / ".github" / "release_template.md"
PLACEHOLDER_PATTERN = re.compile(r"__[A-Z0-9_]+__")


def read_commit_log(repository: str, count: int) -> str:
    result = subprocess.run(
        ["git", "log", f"-{count}", "--pretty=format:%h%x09%H%x09%s"],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    entries = []
    for line in result.stdout.splitlines():
        short_hash, full_hash, subject = line.split("\t", 2)
        entries.append(
            f"- [`{short_hash}`](https://github.com/{repository}/commit/{full_hash}) {subject}"
        )
    return "\n".join(entries) or "- Manual build"


def main() -> None:
    parser = argparse.ArgumentParser(description="Render the GitHub Release body.")
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--asset-name", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit-count", type=int, default=20)
    args = parser.parse_args()

    if args.commit_count < 1:
        parser.error("--commit-count must be at least 1")

    base_url = f"https://github.com/{args.repository}/releases/download/v{args.version}"
    build_info = "\n".join(
        (
            f"- Commit: [`{args.sha[:12]}`](https://github.com/{args.repository}/commit/{args.sha})",
            "- Runtime: `.NET 10`",
            "- Target: `win-x64`",
            "- Package: self-contained ZIP",
        )
    )
    rendered = (
        TEMPLATE.read_text(encoding="utf-8")
        .replace("__REPO__", args.repository)
        .replace("__VERSION__", args.version)
        .replace("__BASE_URL__", base_url)
        .replace("__ASSET_NAME__", args.asset_name)
        .replace("__BUILD_INFO__", build_info)
        .replace("__COMMIT_LOG__", read_commit_log(args.repository, args.commit_count))
    )
    unresolved = sorted(set(PLACEHOLDER_PATTERN.findall(rendered)))
    if unresolved:
        raise ValueError(f"Unresolved release template placeholders: {', '.join(unresolved)}")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(rendered, encoding="utf-8")


if __name__ == "__main__":
    main()
