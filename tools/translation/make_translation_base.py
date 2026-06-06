#!/usr/bin/env python3
"""Create blank TSV translation bases from extracted Blue Prince text."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any, Iterable


GUID_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}")
ASSET_RE = re.compile(r"(^|/|\\)(Assets|Packages|Library)(/|\\)", re.IGNORECASE)
ONLY_PUNCT_RE = re.compile(r"^[\s\d/\\._*#@!?%^&()[\]{}|<>=+~`'\":;,\-]+$")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+", type=Path, help="Input files or directories")
    parser.add_argument("--mode", choices=("game", "ui"), required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--existing", type=Path, help="Existing TSV to preserve translated rows")
    parser.add_argument("--min-length", type=int, default=3)
    return parser.parse_args()


def unescape_text(value: str) -> str:
    return value.replace("\\t", "\t").replace("\\n", "\n")


def escape_tsv(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n").replace("\t", "\\t").replace("\n", "\\n")


def iter_files(paths: Iterable[Path]) -> Iterable[Path]:
    for path in paths:
        if path.is_dir():
            yield from sorted(p for p in path.rglob("*") if p.is_file())
        elif path.is_file():
            yield path


def collect_json(node: Any) -> Iterable[str]:
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        for value in node.values():
            yield from collect_json(value)
    elif isinstance(node, list):
        for value in node:
            yield from collect_json(value)


def collect_plain(path: Path) -> Iterable[str]:
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.rstrip("\n")
            if line:
                yield unescape_text(line)


def collect_inputs(paths: Iterable[Path]) -> Iterable[str]:
    for path in iter_files(paths):
        if path.suffix.lower() == ".json":
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                yield from collect_json(data)
                continue
            except Exception:
                pass
        yield from collect_plain(path)


def useful_game_text(value: str, min_length: int) -> bool:
    stripped = value.strip()
    if len(stripped) < min_length:
        return False
    if GUID_RE.search(stripped) or ASSET_RE.search(stripped):
        return False
    if ONLY_PUNCT_RE.match(stripped):
        return False
    if any(ord(ch) < 0x20 and ch not in "\t\n\r" for ch in stripped):
        return False
    alpha = sum(1 for ch in stripped if ch.isalpha())
    return alpha >= 2


def read_existing(path: Path | None) -> dict[str, str]:
    rows: dict[str, str] = {}
    if path is None or not path.exists():
        return rows
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if not line.strip() or line.startswith("#"):
                continue
            key, sep, value = line.rstrip("\n").partition("\t")
            if sep:
                rows[key] = value
    return rows


def write_game_base(args: argparse.Namespace) -> None:
    existing = read_existing(args.existing)
    values = {
        escape_tsv(value.strip())
        for value in collect_inputs(args.inputs)
        if useful_game_text(value, args.min_length)
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write("# English text<TAB>Japanese text\n")
        handle.write("# Escape literal newlines and tabs as \\n and \\t.\n")
        for key in sorted(values, key=str.lower):
            handle.write(f"{key}\t{existing.get(key, '')}\n")


def write_ui_base(args: argparse.Namespace) -> None:
    existing = read_existing(args.existing)
    keys: set[str] = set()
    for path in iter_files(args.inputs):
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if not line.strip() or line.startswith("#"):
                    continue
                key, sep, _value = line.rstrip("\n").partition("\t")
                if sep and key:
                    keys.add(key)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write("# Localization key<TAB>Japanese text\n")
        for key in sorted(keys, key=str.lower):
            handle.write(f"{key}\t{existing.get(key, '')}\n")


def main() -> None:
    args = parse_args()
    if args.mode == "game":
        write_game_base(args)
    else:
        write_ui_base(args)
    print(f"Wrote {args.output}")


if __name__ == "__main__":
    main()
