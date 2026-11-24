#!/usr/bin/env python3
"""Check local inline Markdown links and heading anchors in the repository."""

from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit

FENCE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")
HEADING = re.compile(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$")
LINK = re.compile(r"\]\((<?[^()\s]+>?)\)")


def prose(path):
    fence = None
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        match = FENCE.match(line)
        if match:
            marker = match[1]
            if fence is None:
                fence = marker
            elif marker[0] == fence[0] and len(marker) >= len(fence):
                fence = None
        elif fence is None:
            yield number, line


def headings(path):
    result = set()
    for _, line in prose(path):
        match = HEADING.match(line)
        if match:
            slug = re.sub(r"[^\w -]", "", match[1].lower()).replace(" ", "-")
            anchor = slug
            duplicate = 1
            while anchor in result:
                anchor = f"{slug}-{duplicate}"
                duplicate += 1
            result.add(anchor)
    return result


def main():
    root = Path(__file__).resolve().parents[1]
    files = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.md"], cwd=root
    ).decode().split("\0")[:-1]
    errors = []
    count = 0
    for file in sorted(set(files)):
        path = root / file
        if not path.is_file():
            continue
        for line_number, line in prose(path):
            for match in LINK.finditer(line):
                reference = match[1].strip("<>")
                url = urlsplit(reference)
                if url.scheme or url.netloc:
                    continue
                target = (path.parent / unquote(url.path)).resolve() if url.path else path
                count += 1
                if not target.is_relative_to(root) or not target.exists():
                    errors.append(f"{file}:{line_number}: missing target {reference}")
                elif url.fragment and target.suffix.lower() == ".md" and unquote(url.fragment) not in headings(target):
                    errors.append(f"{file}:{line_number}: missing heading {reference}")
    if errors:
        sys.exit("\n".join(errors))
    print(f"Checked {count} local Markdown links in {len(set(files))} files.")


if __name__ == "__main__":
    main()
