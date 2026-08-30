from __future__ import annotations

import argparse


ARTIFACT_TOKENS = ("[build-action]", "[build action]")
RELEASE_TOKENS = ("[build-release]", "[build release]")


def classify(message: str) -> str:
    if any(token in message for token in RELEASE_TOKENS):
        return "release"
    if any(token in message for token in ARTIFACT_TOKENS):
        return "artifact"
    return "none"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Classify a build trigger in a commit message.")
    parser.add_argument("--message", required=True)
    print(classify(parser.parse_args().message))
