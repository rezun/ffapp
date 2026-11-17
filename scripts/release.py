#!/usr/bin/env python3
"""Changelog-driven release helper for FFApp and FFRux.

  release.py prepare <version>  Move the Unreleased changelog entries into a new
                                version section and bump both app versions.
  release.py notes <version>    Print that version's changelog section as GitHub
                                release notes. Fails if it is missing or empty.
"""

# Keeps the modern type hints working on macOS's system Python 3.9.
from __future__ import annotations

import argparse
import datetime
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CHANGELOG = ROOT / "CHANGELOG.md"
PROJECTS = [
    ROOT / "src/FFApp/FFApp.csproj",
    ROOT / "src/FFRux/FFRux.csproj",
    # The agent reports the app's version: the app asks it to install exactly that one.
    ROOT / "src/FFApp.Agent/FFApp.Agent.csproj",
]
REPO_URL = "https://github.com/rezun/ffapp"
UNRELEASED = "Unreleased"

SEMVER = re.compile(r"^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$")
HEADING = re.compile(r"^## \[(?P<name>[^\]]+)\]", re.MULTILINE)
LINK_REF = re.compile(r"^\[(?P<name>[^\]]+)\]: ", re.MULTILINE)
VERSION_ELEMENT = re.compile(r"<Version>[^<]*</Version>")


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def find_section(text: str, name: str) -> tuple[int, int, int]:
    """Return (heading start, body start, body end) of a version section."""
    for match in HEADING.finditer(text):
        if match.group("name") == name:
            body_start = text.index("\n", match.end()) + 1
            following = HEADING.search(text, body_start)
            link_refs = LINK_REF.search(text, body_start)
            ends = [m.start() for m in (following, link_refs) if m]
            return match.start(), body_start, min(ends, default=len(text))
    fail(f"CHANGELOG.md has no section for [{name}]")
    raise AssertionError("unreachable")


def has_entries(body: str) -> bool:
    return any(line.lstrip().startswith("- ") for line in body.splitlines())


def previous_version(text: str, version: str) -> str | None:
    names = [m.group("name") for m in HEADING.finditer(text) if m.group("name") != UNRELEASED]
    index = names.index(version)
    return names[index + 1] if index + 1 < len(names) else None


def notes(version: str) -> None:
    text = CHANGELOG.read_text(encoding="utf-8")
    _, body_start, body_end = find_section(text, version)
    body = text[body_start:body_end].strip()
    if not has_entries(body):
        fail(f"CHANGELOG.md section [{version}] has no entries")

    # GitHub shows the release title already; demote headings so they read as sub-sections.
    body = re.sub(r"^### ", "## ", body, flags=re.MULTILINE)
    previous = previous_version(text, version)
    if previous:
        body += f"\n\n**Full changelog**: {REPO_URL}/compare/v{previous}...v{version}"
    print(body)


def prepare(version: str) -> None:
    text = CHANGELOG.read_text(encoding="utf-8")
    if any(m.group("name") == version for m in HEADING.finditer(text)):
        fail(f"CHANGELOG.md already has a section for [{version}]")

    _, body_start, body_end = find_section(text, UNRELEASED)
    entries = text[body_start:body_end].strip()
    if not has_entries(entries):
        fail("the Unreleased section has no entries; describe the changes first")

    previous = next((m.group("name") for m in HEADING.finditer(text) if m.group("name") != UNRELEASED), None)
    today = datetime.date.today().isoformat()
    text = text[:body_start] + f"\n## [{version}] - {today}\n\n{entries}\n\n" + text[body_end:]

    links = f"[{UNRELEASED}]: {REPO_URL}/compare/v{version}...HEAD\n"
    links += f"[{version}]: {REPO_URL}/compare/v{previous}...v{version}\n" if previous else f"[{version}]: {REPO_URL}/releases/tag/v{version}\n"
    text, replaced = re.subn(rf"^\[{UNRELEASED}\]: .*\n", links, text, count=1, flags=re.MULTILINE)
    if not replaced:
        text = text.rstrip("\n") + "\n\n" + links
    CHANGELOG.write_text(text, encoding="utf-8")

    for project in PROJECTS:
        content = project.read_text(encoding="utf-8")
        updated, count = VERSION_ELEMENT.subn(f"<Version>{version}</Version>", content, count=1)
        if not count:
            fail(f"{project.relative_to(ROOT)} has no <Version> element")
        project.write_text(updated, encoding="utf-8")

    print(f"Prepared {version}. Review the diff, then:")
    print(f'  git commit -am "Bump FFApp and FFRux to {version}" && git tag v{version} && git push origin main v{version}')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["prepare", "notes"])
    parser.add_argument("version", help="semver version without the leading v")
    args = parser.parse_args()

    version = args.version.removeprefix("v")
    if not SEMVER.match(version):
        fail(f"'{version}' is not a valid semver version")

    if args.command == "prepare":
        prepare(version)
    else:
        notes(version)


if __name__ == "__main__":
    main()
