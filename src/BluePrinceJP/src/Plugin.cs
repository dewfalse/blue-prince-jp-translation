using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using TMPro;
using UnityEngine.TextCore.LowLevel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppGenList = Il2CppSystem.Collections.Generic.List<string>;
using Il2CppTMPList = Il2CppSystem.Collections.Generic.List<TMPro.TMP_FontAsset>;
using Il2CppInterop.Runtime.Injection;

namespace BluePrinceJP
{
    internal static class MyPluginInfo
    {
        public const string PLUGIN_GUID = "com.blueprincejp.plugin";
        public const string PLUGIN_NAME = "BluePrinceJP";
        public const string PLUGIN_VERSION = "1.0.1";
    }

    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static Harmony Harmony;
        internal static string DataDir;

        // UI localization (lang_ja.tsv): key → Japanese
        internal static readonly System.Collections.Generic.Dictionary<string, string> JaTranslations = new();
        // Game content (game_ja.tsv): English text → Japanese text
        internal static readonly System.Collections.Generic.Dictionary<string, string> GameJaTranslations = new();

        // Lookup with TrimEnd fallback — handles game texts that carry trailing whitespace/newlines
        internal static bool TryGetGameTranslation(string key, out string ja)
        {
            if (GameJaTranslations.TryGetValue(key, out ja)) return true;
            string trimmed = key.TrimEnd();
            if (trimmed.Length < key.Length && GameJaTranslations.TryGetValue(trimmed, out ja)) return true;
            ja = null;
            return false;
        }
        internal static TMP_FontAsset JaFont;

        // --- Original(EN) / Japanese toggle ---
        internal static BepInEx.Configuration.ConfigEntry<KeyCode> ToggleKey;
        internal static bool ShowOriginal = false;
        internal class TextSwap
        {
            public TMP_Text comp; public string orig; public string ja;
            public TMP_FontAsset origFont; public Material origMat;
        }
        // Keyed by component InstanceID so re-translations overwrite instead of duplicating.
        internal static readonly Dictionary<int, TextSwap> Swaps = new();

        // Swap every recorded component between original English and Japanese, switching BOTH the
        // text and the font/material. English uses the game's own font (NotoSansJP is wider for Latin
        // and overflows). Runs inside the set_text guard so it doesn't recurse through our own patch.
        internal static void ApplySwaps()
        {
            TmpTextSetTextPatch.SetInPatch(true);
            try
            {
                var dead = new List<int>();
                foreach (var kv in Swaps)
                {
                    var s = kv.Value;
                    try
                    {
                        if (s.comp == null) { dead.Add(kv.Key); continue; }
                        if (ShowOriginal)
                        {
                            if (s.origFont != null) { s.comp.font = s.origFont; s.comp.fontSharedMaterial = s.origMat; }
                            s.comp.text = s.orig;
                        }
                        else
                        {
                            if (JaFont != null) { s.comp.font = JaFont; s.comp.fontSharedMaterial = JaFont.material; }
                            s.comp.text = s.ja;
                        }
                    }
                    catch { dead.Add(kv.Key); }
                }
                foreach (var d in dead) Swaps.Remove(d);
            }
            finally { TmpTextSetTextPatch.SetInPatch(false); }
        }

        // Collection: English texts seen via TMP that aren't yet translated
        internal static readonly System.Collections.Generic.HashSet<string> CollectedGameTexts = new();
        internal static bool CollectedDirty = false;

        // Original English texts shown on screen. Keep one log line per unique
        // string so the BepInEx console stays usable during normal play.
        internal static readonly System.Collections.Generic.HashSet<string> LoggedOriginalTexts = new();

        internal static void LogDisplayedOriginal(string source, string original)
        {
            if (string.IsNullOrEmpty(original)) return;
            string normalized = original
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\t", "\\t")
                .Replace("\n", "\\n");
            if (!LoggedOriginalTexts.Add(normalized)) return;
            Log.LogInfo($"[ORIGINAL:{source}] {normalized}");
        }

        internal static void CollectText(string s)
        {
            if (CollectedGameTexts.Contains(s)) return;
            CollectedGameTexts.Add(s);
            try { File.AppendAllText(Path.Combine(DataDir, "game_texts_en.txt"), s + "\n", Encoding.UTF8); }
            catch { }
        }

        public override void Load()
        {
            Log = base.Log;
            DataDir = Path.Combine(Paths.BepInExRootPath, "BluePrinceJP");
            Directory.CreateDirectory(DataDir);

            ToggleKey = Config.Bind("General", "ToggleOriginalTextKey", KeyCode.LeftControl,
                "原文(英語)と日本語訳を切り替えるトグルキー。押すたびに切り替わる。" +
                "Ctrl / Shift / Alt を指定した場合は左右どちらのキーでも有効。" +
                "(KeyCode names, e.g. LeftControl, Tab, BackQuote)");

            LoadTranslations();
            LoadGameTranslations();

            Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            Harmony.PatchAll();

            Log.LogInfo($"BluePrinceJP loaded — {JaTranslations.Count} UI, {GameJaTranslations.Count} game translations");

            // Attach a behaviour that runs delayed layout diagnostics once Unity has laid text out
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<BpjpRuntime>();
                var rtGo = new GameObject("BPJP_Runtime");
                UnityEngine.Object.DontDestroyOnLoad(rtGo);
                rtGo.hideFlags = HideFlags.HideAndDontSave;
                rtGo.AddComponent<BpjpRuntime>();
                Log.LogInfo("[JP] Runtime behaviour attached");
            }
            catch (Exception e) { Log.LogWarning($"[JP] Diag attach failed: {e.Message}"); }
        }

        private static void LoadTranslations()
        {
            string path = Path.Combine(DataDir, "lang_ja.tsv");
            if (!File.Exists(path)) { Log.LogWarning($"[JP] Translation file not found: {path}"); return; }
            int count = 0;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string key = line.Substring(0, tab).Trim();
                string val = line.Substring(tab + 1);
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                { JaTranslations[key] = val; count++; }
            }
            Log.LogInfo($"[JP] Loaded {count} translations from {path}");
        }

        private static void LoadGameTranslations()
        {
            string path = Path.Combine(DataDir, "game_ja.tsv");
            if (!File.Exists(path)) { Log.LogInfo("[JP] game_ja.tsv not found — game content collection mode active"); return; }
            int count = 0;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string en = line.Substring(0, tab).Replace("\\n", "\n").Replace("\\t", "\t");
                string ja = line.Substring(tab + 1).Replace("\\n", "\n").Replace("\\t", "\t");
                if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(ja))
                { GameJaTranslations[en] = ja; count++; }
            }
            Log.LogInfo($"[JP] Loaded {count} game content translations from {path}");
        }
    }

    // ============================================================
    // Patch: LocalizationManager.Translate(string key) — inject Japanese UI strings
    // ============================================================
    [HarmonyPatch(typeof(SummertimeMadness.Localization.LocalizationManager), "Translate", new Type[] { typeof(string) })]
    public static class TranslatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(string key, ref string __result)
        {
            if (Plugin.JaTranslations.TryGetValue(key, out string ja))
            {
                Plugin.LogDisplayedOriginal("UI", __result ?? key);
                __result = ja;
            }
        }
    }

    // ============================================================
    // Patch: TMP_Text.set_text — translate English→Japanese and swap font
    // ============================================================
    [HarmonyPatch(typeof(TMPro.TMP_Text), "set_text")]
    public static class TmpTextSetTextPatch
    {
        private static bool _inPatch = false;
        internal static bool IsInPatch => _inPatch;
        internal static void SetInPatch(bool v) => _inPatch = v;

        [HarmonyPostfix]
        public static void Postfix(TMPro.TMP_Text __instance, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_inPatch) return;
            ApplyTranslation(__instance, value);
        }

        // Called from OnEnable patches as well
        internal static void ApplyTranslation(TMPro.TMP_Text instance, string value = null)
        {
            if (_inPatch) return;
            try
            {
                if (value == null) value = instance.text;
                if (string.IsNullOrEmpty(value)) return;

                if (Plugin.JaFont == null) SceneLoadPatch.TryFindJaFont();

                // Ensure any font this text uses has JaFont in its fallback chain
                if (Plugin.JaFont != null && instance.font != null &&
                    instance.font.GetInstanceID() != Plugin.JaFont.GetInstanceID())
                    SceneLoadPatch.EnsureFallback(instance.font, Plugin.JaFont);

                if (Plugin.TryGetGameTranslation(value, out string ja))
                {
                    Plugin.LogDisplayedOriginal("TMP", value);
                    _inPatch = true;
                    try
                    {
                        // Skip the <size=80%> wrapper ONLY for strings that are already size-tagged
                        // (start with "<size=") or that carry an inline-icon reservation marker
                        // (a leading "<size=N> </size>", used by short room "Type" rows).
                        // A plain leading indent (e.g. the fruit letter body "        果物…") must NOT
                        // be excluded: it is long body text that needs the 80% reduction to avoid
                        // overflowing into fixed-position elements placed below it.
                        bool hasIconMarker = System.Text.RegularExpressions.Regex.IsMatch(ja, @"^\s*<size=[\d.]+> </size>");
                        bool skipWrap = ja.Length == 0 || ja.StartsWith("<size=") || hasIconMarker;
                        string jaText = skipWrap ? ja : $"<size=80%>{ja}</size>";

                        // Icon-bearing "Type" rows: after swapping to NotoSansJP (narrower space glyph)
                        // the reserved gap is too small and the first kanji overlaps the icon. Prepend
                        // one ideographic space (em-width, font-independent) to restore the clearance.
                        if (hasIconMarker)
                            jaText = "　" + jaText;

                        // Determine the component's ORIGINAL font/material so it can be restored when
                        // showing English (NotoSansJP is wider for Latin and overflows). If the font is
                        // already NotoSansJP (re-translation), inherit the original from the prior record.
                        int id = instance.GetInstanceID();
                        TMP_FontAsset origFont; Material origMat;
                        bool isJa = Plugin.JaFont != null && instance.font != null &&
                                    instance.font.GetInstanceID() == Plugin.JaFont.GetInstanceID();
                        if (!isJa && instance.font != null) { origFont = instance.font; origMat = instance.fontSharedMaterial; }
                        else if (Plugin.Swaps.TryGetValue(id, out var ex)) { origFont = ex.origFont; origMat = ex.origMat; }
                        else { origFont = instance.font; origMat = instance.fontSharedMaterial; }

                        if (Plugin.ShowOriginal)
                        {
                            // English mode: keep the game's own font, show original text
                            if (origFont != null) { instance.font = origFont; instance.fontSharedMaterial = origMat; }
                            instance.text = value;
                        }
                        else
                        {
                            // Japanese mode: swap font+material to NotoSansJP (material swap avoids tofu),
                            // force-add glyphs to the atlas, then set the translated text.
                            if (Plugin.JaFont != null && origFont != null &&
                                origFont.GetInstanceID() != Plugin.JaFont.GetInstanceID())
                            {
                                SceneLoadPatch.EnsureFallback(Plugin.JaFont, origFont);
                                instance.font = Plugin.JaFont;
                                instance.fontSharedMaterial = Plugin.JaFont.material;
                            }
                            if (Plugin.JaFont != null)
                                foreach (char c in jaText)
                                    if (c >= 0x2E80) Plugin.JaFont.HasCharacter(c, false, true);
                            instance.text = jaText;
                        }

                        Plugin.Swaps[id] = new Plugin.TextSwap
                        { comp = instance, orig = value, ja = jaText, origFont = origFont, origMat = origMat };
                    }
                    finally { _inPatch = false; }
                    return;
                }

                if (ShouldCollect(value))
                    Plugin.CollectText(value);
            }
            catch { }
        }

        internal static bool ShouldCollect(string s)
        {
            if (s.Length < 3) return false;
            if (Plugin.JaTranslations.ContainsKey(s)) return false;
            if (ContainsJapanese(s)) return false; // already translated
            if (s.StartsWith("/") || s.StartsWith("\\")) return false;
            if (s.IndexOf('\n') >= 0 && s.Length > 2000) return false;
            return true;
        }

        internal static bool ContainsJapanese(string s)
        {
            foreach (char c in s)
                if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xFF00 && c <= 0xFFEF))
                    return true;
            return false;
        }
    }

    // ============================================================
    // Patch: Languages.DoUpdate() — translate Languages.text after update
    // ============================================================
    [HarmonyPatch(typeof(BluePrince.Language.Languages), nameof(BluePrince.Language.Languages.DoUpdate))]
    public static class LanguagesDoUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(BluePrince.Language.Languages __instance)
        {
            // First call: dump the full language tables directly from this instance
            // (Resources.FindObjectsOfTypeAll misses AssetBundle-loaded SOs — __instance bypasses that)
            SceneLoadPatch.TryDumpLanguageTextInstance(__instance);

            try
            {
                string current = __instance.text;
                if (string.IsNullOrEmpty(current)) return;
                if (Plugin.TryGetGameTranslation(current, out string ja))
                {
                    Plugin.LogDisplayedOriginal("Language", current);
                    __instance.text = ja;
                }
                else if (TmpTextSetTextPatch.ShouldCollect(current))
                    Plugin.CollectText(current);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DoUpdate patch] {e.Message}");
            }
        }
    }

    // ============================================================
    // Scene load: find font, dump localization DB, dump LanguageText data
    // ============================================================
    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), "Internal_SceneLoaded")]
    public static class SceneLoadPatch
    {
        private static bool _dbDumped = false;
        private static bool _langTextDumped = false;

        [HarmonyPostfix]
        public static void Postfix(UnityEngine.SceneManagement.Scene scene)
        {
            Plugin.Log.LogInfo($"[SCENE] Loaded: {scene.name}");

            if (Plugin.JaFont == null)
                InitializeJaFont();

            // Dump LocalizationManager DB (UI strings)
            if (!_dbDumped)
                TryDumpLocalizationDB();

            // Dump LanguageText data (game content) — try on every scene until it succeeds
            if (!_langTextDumped)
                TryDumpLanguageText();
        }

        public static void TryFindJaFont() => InitializeJaFont();

        private static bool _jaFontInitialized = false;
        private static void InitializeJaFont()
        {
            if (_jaFontInitialized) return;
            _jaFontInitialized = true;
            try
            {
                // Diagnostic: log all TMP_FontAssets currently in memory
                var preloadedFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                Plugin.Log.LogInfo($"[JP] TMP_FontAssets in memory ({preloadedFonts.Length}):");
                foreach (var f in preloadedFonts)
                    if (f != null) Plugin.Log.LogInfo($"[JP]   '{f.name}'");

                // --- Approach: CreateFontAsset from extracted TTF (same as Caves of Qud JP MOD) ---
                string ttfPath = Path.Combine(Plugin.DataDir, "NotoSansJP-Medium.ttf");
                if (!File.Exists(ttfPath))
                {
                    Plugin.Log.LogWarning($"[JP] TTF not found: {ttfPath}");
                    return;
                }

                Plugin.Log.LogInfo($"[JP] Creating TMP_FontAsset from: {ttfPath}");
                // samplingPointSize=48: good SDF quality, ~2200 JP glyphs fit in one 4096² page.
                // (Pre-population reports notAdded=30 — those are codepoints simply absent from the
                //  TTF, not a capacity issue. Render size is controlled by fontSize × <size=80%>.)
                var jaFont = TMP_FontAsset.CreateFontAsset(
                    ttfPath, 0, 48, 5, GlyphRenderMode.SDFAA, 4096, 4096);

                if (jaFont == null)
                {
                    Plugin.Log.LogWarning("[JP] CreateFontAsset returned null — FontEngine may be stripped");
                    return;
                }

                Plugin.Log.LogInfo($"[JP] CreateFontAsset success: {jaFont.name}, pointSize={jaFont.faceInfo.pointSize}");
                jaFont.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                jaFont.isMultiAtlasTexturesEnabled = true;

                // Pre-populate ALL translation glyphs NOW, at init time (long before any rendering).
                // Diagnostic proved: set_text-time HasCharacter(tryAdd) registers the glyph in the
                // CPU lookup table immediately (行inAtlas=True) but the GPU texture Apply() lags behind
                // the mesh draw, so the FIRST component to use a glyph renders tofu and never re-renders.
                // Adding everything here guarantees the GPU atlas is fully uploaded before any draw.
                PrePopulateAtlas(jaFont);

                Plugin.JaFont = jaFont;

                // --- Register as global fallback on all existing TMP font assets ---
                RegisterAsGlobalFallback(jaFont);

                Plugin.Log.LogInfo("[JP] Japanese font initialized and registered as global fallback");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[JP] InitializeJaFont error: {e.Message}\n{e.StackTrace}");
            }
        }

        private static void PrePopulateAtlas(TMP_FontAsset font)
        {
            try
            {
                var uniqueChars = new HashSet<char>();
                // All CJK/symbol chars used in actual translations
                foreach (var val in Plugin.GameJaTranslations.Values)
                    foreach (char c in val)
                        if (c >= 0x2E80) uniqueChars.Add(c);
                // Full hiragana + katakana blocks (U+3040–U+30FF)
                for (char c = '぀'; c <= 'ヿ'; c++) uniqueChars.Add(c);
                // Fullwidth/halfwidth forms (U+FF00–U+FFEF)
                for (char c = '＀'; c <= '￯'; c++) uniqueChars.Add(c);

                var arr = new char[uniqueChars.Count];
                int idx = 0;
                foreach (var c in uniqueChars) arr[idx++] = c;
                string preload = new string(arr);

                string missing;
                bool ok = font.TryAddCharacters(preload, out missing, false);
                // Force-flush every atlas page's pixels to its GPU texture
                var pages = font.atlasTextures;
                if (pages != null)
                    for (int p = 0; p < pages.Length; p++)
                        if (pages[p] != null) pages[p].Apply(false, false);
                int missingCount = string.IsNullOrEmpty(missing) ? 0 : missing.Length;
                Plugin.Log.LogInfo($"[JP] Pre-populated {uniqueChars.Count} chars: success={ok}, atlasPages={font.atlasTextureCount}, notAdded={missingCount}");
                if (missingCount > 0)
                    Plugin.Log.LogWarning($"[JP] Chars not added to atlas (capacity?): {missing.Substring(0, Math.Min(40, missingCount))}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[JP] PrePopulateAtlas error: {e.Message}"); }
        }

        private static void RegisterAsGlobalFallback(TMP_FontAsset jaFont)
        {
            try
            {
                // Add to TMP_Settings global fallback list
                int jaFontId = jaFont.GetInstanceID();
                if (TMP_Settings.fallbackFontAssets == null)
                    TMP_Settings.fallbackFontAssets = new Il2CppTMPList();
                var globalFallbacks = TMP_Settings.fallbackFontAssets;
                bool alreadyInGlobal = false;
                for (int i = 0; i < globalFallbacks.Count; i++)
                    if (globalFallbacks[i] != null && globalFallbacks[i].GetInstanceID() == jaFontId) { alreadyInGlobal = true; break; }
                if (!alreadyInGlobal)
                {
                    globalFallbacks.Insert(0, jaFont);
                    // Verify the insert actually worked
                    var verify = TMP_Settings.fallbackFontAssets;
                    bool verifyOk = false;
                    for (int i = 0; i < verify.Count; i++)
                        if (verify[i]?.GetInstanceID() == jaFontId) { verifyOk = true; break; }
                    Plugin.Log.LogInfo($"[JP] Added to TMP_Settings.fallbackFontAssets (verified={verifyOk}, count={verify.Count})");
                }

                // Add as fallback to every currently-loaded TMP_FontAsset
                int patched = 0;
                var existingFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (var f in existingFonts)
                {
                    if (f == null || f.GetInstanceID() == jaFontId) continue;
                    if (EnsureFallback(f, jaFont)) patched++;
                }
                Plugin.Log.LogInfo($"[JP] Added fallback to {patched}/{existingFonts.Length} existing font assets");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[JP] RegisterAsGlobalFallback error: {e.Message}"); }
        }

        internal static bool EnsureFallback(TMP_FontAsset target, TMP_FontAsset fallback)
        {
            try
            {
                int fallbackId = fallback.GetInstanceID();
                if (target.fallbackFontAssetTable == null)
                    target.fallbackFontAssetTable = new Il2CppTMPList();
                var list = target.fallbackFontAssetTable;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].GetInstanceID() == fallbackId) return false;
                list.Insert(0, fallback);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[JP] EnsureFallback failed for '{target?.name}': {ex.Message}");
                return false;
            }
        }

        private static void TryDumpLocalizationDB()
        {
            try
            {
                var locMgr = UnityEngine.Object.FindObjectOfType<SummertimeMadness.Localization.LocalizationManager>();
                if (locMgr == null) return;
                var db = locMgr.TargetLocalization;
                if (db == null) return;
                _dbDumped = true;
                DumpLocalizationDatabase(db);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LOC] DB dump error: {e.Message}"); }
        }

        private static void DumpLocalizationDatabase(SummertimeMadness.Localization.LocalizationDatabaseAsset db)
        {
            var keyDb = db.Keys;
            if (keyDb?.Keys != null)
            {
                var keys = keyDb.Keys;
                var sb = new StringBuilder();
                for (int i = 0; i < keys.Count; i++) sb.AppendLine(keys[i]);
                File.WriteAllText(Path.Combine(Plugin.DataDir, "all_loc_keys.txt"), sb.ToString(), Encoding.UTF8);
            }

            var languages = db.Languages;
            if (languages == null) return;
            Plugin.Log.LogInfo($"[LOC] Languages: {languages.Count}");
            for (int li = 0; li < languages.Count; li++)
            {
                var lang = languages[li];
                if (lang == null) continue;
                Plugin.Log.LogInfo($"[LOC]   [{li}] tag={lang.LanguageTag} name={lang.LocalizedName} enabled={lang.IsEnabled}");
                var entries = lang.Entries;
                if (entries == null) continue;
                var sb = new StringBuilder();
                sb.AppendLine($"# Language: {lang.LanguageTag} ({lang.LocalizedName})");
                for (int ei = 0; ei < entries.Count; ei++)
                {
                    var e = entries[ei];
                    if (e != null) sb.AppendLine($"{e.Key}\t{e.Value}");
                }
                File.WriteAllText(Path.Combine(Plugin.DataDir, $"lang_{lang.LanguageTag}.txt"), sb.ToString(), Encoding.UTF8);
            }
        }

        private static void TryDumpLanguageText()
        {
            try
            {
                var allLangs = Resources.FindObjectsOfTypeAll<BluePrince.Language.Languages>();
                Plugin.Log.LogInfo($"[LANGTEXT] FindObjectsOfTypeAll: {allLangs.Length} (AssetBundle SOs not included)");
                if (allLangs.Length == 0) return;
                foreach (var l in allLangs)
                    TryDumpLanguageTextInstance(l);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LANGTEXT] Dump error: {e.Message}"); }
        }

        // Called from LanguagesDoUpdatePatch — receives the live instance directly,
        // bypassing the FindObjectsOfTypeAll limitation for AssetBundle-loaded SOs.
        public static void TryDumpLanguageTextInstance(BluePrince.Language.Languages instance)
        {
            if (_langTextDumped) return;
            try
            {
                if (instance == null || instance.languages == null) return;
                var langsList = instance.languages;
                if (langsList.Count == 0) return;

                // Require at least some room data before committing the dump
                var lt0 = langsList[0];
                if (lt0 == null) return;
                int dataCount = (lt0.bd_UpgradeNames?.Count ?? 0) + (lt0.dirigiblocksText?.Count ?? 0);
                if (dataCount == 0) return;

                _langTextDumped = true;
                Plugin.Log.LogInfo($"[LANGTEXT] Dumping via DoUpdate instance: {langsList.Count} language(s), {dataCount}+ strings");

                for (int li = 0; li < langsList.Count; li++)
                {
                    var lt = langsList[li];
                    if (lt == null) continue;

                    var sb = new StringBuilder();
                    sb.AppendLine($"# Languages[{li}]");
                    DumpStringList(sb, "bd_UpgradeNames", lt.bd_UpgradeNames);
                    DumpStringList(sb, "bd_UpgradeDescriptions", lt.bd_UpgradeDescriptions);
                    DumpStringList(sb, "bd_UpgradedDescriptions", lt.bd_UpgradedDescriptions);
                    DumpStringListKeyed(sb, "dirigiblocksText", lt.dirigiblocksTextRef, lt.dirigiblocksText);

                    // Feed every string into game_texts_en.txt for translation reference
                    CollectFromList(lt.bd_UpgradeNames);
                    CollectFromList(lt.bd_UpgradeDescriptions);
                    CollectFromList(lt.bd_UpgradedDescriptions);
                    CollectFromList(lt.dirigiblocksText);

                    string filename = $"langtext_{li}.txt";
                    File.WriteAllText(Path.Combine(Plugin.DataDir, filename), sb.ToString(), Encoding.UTF8);
                    Plugin.Log.LogInfo($"[LANGTEXT] Wrote {filename}");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[LANGTEXT instance] {e.Message}"); }
        }

        private static void CollectFromList(Il2CppGenList list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string s = list[i];
                if (!string.IsNullOrEmpty(s) && TmpTextSetTextPatch.ShouldCollect(s))
                    Plugin.CollectText(s);
            }
        }

        private static void DumpStringList(StringBuilder sb, string label, Il2CppGenList list)
        {
            if (list == null) return;
            sb.AppendLine($"## {label} ({list.Count} entries)");
            for (int i = 0; i < list.Count; i++)
                sb.AppendLine($"{i}\t{list[i]}");
        }

        private static void DumpStringListKeyed(StringBuilder sb, string label, Il2CppGenList keys, Il2CppGenList values)
        {
            if (values == null) return;
            sb.AppendLine($"## {label} ({values.Count} entries)");
            int kCount = keys?.Count ?? 0;
            for (int i = 0; i < values.Count; i++)
            {
                string key = (i < kCount) ? keys[i] : i.ToString();
                sb.AppendLine($"{key}\t{values[i]}");
            }
        }
    }

    // ============================================================
    // Patch: TMP_Text.SetText(string) — alternative text assignment method
    // Note: uses postfix to avoid IL2CPP parameter-name mismatch in prefix
    // ============================================================
    [HarmonyPatch(typeof(TMPro.TMP_Text), "SetText", new Type[] { typeof(string) })]
    public static class TmpTextSetTextMethodPatch
    {
        [HarmonyPostfix]
        public static void Postfix(TMPro.TMP_Text __instance)
            => TmpTextSetTextPatch.ApplyTranslation(__instance);
    }

    // ============================================================
    // Patch: TextMeshPro.OnEnable + TextMeshProUGUI.OnEnable
    // Intercepts text that was set via Unity prefab serialization
    // (serialization writes m_text directly, bypassing set_text)
    // ============================================================
    [HarmonyPatch(typeof(TMPro.TextMeshPro), "OnEnable")]
    public static class TextMeshProOnEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(TMPro.TextMeshPro __instance)
            => TmpTextSetTextPatch.ApplyTranslation(__instance);
    }

    [HarmonyPatch(typeof(TMPro.TextMeshProUGUI), "OnEnable")]
    public static class TextMeshProUGUIOnEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(TMPro.TextMeshProUGUI __instance)
            => TmpTextSetTextPatch.ApplyTranslation(__instance);
    }

    // ============================================================
    // Patch: TMP_Text.set_font — detect/block game resetting font on Japanese-text components
    // ============================================================
    [HarmonyPatch(typeof(TMPro.TMP_Text), "set_font")]
    public static class TmpTextSetFontPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(TMPro.TMP_Text __instance, TMPro.TMP_FontAsset value)
        {
            if (Plugin.JaFont == null) return true;
            // Allow if we're inside our own translation patch (avoid infinite loop)
            if (TmpTextSetTextPatch.IsInPatch) return true;
            // Allow if setting to our own JaFont
            if (value != null && value.GetInstanceID() == Plugin.JaFont.GetInstanceID()) return true;

            string currentText = __instance.text;
            if (!string.IsNullOrEmpty(currentText) && TmpTextSetTextPatch.ContainsJapanese(currentText))
            {
                // Game is trying to change font on a component showing Japanese text — block it
                Plugin.Log.LogInfo($"[FONT-RESET] Blocked: '{value?.name}' on Japanese component (keeping NotoSansJP)");
                return false; // suppress the font change
            }
            return true;
        }
    }

    // ============================================================
    // Patch: BluePrince.Language.Languages.GetText() — log and intercept
    // ============================================================
    [HarmonyPatch(typeof(BluePrince.Language.Languages), nameof(BluePrince.Language.Languages.GetText))]
    public static class GetTextPatch
    {
        private static readonly System.Collections.Generic.HashSet<string> Logged = new();

        [HarmonyPostfix]
        public static void Postfix(string reference, BluePrince.LanguageGroup languageGroup, ref string __result)
        {
            string k = $"{languageGroup}:{reference}";
            if (!Logged.Contains(k))
            {
                Logged.Add(k);
                Plugin.Log.LogInfo($"[GETTEXT] [{languageGroup}] {reference} => {(__result != null ? __result.Substring(0, Math.Min(60, __result.Length)) : "null")}");
            }
            // Try to translate the result; otherwise collect it
            if (__result != null && Plugin.TryGetGameTranslation(__result, out string ja))
            {
                Plugin.LogDisplayedOriginal("GetText", __result);
                __result = ja;
            }
            else if (__result != null && TmpTextSetTextPatch.ShouldCollect(__result))
                Plugin.CollectText(__result);
        }
    }

    // ============================================================
    // Delayed diagnostics behaviour: logs a queued text component's layout once
    // Unity has actually laid it out (rect becomes non-zero), or after a timeout.
    // ============================================================
    // ============================================================
    // Runtime behaviour: toggles all translated text between Japanese and
    // original English when the configured key is pressed (toggle, not hold).
    // ============================================================
    public class BpjpRuntime : MonoBehaviour
    {
        public BpjpRuntime(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            try
            {
                if (IsToggleDown())
                {
                    Plugin.ShowOriginal = !Plugin.ShowOriginal;
                    Plugin.ApplySwaps();
                    Plugin.Log.LogInfo($"[TOGGLE] ShowOriginal={Plugin.ShowOriginal} ({Plugin.Swaps.Count} entries)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TOGGLE] {e.Message}"); }
        }

        private static bool IsToggleDown()
        {
            if (Plugin.ToggleKey == null) return false;
            var k = Plugin.ToggleKey.Value;
            // Ctrl / Shift / Alt: accept either left or right variant.
            if (k == KeyCode.LeftControl || k == KeyCode.RightControl)
                return Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl);
            if (k == KeyCode.LeftShift || k == KeyCode.RightShift)
                return Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
            if (k == KeyCode.LeftAlt || k == KeyCode.RightAlt)
                return Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt);
            return Input.GetKeyDown(k);
        }
    }
}
