# Translation Workflow

## Game Text

1. Run `tools/extract/extract_blue_prince_text.py` against a local Blue Prince install.
2. Use `tools/translation/make_translation_base.py --mode game` to create a blank `game_ja.tsv`.
3. Fill the second TSV column with Japanese translations. Keep literal newlines and tabs escaped as `\n` and `\t`.

## UI Text

1. Install the mod locally and start the game once.
2. The plugin writes the English UI table to `BepInEx/BluePrinceJP/lang_en.txt`.
3. Use `tools/translation/make_translation_base.py --mode ui` to create a blank `lang_ja.tsv`.
4. Keep the key column unchanged and fill the second TSV column with Japanese translations.

## Release Check

The release archive should include only runtime files needed by users:

- BepInEx loader/runtime files
- `BepInEx/plugins/BluePrinceJP/BluePrinceJP.dll`
- `BepInEx/BluePrinceJP/NotoSansJP-Medium.ttf`
- `BepInEx/BluePrinceJP/lang_ja.tsv`
- `BepInEx/BluePrinceJP/game_ja.tsv`
- license files and third-party notices for bundled dependencies

Do not include game files or BepInEx generated `interop` / `unity-libs` directories.
