using MelonLoader;
using HarmonyLib;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

[assembly: MelonInfo(typeof(NeverGraveRussian.MyMod), "Never Grave Russian", "1.2.0", "Ziegmaster")]
[assembly: MelonGame(null, null)]

namespace NeverGraveRussian
{
    public class MyMod : MelonMod
    {
        private static Dictionary<string, string> TextTranslations = new Dictionary<string, string>();
        private static readonly string JsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "NeverGraveRussian.json");
        private static readonly string UntranslatedJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "NeverGraveUntranslated.json");

        private class WildcardEntry
        {
            public Regex Regex { get; set; } = null!;
            public string Translation { get; set; } = string.Empty;
        }
        private static List<WildcardEntry> wildcardCache = new List<WildcardEntry>();

        private static readonly Regex SmartRegex = new Regex(@"<[^>]+>|\{[^}]+\}|\d+", RegexOptions.Compiled);
        private static readonly Regex HasEnglishLetters = new Regex(@"[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex AutoSizePattern = new Regex(@"\[AUTO_SIZE:?.*?\]\]|\[AUTO_SIZE\]", RegexOptions.Compiled);
        private static readonly Regex AlterWidthPattern = new Regex(@"\[ALTER_WIDTH:.*?\]\]", RegexOptions.Compiled);
        private static readonly Regex MarginPattern = new Regex(@"\[MARGIN_(?<side>LEFT|RIGHT|UP|DOWN):.*?\]\]", RegexOptions.Compiled);
        private static readonly Regex ExtractPath = new Regex(@"\[PATH=(?<val>[^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex ExtractDepth = new Regex(@"\[DEPTH=(?<val>\d+)\]", RegexOptions.Compiled);
        private static readonly Regex ExtractDelta = new Regex(@"\[DELTA=(?<val>[\+\-]\d+)\]", RegexOptions.Compiled);
        private static readonly Regex ExtractTarget = new Regex(@"\[TARGET=(?<val>[^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex DebugBlockPattern = new Regex(@"\[DEBUG(\[[^\]]*\])*\]", RegexOptions.Compiled);
        private static readonly object FileLock = new object();

        public override void OnInitializeMelon()
        {
            LoadTranslations();
            var harmony = HarmonyInstance;
            try
            {
                Type? tmpType = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    tmpType = assembly.GetType("TMPro.TMP_Text") ?? assembly.GetType("Il2CppTMPro.TMP_Text");
                    if (tmpType != null) break;
                }
                if (tmpType != null)
                {
                    var prefix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(UniversalPrefix)));
                    var textSetter = tmpType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                    if (textSetter != null) harmony.Patch(textSetter, prefix: prefix);
                    var methods = tmpType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name == "SetText" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string))
                        {
                            harmony.Patch(m, prefix: prefix);
                        }
                    }
                }
            }
            catch { }
        }

        public static void UniversalPrefix(object? __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0) || __0.Length < 2) return;
            string cleanValue = __0.Replace('\u00A0', ' ').Trim();
            string keyForDict = cleanValue;
            string[]? dynamicParts = null;
            var matches = SmartRegex.Matches(cleanValue);
            if (matches.Count > 0)
            {
                string template = cleanValue;
                dynamicParts = new string[matches.Count];
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    var m = matches[i];
                    dynamicParts[i] = m.Value;
                    template = template.Remove(m.Index, m.Length).Insert(m.Index, $"{{{i}}}");
                }
                keyForDict = template;
            }

            string? translated = FindTranslationWithWildcard(keyForDict, out List<string>? capturedValues);

            if (translated != null && !string.IsNullOrEmpty(translated))
            {
                string result = translated;

                if (capturedValues != null && capturedValues.Count > 0)
                {
                    int wildcardIndex = 0;
                    result = Regex.Replace(result, @"\{WILDCARD\}", m =>
                    {
                        if (wildcardIndex < capturedValues.Count)
                            return capturedValues[wildcardIndex++];
                        else
                            return m.Value;
                    });
                }

                if (__instance is MonoBehaviour comp)
                {
                    var asMatches = AutoSizePattern.Matches(result);
                    foreach (Match m in asMatches)
                    {
                        string path = ExtractPath.Match(m.Value).Groups["val"].Value;
                        if (string.IsNullOrEmpty(path) || CheckHierarchyPath(comp, path)) ForceTextAutoSize(comp);
                        result = result.Replace(m.Value, "");
                    }
                    var awMatches = AlterWidthPattern.Matches(result);
                    foreach (Match m in awMatches)
                    {
                        string b = m.Value;
                        string path = ExtractPath.Match(b).Groups["val"].Value;
                        int depth = int.TryParse(ExtractDepth.Match(b).Groups["val"].Value, out int d) ? d : 0;
                        if (float.TryParse(ExtractDelta.Match(b).Groups["val"].Value, out float delta))
                            if (string.IsNullOrEmpty(path) || CheckHierarchyPath(comp, path)) AdjustHierarchyWidthCascade(comp, depth, delta);
                        result = result.Replace(b, "");
                    }
                    var mMatches = MarginPattern.Matches(result);
                    foreach (Match m in mMatches)
                    {
                        string side = m.Groups["side"].Value;
                        string path = ExtractPath.Match(m.Value).Groups["val"].Value;
                        string target = ExtractTarget.Match(m.Value).Groups["val"].Value;
                        if (float.TryParse(ExtractDelta.Match(m.Value).Groups["val"].Value, out float delta))
                        {
                            ApplyMarginLogic(comp, path, target, side, delta);
                        }
                        result = result.Replace(m.Value, "");
                    }

                    var debugMatches = DebugBlockPattern.Matches(result);
                    foreach (Match debugMatch in debugMatches)
                    {
                        string debugBlock = debugMatch.Value;
                        if (debugBlock.Contains("[PATH]"))
                            LogHierarchyPath(comp);
                        if (debugBlock.Contains("[KEY]"))
                            MelonLogger.Msg($"[DEBUG_KEY] Key for dictionary: {keyForDict}");
                        if (debugBlock.Contains("[STRING]"))
                            MelonLogger.Msg($"[DEBUG_STRING] Original string before processing: {__0}");
                    }
                    if (debugMatches.Count > 0)
                        result = DebugBlockPattern.Replace(result, "");
                }

                try
                {
                    __0 = (dynamicParts != null) ? string.Format(result, dynamicParts) : result;
                }
                catch
                {
                    __0 = result;
                }
            }
            else if (!TextTranslations.ContainsKey(keyForDict) && HasEnglishLetters.IsMatch(cleanValue))
            {
                AddNewKeyToJson(keyForDict);
            }
        }

        private static string? FindTranslationWithWildcard(string inputKey, out List<string>? capturedValues)
        {
            capturedValues = null;
            if (TextTranslations.TryGetValue(inputKey, out string? exactTranslation))
            {
                return exactTranslation;
            }

            foreach (var entry in wildcardCache)
            {
                var match = entry.Regex.Match(inputKey);
                if (match.Success)
                {
                    capturedValues = new List<string>();
                    for (int i = 1; i < match.Groups.Count; i++)
                        capturedValues.Add(match.Groups[i].Value);
                    return entry.Translation;
                }
            }
            return null;
        }

        private static void ApplyMarginLogic(MonoBehaviour comp, string path, string targetType, string side, float delta)
        {
            try {
                Transform? found = string.IsNullOrEmpty(path) ? null : GetTargetFromPath(comp, path);
                if (!string.IsNullOrEmpty(path) && found == null) return;
                Transform target = (targetType == "PARENT" && found != null) ? found : comp.transform;
                var rect = target.GetComponent<RectTransform>();
                if (rect == null) return;
                Vector2 pos = rect.anchoredPosition;
                switch (side)
                {
                    case "LEFT": pos.x += delta; break;
                    case "RIGHT": pos.x -= delta; break;
                    case "UP": pos.y += delta; break;
                    case "DOWN": pos.y -= delta; break;
                }
                rect.anchoredPosition = pos;
            } catch { }
        }

        private static Transform? GetTargetFromPath(MonoBehaviour? comp, string path)
        {
            if (comp == null) return null;
            string[] names = path.Split('>');
            Transform curr = comp.transform;
            int found = 0; Transform? lastMatch = null;
            while (curr != null && found < names.Length)
            {
                if (curr.gameObject.name.Contains(names[found].Trim())) { lastMatch = curr; found++; }
                curr = curr.parent;
            }
            return (found == names.Length) ? lastMatch : null;
        }

        private static bool CheckHierarchyPath(MonoBehaviour comp, string path) => GetTargetFromPath(comp, path) != null;

        private static void LogHierarchyPath(MonoBehaviour comp)
        {
            List<string> p = new List<string> { comp.gameObject.name };
            Transform? c = comp.transform.parent;
            while (c != null)
            {
                p.Add(c.gameObject.name);
                c = c.parent;
            }
            MelonLogger.Msg($"[DEBUG_PATH] {string.Join(">", p)}");
        }

        private static void ForceTextAutoSize(MonoBehaviour comp)
        {
            var t = comp.GetType();
            t.GetProperty("enableAutoSizing")?.SetValue(comp, true);
            t.GetProperty("fontSizeMin")?.SetValue(comp, 6f);
        }

        private static void AdjustHierarchyWidthCascade(MonoBehaviour comp, int depth, float delta)
        {
            var r = comp.GetComponent<RectTransform>();
            if (r != null) r.sizeDelta = new Vector2(r.sizeDelta.x + delta, r.sizeDelta.y);
            Transform curr = comp.transform;
            for (int i = 0; i < depth; i++)
            {
                if (curr.parent == null) break;
                curr = curr.parent;
                var pr = curr.GetComponent<RectTransform>();
                if (pr != null) pr.sizeDelta = new Vector2(pr.sizeDelta.x + delta, pr.sizeDelta.y);
            }
        }

        private static void AddNewKeyToJson(string key)
        {
            lock (FileLock)
            {
                Dictionary<string, string> untranslated = new Dictionary<string, string>();
                if (File.Exists(UntranslatedJsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(UntranslatedJsonPath);
                        untranslated = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    }
                    catch { }
                }
                if (!untranslated.ContainsKey(key))
                {
                    untranslated[key] = "";
                    try
                    {
                        var opt = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        };
                        File.WriteAllText(UntranslatedJsonPath, JsonSerializer.Serialize(untranslated, opt));
                    }
                    catch { }
                }
            }
        }

        private void LoadTranslations()
        {
            if (File.Exists(JsonPath))
                try { TextTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(JsonPath)) ?? new Dictionary<string, string>(); } catch { }
            TextTranslations ??= new Dictionary<string, string>();
            BuildWildcardCache();
        }

        private void BuildWildcardCache()
        {
            wildcardCache.Clear();
            foreach (var kvp in TextTranslations)
            {
                if (kvp.Key.Contains("{WILDCARD}"))
                {
                    string tempKey = kvp.Key.Replace("{WILDCARD}", "@@WILDCARD@@");
                    string escaped = Regex.Escape(tempKey);
                    string pattern = "^" + escaped.Replace("@@WILDCARD@@", "(.*?)") + "$";
                    try
                    {
                        var regex = new Regex(pattern, RegexOptions.Compiled);
                        wildcardCache.Add(new WildcardEntry { Regex = regex, Translation = kvp.Value });
                    }
                    catch { }
                }
            }
        }
    }
}