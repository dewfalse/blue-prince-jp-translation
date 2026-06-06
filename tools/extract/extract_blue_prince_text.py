#!/usr/bin/env python3
"""Extract candidate strings from a Blue Prince install using UnityPy."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Iterable

import UnityPy


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", required=True, type=Path, help="Blue Prince install directory")
    parser.add_argument("--out-dir", required=True, type=Path, help="Directory for extracted text files")
    return parser.parse_args()


def escape_text(value: str) -> str:
    return value.replace("\r\n", "\n").replace("\r", "\n").replace("\t", "\\t").replace("\n", "\\n")


def collect_strings(node: Any, path: str = "") -> Iterable[tuple[str, str]]:
    if isinstance(node, str):
        yield path, node
    elif isinstance(node, bytes):
        for encoding in ("utf-8", "utf-16", "latin1"):
            try:
                text = node.decode(encoding)
                if text:
                    yield path, text
                    break
            except UnicodeDecodeError:
                continue
    elif isinstance(node, dict):
        for key, value in node.items():
            child_path = f"{path}.{key}" if path else str(key)
            yield from collect_strings(value, child_path)
    elif isinstance(node, (list, tuple)):
        for index, value in enumerate(node):
            yield from collect_strings(value, f"{path}[{index}]")


def iter_unity_files(game_dir: Path) -> Iterable[Path]:
    data_dir = game_dir / "BLUE PRINCE_Data"
    direct_names = ["resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets"]
    for name in direct_names:
        path = data_dir / name
        if path.exists():
            yield path

    for path in sorted(data_dir.glob("level*")):
        if path.is_file() and not path.suffix:
            yield path

    bundle_dir = data_dir / "StreamingAssets" / "aa" / "StandaloneWindows64"
    if bundle_dir.exists():
        yield from sorted(bundle_dir.glob("*.bundle"))


def extract_file(path: Path, strings: dict[str, set[str]]) -> tuple[int, int]:
    env = UnityPy.load(str(path))
    ok = 0
    failed = 0
    seen = set()

    for asset_path, reader in env.container.items():
        try:
            tree = reader.read_typetree()
            for field_path, value in collect_strings(tree, asset_path):
                value = value.strip()
                if value:
                    strings.setdefault(value, set()).add(f"{path.name}::{field_path}")
            ok += 1
        except Exception:
            failed += 1

    for obj in env.objects:
        key = getattr(obj, "path_id", id(obj))
        if key in seen:
            continue
        seen.add(key)
        try:
            if obj.type.name == "TextAsset":
                data = obj.read()
                name = getattr(data, "name", "<textasset>")
                text = getattr(data, "text", None)
                if text is None:
                    text = getattr(data, "script", "")
                for field_path, value in collect_strings(text, name):
                    value = value.strip()
                    if value:
                        strings.setdefault(value, set()).add(f"{path.name}::{field_path}")
            else:
                tree = obj.read_typetree()
                name = tree.get("m_Name", obj.type.name) if isinstance(tree, dict) else obj.type.name
                for field_path, value in collect_strings(tree, str(name)):
                    value = value.strip()
                    if value:
                        strings.setdefault(value, set()).add(f"{path.name}::{field_path}")
            ok += 1
        except Exception:
            failed += 1

    return ok, failed


def main() -> None:
    args = parse_args()
    args.out_dir.mkdir(parents=True, exist_ok=True)

    strings: dict[str, set[str]] = {}
    for path in iter_unity_files(args.game_dir):
        print(f"Extracting {path}")
        try:
            ok, failed = extract_file(path, strings)
            print(f"  ok={ok} failed={failed}")
        except Exception as exc:
            print(f"  load failed: {exc}")

    raw_path = args.out_dir / "raw_texts.txt"
    detail_path = args.out_dir / "raw_texts_detail.json"

    with raw_path.open("w", encoding="utf-8", newline="\n") as handle:
        for value in sorted(strings, key=str.lower):
            handle.write(escape_text(value) + "\n")

    detail = {escape_text(value): sorted(sources) for value, sources in sorted(strings.items(), key=lambda item: item[0].lower())}
    detail_path.write_text(json.dumps(detail, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"Wrote {len(strings)} unique strings")
    print(f"  {raw_path}")
    print(f"  {detail_path}")


if __name__ == "__main__":
    main()
