using MelonLoader;
using HarmonyLib;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System;
using System.Net.Http;
using System.Threading.Tasks;

[assembly: MelonInfo(typeof(NeverGraveRussian.MyMod), "Never Grave Russian", "1.7.0", "Ziegmaster")]
[assembly: MelonGame(null, null)]

namespace NeverGraveRussian
{
    public class MyMod : MelonMod
    {
        // Словари и пути к файлам локализации
        private static Dictionary<string, string> TextTranslations = new Dictionary<string, string>();
        private static bool isUpdateAvailable = false;
        private static readonly string JsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "NeverGraveRussian.json");
        private static string UntranslatedJsonPath = string.Empty;
        private static string CurrentVersion = "1.0.0";
        private static Dictionary<string, string> UntranslatedCache = new Dictionary<string, string>();
        private static bool HasLoadedUntranslated = false;

        // Структура для хранения шаблонов с {WILDCARD}
        // Используется для переводов, которые содержат динамические части, не поддающиеся простой нормализации.
        private class WildcardEntry
        {
            public Regex Regex { get; set; } = null!;
            public string Translation { get; set; } = string.Empty;
        }
        private static List<WildcardEntry> wildcardCache = new List<WildcardEntry>();

        // Регулярные выражения для извлечения тегов, чисел и плейсхолдеров
        private static readonly Regex SmartRegex = new Regex(@"<[^>]+>|\{[^}]+\}|\d+", RegexOptions.Compiled);
        private static readonly Regex HasEnglishLetters = new Regex(@"[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex HasCyrillic = new Regex(@"[А-Яа-яЁё]", RegexOptions.Compiled);
        private static readonly object FileLock = new object();
        private static HashSet<int> patchedComponents = new HashSet<int>();
        
        private class OriginalRectData {
            public float localY;
            public float height;
            public float width;
        }
        private static Dictionary<int, OriginalRectData> originalRects = new Dictionary<int, OriginalRectData>();

        // Кэш для предотвращения повторной настройки одних и тех же шрифтов
        private static HashSet<int> _patchedFonts = new HashSet<int>();

        /// <summary>
        /// Расширяет размер текстур атласа шрифтов для поддержки большого количества символов.
        /// Фикс: Позволяет встроенному шрифту рендерить русский язык без появления "квадратов" (переполнение атласа).
        /// </summary>
        public static void EnsureFontAtlasCapacity(object? __instance)
        {
            if (__instance == null) return;

            try
            {
                Type type = __instance.GetType();
                var fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                if (fontProp == null) return;

                object? originalFont = fontProp.GetValue(__instance);
                if (originalFont == null) return;

                int fontId = originalFont.GetHashCode();
                if (_patchedFonts.Contains(fontId)) return;

                // Увеличиваем размер текстуры и включаем поддержку мультиатласов для основного шрифта
                PatchFontCapacity(originalFont);

                Type fontType = originalFont.GetType();
                var fallbackTableProp = fontType.GetProperty("fallbackFontAssetTable", BindingFlags.Public | BindingFlags.Instance);
                if (fallbackTableProp != null)
                {
                    object? fallbackList = fallbackTableProp.GetValue(originalFont);
                    if (fallbackList != null)
                    {
                        var countProp = fallbackList.GetType().GetProperty("Count");
                        var itemProp = fallbackList.GetType().GetProperty("Item");
                        
                        if (countProp != null && itemProp != null)
                        {
                            object? countObj = countProp.GetValue(fallbackList);
                            int count = countObj != null ? (int)countObj : 0;
                            for (int i = 0; i < count; i++)
                            {
                                object? fallback = itemProp.GetValue(fallbackList, new object[] { i });
                                if (fallback != null) PatchFontCapacity(fallback);
                            }
                        }
                    }
                }
                
                // Заставляем текстовый компонент обновить геометрию (важно для немедленного исчезновения квадратов)
                var setAllDirty = type.GetMethod("SetAllDirty");
                if (setAllDirty != null) setAllDirty.Invoke(__instance, null);

                _patchedFonts.Add(fontId);
            }
            catch { }
        }

        /// <summary>
        /// Включает Dynamic модификацию атласа для шрифта и поддержку дополнительных атласов (MultiAtlas).
        /// Оставляет оригинальные закэшированные символы (цифры, латиницу) нетронутыми,
        /// что предотвращает потерю стилей, жирности, раскраски шрифтов и зависания при переходе между сценами.
        /// </summary>
        private static void PatchFontCapacity(object fontObj)
        {
            try
            {
                Type fontType = fontObj.GetType();

                var atlasPopProp = fontType.GetProperty("atlasPopulationMode", BindingFlags.Public | BindingFlags.Instance);
                if (atlasPopProp != null) atlasPopProp.SetValue(fontObj, 1); // 1 = Dynamic

                var multiAtlasProp = fontType.GetProperty("isMultiAtlasTexturesEnabled", BindingFlags.Public | BindingFlags.Instance);
                if (multiAtlasProp != null) multiAtlasProp.SetValue(fontObj, true);
            }
            catch { }
        }

        public override void OnInitializeMelon()
        {
            // Инициализация мода: загрузка переводов, запуск проверки обновлений
            // и установка патчей Harmony на методы установки текста (SetText и свойство text).
            var info = typeof(MyMod).Assembly.GetCustomAttribute<MelonInfoAttribute>();
            CurrentVersion = info?.Version ?? "1.0.0";
            // Удаляем устаревшие файлы NeverGraveUntranslated перед дальнейшей инициализацией
            CleanupOldUntranslatedFiles();

            UntranslatedJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", $"NeverGraveUntranslated_{CurrentVersion}.json");

            LoadTranslations();
            Task.Run(CheckForUpdates);
            var harmony = HarmonyInstance;
            try
            {
                Type? tmpTextType = null;
                Type? tmpTextUGUIType = null;
                Type? tmpTextWorldType = null;
                Type? unityTextType = null;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (tmpTextType == null) tmpTextType = assembly.GetType("TMPro.TMP_Text") ?? assembly.GetType("Il2CppTMPro.TMP_Text");
                    if (tmpTextUGUIType == null) tmpTextUGUIType = assembly.GetType("TMPro.TextMeshProUGUI") ?? assembly.GetType("Il2CppTMPro.TextMeshProUGUI");
                    if (tmpTextWorldType == null) tmpTextWorldType = assembly.GetType("TMPro.TextMeshPro") ?? assembly.GetType("Il2CppTMPro.TextMeshPro");
                    if (unityTextType == null) unityTextType = assembly.GetType("UnityEngine.UI.Text");
                    
                    if (tmpTextType != null && unityTextType != null && tmpTextUGUIType != null && tmpTextWorldType != null) break;
                }

                var prefix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(UniversalPrefix)));
                var postfix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(UniversalPostfix)));
                var onEnablePostfix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(OnEnablePostfix)));
                var sbPrefix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(Prefix_StringBuilder)));
                var charPrefix = new HarmonyMethod(typeof(MyMod).GetMethod(nameof(Prefix_CharArray)));

                // Patch TMP_Text and its child classes directly
                Type[] tmproTypes = new Type?[] { tmpTextType, tmpTextUGUIType, tmpTextWorldType }.Where(t => t != null).Cast<Type>().ToArray();
                foreach (Type t in tmproTypes)
                {
                    var textSetter = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                    if (textSetter != null && textSetter.DeclaringType == t) harmony.Patch(textSetter, prefix: prefix, postfix: postfix);

                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.DeclaringType != t) continue;
                        
                        var p = m.GetParameters();
                        if (m.Name == "SetText" && p.Length > 0 && p[0].ParameterType == typeof(string))
                        {
                            harmony.Patch(m, prefix: prefix, postfix: postfix);
                        }
                        else if (m.Name == "SetText" && p.Length > 0 && p[0].ParameterType.Name == "StringBuilder")
                        {
                            harmony.Patch(m, prefix: sbPrefix, postfix: postfix);
                        }
                        else if (m.Name == "SetCharArray" && p.Length > 0 && p[0].ParameterType == typeof(char[]))
                        {
                            harmony.Patch(m, prefix: charPrefix, postfix: postfix);
                        }
                        else if ((m.Name == "OnEnable" || m.Name == "Awake" || m.Name == "Start") && p.Length == 0)
                        {
                            harmony.Patch(m, postfix: onEnablePostfix);
                        }
                    }
                }

                // Patch Unity standard Text if exists
                if (unityTextType != null)
                {
                    var uTextSetter = unityTextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                    if (uTextSetter != null && uTextSetter.DeclaringType == unityTextType) harmony.Patch(uTextSetter, prefix: prefix, postfix: postfix);

                    var methods = unityTextType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.DeclaringType != unityTextType) continue;
                        if ((m.Name == "OnEnable" || m.Name == "Awake" || m.Name == "Start") && m.GetParameters().Length == 0)
                        {
                            harmony.Patch(m, postfix: onEnablePostfix);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Postfix для методов инициализации UI компонентов.
        /// Вызывается при OnEnable, Awake, Start и обновляет переводы.
        /// </summary>
        public static void OnEnablePostfix(object? __instance)
        {
            if (__instance == null) return;
            EnsureFontAtlasCapacity(__instance);
            try
            {
                var t = __instance.GetType();
                var textProp = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProp == null) return;

                string currentText = textProp.GetValue(__instance) as string ?? "";
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

                if (HasCyrillic.IsMatch(currentText)) return;

                string cleanValue = currentText.Replace('\u00A0', ' ').Trim();
                if (!HasEnglishLetters.IsMatch(cleanValue)) return;

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

                if (!string.IsNullOrWhiteSpace(translated))
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

                    string finalStr = (dynamicParts != null) ? string.Format(result, dynamicParts) : result;
                    textProp.SetValue(__instance, finalStr);
                }
                else
                {
                    AddNewKeyToJson(keyForDict);
                }
            }
            catch { }
        }

        /// <summary>
        /// Prefix для метода SetCharArray.
        /// Перехватывает текст, устанавливаемый как массив символов, и заменяет на перевод.
        /// </summary>
        public static void Prefix_CharArray(object? __instance, char[] __0)
        {
            if (__0 == null || __0.Length < 2) return;
            EnsureFontAtlasCapacity(__instance);
            string currentText = new string(__0);
            if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;
            if (HasCyrillic.IsMatch(currentText)) return;
            
            string cleanValue = currentText.Replace('\u00A0', ' ').Trim();
            if (!HasEnglishLetters.IsMatch(cleanValue)) return;
            
            string keyForDict = cleanValue;
            string? translated = FindTranslationWithWildcard(keyForDict, out _);
            
            if (!string.IsNullOrWhiteSpace(translated))
            {
                // Для SetCharArray мы не можем изменить длину массива, поэтому просто вызовем обычный SetText
                try
                {
                    var t = __instance!.GetType();
                    var textProp = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null) textProp.SetValue(__instance, translated);
                }
                catch { }
            }
            else
            {
                AddNewKeyToJson(keyForDict);
            }
        }

        /// <summary>
        /// Prefix для метода SetText(StringBuilder).
        /// Перехватывает текст из StringBuilder и подменяет на переведенную строку.
        /// </summary>
        public static void Prefix_StringBuilder(object? __instance, object __0)
        {
            if (__0 == null) return;
            EnsureFontAtlasCapacity(__instance);
            string currentText = __0.ToString() ?? "";
            if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;
            if (HasCyrillic.IsMatch(currentText)) return;
            
            string cleanValue = currentText.Replace('\u00A0', ' ').Trim();
            if (!HasEnglishLetters.IsMatch(cleanValue)) return;
            
            string keyForDict = cleanValue;
            string? translated = FindTranslationWithWildcard(keyForDict, out _);
            
            if (!string.IsNullOrWhiteSpace(translated))
            {
                // Для StringBuilder мы не можем легко изменить ref, поэтому очистим и добавим
                var sbType = __0.GetType();
                var clearMethod = sbType.GetMethod("Clear");
                var appendMethod = sbType.GetMethod("Append", new[] { typeof(string) });
                if (clearMethod != null && appendMethod != null)
                {
                    clearMethod.Invoke(__0, null);
                    appendMethod.Invoke(__0, new object[] { translated });
                }
            }
            else
            {
                AddNewKeyToJson(keyForDict);
            }
        }

        /// <summary>
        /// Основной метод-перехватчик (Prefix), который вызывается перед установкой string текста в UI-компонент.
        /// Здесь происходит замена английского текста на русский и извлечение динамических переменных.
        /// </summary>
        public static void UniversalPrefix(object? __instance, ref string __0)
        {
            EnsureFontAtlasCapacity(__instance);
            if (string.IsNullOrEmpty(__0) || __0.Length < 2) return;
            string cleanValue = __0.Replace('\u00A0', ' ').Trim();
            
            // Если текст уже содержит русские буквы, пропускаем логику словаря
            if (HasCyrillic.IsMatch(cleanValue)) return;

            string keyForDict = cleanValue;
            string[]? dynamicParts = null;
            
            // Извлекаем динамические части (теги, числа), чтобы они не мешали поиску в словаре
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

            string path = "";
            if (__instance is MonoBehaviour compPath)
            {
                path = GetHierarchyPath(compPath);

                // Хардкод для версии игры, чтобы не слетало при обновлениях
                if (path == "MvUI_Title>Canvas>Menu_Top>Info_Version>Txt_Version")
                {
                    if (!__0.Contains("ZIEGMASTER"))
                    {
                        __0 = __0 + "\nПЕРЕВОД: <b>ZIEGMASTER</b>";
                        if (isUpdateAvailable)
                        {
                            __0 += "<color=green>\nДОСТУПНО ОБНОВЛЕНИЕ</color>";
                        }
                    }

                    var rect = compPath.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        int id = rect.GetInstanceID();
                        if (!patchedComponents.Contains(id))
                        {
                            patchedComponents.Add(id);
                            
                            // Чтобы текст рос вверх, pivot должен быть снизу (y=0)
                            // И выравнивание самого текста тоже должно быть снизу.
                            
                            float h = rect.rect.height;
                            if (h == 0) h = 60f; // Примерная высота для 2-3 строк
                            
                            Vector2 oldPivot = rect.pivot;
                            // Вычисляем текущую позицию нижней границы относительно якоря
                            float currentBottom = rect.anchoredPosition.y - (oldPivot.y * h);
                            
                            rect.pivot = new Vector2(oldPivot.x, 0f);
                            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, currentBottom);
                            
                            // Также принудительно ставим вертикальное выравнивание вниз
                            try {
                                var alignmentProp = compPath.GetType().GetProperty("alignment");
                                if (alignmentProp != null) {
                                    object? val = alignmentProp.GetValue(compPath);
                                    string currentName = val?.ToString() ?? "";
                                    
                                    string newName = "Bottom";
                                    if (currentName.Contains("Left")) newName = "BottomLeft";
                                    else if (currentName.Contains("Right")) newName = "BottomRight";
                                    
                                    alignmentProp.SetValue(compPath, Enum.Parse(alignmentProp.PropertyType, newName));
                                }
                            } catch {}

                            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rect.rect.width + 50f);
                        }
                    }
                    return;
                }

                // Расширение блока настроек до 60% ширины экрана (для главного меню и внутриигрового меню)
                if (path.Contains("MvUI_Title>Canvas>MvUI_MenuSetting>MvUI_SettingSelect") || 
                    path.Contains("MvUI_MenuMain(Clone)>Canvas>MvUI_MenuMain_TabSystem>MvUI_MenuSetting>MvUI_SettingSelect"))
                {
                    Transform t = compPath.transform;
                    while (t != null && t.name != "MvUI_SettingSelect")
                    {
                        t = t.parent;
                    }

                    if (t != null)
                    {
                        var rect = t.GetComponent<RectTransform>();
                        if (rect != null)
                        {
                            int id = rect.GetInstanceID();
                            if (!patchedComponents.Contains(id))
                            {
                                patchedComponents.Add(id);
                                float targetWidth = Screen.width * 0.6f;
                                rect.anchorMin = new Vector2(0.5f, rect.anchorMin.y);
                                rect.anchorMax = new Vector2(0.5f, rect.anchorMax.y);
                                rect.pivot = new Vector2(0.5f, rect.pivot.y);
                                rect.anchoredPosition = new Vector2(0, rect.anchoredPosition.y);
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
                                
                                // Центрируем родительский контейнер
                                Transform currP = t.parent;
                                while (currP != null && currP.name != "Canvas")
                                {
                                    var pRect = currP.GetComponent<RectTransform>();
                                    if (pRect != null)
                                    {
                                        pRect.anchorMin = new Vector2(0.5f, pRect.anchorMin.y);
                                        pRect.anchorMax = new Vector2(0.5f, pRect.anchorMax.y);
                                        pRect.pivot = new Vector2(0.5f, pRect.pivot.y);
                                        pRect.anchoredPosition = new Vector2(0, pRect.anchoredPosition.y);
                                    }
                                    if (currP.name == "MvUI_MenuSetting") break;
                                    currP = currP.parent;
                                }

                                // Расширяем внутренние элементы и исправляем их выравнивание
                                foreach (var child in t.GetComponentsInChildren<RectTransform>(true))
                                {
                                    string n = child.name;
                                    
                                    // Сбрасываем X-позицию и центрируем якоря для всех контейнеров
                                    if (n == "Group_Btn" || n.StartsWith("LayoutGroup") || n.Contains("MvUI_MenuSetting_Select") || n.StartsWith("Folder"))
                                    {
                                        child.anchorMin = new Vector2(0.5f, child.anchorMin.y);
                                        child.anchorMax = new Vector2(0.5f, child.anchorMax.y);
                                        child.pivot = new Vector2(0.5f, child.pivot.y);
                                        child.anchoredPosition = new Vector2(0, child.anchoredPosition.y);
                                        
                                        if (n == "Group_Btn" || n.StartsWith("LayoutGroup") || n.StartsWith("Folder"))
                                        {
                                            child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
                                        }
                                    }

                                    // Исправляем выравнивание в LayoutGroup
                                    var layout = child.GetComponent("LayoutGroup");
                                    if (layout != null)
                                    {
                                        try {
                                            var alignmentProp = layout.GetType().GetProperty("childAlignment");
                                            if (alignmentProp != null) {
                                                alignmentProp.SetValue(layout, Enum.Parse(alignmentProp.PropertyType, "MiddleCenter"));
                                            }
                                            var paddingProp = layout.GetType().GetProperty("padding");
                                            if (paddingProp != null) {
                                                object? padding = paddingProp.GetValue(layout);
                                                if (padding != null) {
                                                    padding.GetType().GetProperty("left")?.SetValue(padding, 0);
                                                    padding.GetType().GetProperty("right")?.SetValue(padding, 0);
                                                    padding.GetType().GetProperty("top")?.SetValue(padding, 0);
                                                    padding.GetType().GetProperty("bottom")?.SetValue(padding, 0);
                                                }
                                            }
                                        } catch {}
                                    }

                                    // Расширяем строки и блоки описания
                                    if (n.Contains("MvUI_MenuSetting_Select") || 
                                        n.StartsWith("Img_HitBox") || 
                                        n.StartsWith("Txt_Desc") ||
                                        n.StartsWith("Txt_DifficultyDescDesc"))
                                    {
                                        child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
                                    }

                                    // Расширяем кнопки биндов, чтобы текст не вылезал и все колонки умещались
                                    if (n.Contains("MvUI_KeyBind"))
                                    {
                                        child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 280f);
                                        
                                        // Исправляем центрирование текста и иконок внутри бинда
                                        foreach (var subChild in child.GetComponentsInChildren<RectTransform>(true))
                                        {
                                            string sn = subChild.name;
                                            if (sn.StartsWith("Txt_Bind"))
                                            {
                                                subChild.anchorMin = new Vector2(0.5f, subChild.anchorMin.y);
                                                subChild.anchorMax = new Vector2(0.5f, subChild.anchorMax.y);
                                                
                                                var textComp = subChild.GetComponent("TMPro.TMP_Text") ?? subChild.GetComponent("UnityEngine.UI.Text");
                                                string alignment = "Center";

                                                if (sn == "Txt_Bind_Btn") // Иконка/Кнопка
                                                {
                                                    subChild.pivot = new Vector2(0f, 0.5f);
                                                    subChild.anchoredPosition = new Vector2(-135f, subChild.anchoredPosition.y);
                                                    subChild.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 45f);
                                                    alignment = "Right";
                                                }
                                                else if (sn == "Txt_Bind_Name") // Название действия
                                                {
                                                    subChild.pivot = new Vector2(0f, 0.5f);
                                                    subChild.anchoredPosition = new Vector2(-85f, subChild.anchoredPosition.y);
                                                    subChild.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 225f);
                                                    alignment = "Left";
                                                }
                                                else
                                                {
                                                    subChild.pivot = new Vector2(0.5f, subChild.pivot.y);
                                                    subChild.anchoredPosition = new Vector2(0, subChild.anchoredPosition.y);
                                                }
                                                
                                                if (textComp != null)
                                                {
                                                    try {
                                                        var alignProp = textComp.GetType().GetProperty("alignment");
                                                        if (alignProp != null) {
                                                            string typeFullName = textComp.GetType().FullName ?? "";
                                                            string finalAlign = typeFullName.Contains("TMPro") ? alignment : "Middle" + alignment;
                                                            alignProp.SetValue(textComp, Enum.Parse(alignProp.PropertyType, finalAlign));
                                                        }
                                                    } catch {}
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Для разделительных линий
                                    if (n.StartsWith("Img_Line"))
                                    {
                                        // Центрируем линию относительно родителя
                                        child.anchoredPosition = new Vector2(0, child.anchoredPosition.y);

                                        bool isKeyBind = false;
                                        Transform p = child.parent;
                                        while (p != null && p != t)
                                        {
                                            if (p.name.Contains("KeyBind") || p.name.Contains("KeyConfig"))
                                            {
                                                isKeyBind = true;
                                                break;
                                            }
                                            p = p.parent;
                                        }

                                        if (isKeyBind)
                                        {
                                            // Для биндов делаем линию соответствующей ширине контента (320)
                                            child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 320f);
                                        }
                                        else
                                        {
                                            // Для биндов делаем линию соответствующей ширине контента
                                            child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth * 0.9f);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (path.Contains("MvUI_MenuMain_TabInventory"))
                {
                    var rect = compPath.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        int id = rect.GetInstanceID();
                        if (!patchedComponents.Contains(id))
                        {
                            if (path.EndsWith("Txt_EqNumTitile") || path.EndsWith("Txt_EqNumTitle"))
                            {
                                patchedComponents.Add(id);
                                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x - 70f, rect.anchoredPosition.y);
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rect.rect.width + 100f);
                            }
                            else if (path.EndsWith("Txt_EqNum"))
                            {
                                patchedComponents.Add(id);
                                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x - 150f, rect.anchoredPosition.y);
                            }
                            else if (path.Contains("Txt_Eq_Convert"))
                            {
                                patchedComponents.Add(id);
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rect.rect.width + 100f);
                            }
                        }
                    }
                }
            }

            // Поиск перевода (точное совпадение или по шаблону с WILDCARD)
            string? translated = FindTranslationWithWildcard(keyForDict, out List<string>? capturedValues);

            // --- ЛОГИРОВАНИЕ НЕПЕРЕВЕДЕННЫХ СТРОК ---
            // Если точного совпадения в словаре нет, либо оно пустое - мы добавляем это в файл непереведенных строк.
            // Даже если строка была перехвачена через {WILDCARD}, мы все равно добавим оригинальную строку
            // для возможного индивидуального перевода.
            bool hasExact = TextTranslations.TryGetValue(keyForDict, out string? exactVal);
            if (!hasExact || string.IsNullOrWhiteSpace(exactVal))
            {
                if (HasEnglishLetters.IsMatch(cleanValue))
                {
                    AddNewKeyToJson(keyForDict);
                }
            }

            if (__instance is MonoBehaviour comp)
            {
                // Патчим специфичные UI элементы
                PatchSpecificUIElements(comp, path);
            }

            if (translated != null && !string.IsNullOrEmpty(translated))
            {
                string result = translated;

                // Подстановка захваченных значений из WILDCARD обратно в строку
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

                try
                {
                    // Форматируем финальную строку, возвращая на место динамические части
                    __0 = (dynamicParts != null) ? string.Format(result, dynamicParts) : result;
                }
                catch
                {
                    __0 = result;
                }
            }
        }

        private static void AdjustElementHeight(MonoBehaviour comp, string[] centerElementNames, string[] shiftElementNames)
        {
            // Вспомогательный метод для динамического изменения высоты элементов UI (например, MvUI_ElemUnlock).
            // Отключает перенос слов, вычисляет идеальную ширину текста,
            // а затем включает перенос и устанавливает LayoutElement, чтобы текст переносился на новые строки.
            // Также сдвигает соседние элементы вниз, чтобы они не перекрывались текстом.
            try
            {
                var t = comp.GetType();
                
                var enableWordWrappingProp = t.GetProperty("enableWordWrapping");
                if (enableWordWrappingProp != null) enableWordWrappingProp.SetValue(comp, true);
                
                var horizontalOverflowProp = t.GetProperty("horizontalOverflow");
                if (horizontalOverflowProp != null) horizontalOverflowProp.SetValue(comp, 0); // 0 = Wrap

                var prefHeightProp = t.GetProperty("preferredHeight");
                if (prefHeightProp != null)
                {
                    var rect = comp.GetComponent<RectTransform>();
                    if (rect != null && comp.transform.parent != null)
                    {
                        var parent = comp.transform.parent;
                        var parentRect = parent.GetComponent<RectTransform>();
                        if (parentRect == null) return;

                        // Fix for 0-width causing massive preferredHeight
                        float originalWidth = rect.rect.width;
                        bool widthForced = false;
                        
                        if (parent.parent != null)
                        {
                            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parent.parent.GetComponent<RectTransform>());
                        }
                        
                        if (rect.rect.width <= 10f)
                        {
                            float estimatedWidth = 400f;
                            if (parent.parent != null && parent.parent.parent != null)
                            {
                                var viewport = parent.parent.parent.GetComponent<RectTransform>();
                                if (viewport != null && viewport.rect.width > 10f)
                                {
                                    estimatedWidth = Mathf.Max(200f, viewport.rect.width - 200f);
                                }
                            }
                            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, estimatedWidth);
                            widthForced = true;
                        }

                        object? heightObj = prefHeightProp.GetValue(comp);
                        float prefHeight = heightObj != null ? (float)heightObj : 0f;

                        if (widthForced)
                        {
                            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, originalWidth);
                        }

                        // Record original data
                        int parentId = parentRect.GetInstanceID();
                        if (!originalRects.ContainsKey(parentId)) 
                        {
                            if (parentRect.rect.height <= 1f && parent.parent != null)
                            {
                                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parent.parent.GetComponent<RectTransform>());
                            }
                            float origH = parentRect.rect.height;
                            var l = parent.GetComponent<UnityEngine.UI.LayoutElement>();
                            if (origH <= 1f && l != null && l.preferredHeight > 0) origH = l.preferredHeight;
                            
                            originalRects[parentId] = new OriginalRectData { height = origH };
                        }

                        int textId = rect.GetInstanceID();
                        if (!originalRects.ContainsKey(textId)) originalRects[textId] = new OriginalRectData { localY = rect.localPosition.y, height = rect.rect.height };

                        var imgSelect = parent.Find("Img_Select")?.GetComponent<RectTransform>();
                        int imgId = imgSelect != null ? imgSelect.GetInstanceID() : 0;
                        if (imgSelect != null && !originalRects.ContainsKey(imgId)) originalRects[imgId] = new OriginalRectData { localY = imgSelect.localPosition.y, height = imgSelect.rect.height };

                        var centerElements = new List<RectTransform>();
                        foreach (var name in centerElementNames)
                        {
                            var child = parent.Find(name)?.GetComponent<RectTransform>();
                            if (child != null)
                            {
                                centerElements.Add(child);
                                int cid = child.GetInstanceID();
                                if (!originalRects.ContainsKey(cid)) originalRects[cid] = new OriginalRectData { localY = child.localPosition.y };
                            }
                        }

                        var shiftElements = new List<RectTransform>();
                        foreach (var name in shiftElementNames)
                        {
                            var child = parent.Find(name)?.GetComponent<RectTransform>();
                            if (child != null)
                            {
                                shiftElements.Add(child);
                                int cid = child.GetInstanceID();
                                if (!originalRects.ContainsKey(cid)) originalRects[cid] = new OriginalRectData { localY = child.localPosition.y };
                            }
                        }

                        // Calculate diff
                        float diff = 0f;
                        if (prefHeight > 65f) // Threshold for > 1 line
                        {
                            diff = prefHeight - originalRects[textId].height;
                        }

                        // Apply layout changes
                        float parentPivotY = parentRect.pivot.y;
                        float topEdgeShift = diff * (1f - parentPivotY);

                        if (diff > 0)
                        {
                            // Parent
                            parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, originalRects[parentId].height + diff);
                            var parentLayout = parent.GetComponent<UnityEngine.UI.LayoutElement>();
                            if (parentLayout == null) parentLayout = parent.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                            parentLayout.preferredHeight = originalRects[parentId].height + diff;
                            parentLayout.minHeight = originalRects[parentId].height + diff;
                        }
                        else
                        {
                            // Restore original layout
                            if (originalRects[parentId].height > 0)
                            {
                                parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, originalRects[parentId].height);
                            }
                            var parentLayout = parent.GetComponent<UnityEngine.UI.LayoutElement>();
                            if (parentLayout != null)
                            {
                                parentLayout.preferredHeight = -1;
                                parentLayout.minHeight = -1;
                            }
                        }

                        // Txt_Name
                        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, originalRects[textId].height + diff);
                        rect.localPosition = new Vector3(rect.localPosition.x, originalRects[textId].localY - diff * (1f - rect.pivot.y) + topEdgeShift, rect.localPosition.z);

                        // Img_Select
                        if (imgSelect != null)
                        {
                            imgSelect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, originalRects[imgId].height + diff);
                            imgSelect.localPosition = new Vector3(imgSelect.localPosition.x, originalRects[imgId].localY - diff * (1f - imgSelect.pivot.y) + topEdgeShift, imgSelect.localPosition.z);
                        }

                        // Center elements
                        foreach (var child in centerElements)
                        {
                            int cid = child.GetInstanceID();
                            child.localPosition = new Vector3(child.localPosition.x, originalRects[cid].localY + topEdgeShift - (diff / 2f), child.localPosition.z);
                        }

                        // Shift elements
                        foreach (var child in shiftElements)
                        {
                            int cid = child.GetInstanceID();
                            child.localPosition = new Vector3(child.localPosition.x, originalRects[cid].localY + topEdgeShift - diff, child.localPosition.z);
                        }

                        // Force rebuild
                        if (parent.parent != null)
                        {
                            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parent.parent.GetComponent<RectTransform>());
                        }
                    }
                }
            }
            catch { }
        }

        public static void UniversalPostfix(object? __instance)
        {
            // Метод-перехватчик (Postfix), который вызывается после установки текста.
            // Здесь происходит динамическая корректировка размеров UI-элементов (Layout),
            // чтобы вместить переведенный текст, который часто бывает длиннее оригинала.
            if (__instance is MonoBehaviour comp)
            {
                string path = GetHierarchyPath(comp);
                if (path.Contains("MvHud_TalkAuto") && path.Contains("Txt_Talk_Sentence"))
                {
                    try
                    {
                        var t = comp.GetType();
                        var prefWidthProp = t.GetProperty("preferredWidth");
                        if (prefWidthProp != null)
                        {
                            float maxWidth = 600f;
                            float targetWidth = maxWidth;

                            var textProp = t.GetProperty("text");
                            string currentText = (textProp?.GetValue(comp) as string) ?? "";

                            var getPrefValuesMethod = t.GetMethod("GetPreferredValues", new Type[] { typeof(string), typeof(float), typeof(float) });
                            object? invokeResult = getPrefValuesMethod?.Invoke(comp, new object[] { currentText, maxWidth, float.PositiveInfinity });

                            if (invokeResult is Vector2 values)
                            {
                                targetWidth = Mathf.Min(values.x + 2f, maxWidth);
                            }
                            else
                            {
                                object? widthObj = prefWidthProp.GetValue(comp);
                                float prefWidth = widthObj != null ? (float)widthObj : 0f;
                                targetWidth = Mathf.Min(prefWidth, maxWidth);
                            }

                            // Применяем эту ширину к LayoutElement
                            var layout = comp.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
                            if (layout != null)
                            {
                                layout.preferredWidth = targetWidth;
                            }
                        }
                    }
                    catch { }
                }
                else if (path.Contains("MvUI_ElemUnlock") && path.Contains("Txt_Name"))
                {
                    AdjustElementHeight(comp, new[] { "Icon", "Unlocked" }, new[] { "Progress", "Price" });
                }
                else if (path.Contains("MvUI_ElemLot") && path.Contains("Txt_Name"))
                {
                    AdjustElementHeight(comp, new[] { "Icon", "Txt_LotTitle", "Img_SubFrame", "Txt_LotWeight" }, new[] { "Gauge" });
                }
                else if (path.Contains("Bg_Interact") && path.Contains("Txt_Interact"))
                {
                    try
                    {
                        var t = comp.GetType();
                        var prefWidthProp = t.GetProperty("preferredWidth");
                        var prefHeightProp = t.GetProperty("preferredHeight");
                        var wrappingProp = t.GetProperty("enableWordWrapping");
                        
                        if (prefWidthProp != null && wrappingProp != null && prefHeightProp != null)
                        {
                            // Временно отключаем перенос, чтобы узнать полную ширину текста в одну строку
                            wrappingProp.SetValue(comp, false);
                            
                            object? val = prefWidthProp.GetValue(comp);
                            float prefWidth = val != null ? (float)val : 0f;
                            float maxWidth = 400f;
                            float targetWidth = maxWidth;

                            if (prefWidth > maxWidth)
                            {
                                // Если текст лишь слегка превышает лимит, даем ему максимум, чтобы не дробить на узкие строки
                                if (prefWidth < 480f)
                                {
                                    targetWidth = maxWidth;
                                }
                                else
                                {
                                    // Пытаемся разделить текст примерно 50/50 по длине строки
                                    // Увеличиваем минимальную ширину до 280, чтобы длинные слова (как ОТПРАВИТЬСЯ) точно не бились
                                    targetWidth = Mathf.Max(280f, (prefWidth / 2f) + 40f);
                                }
                                if (targetWidth > maxWidth) targetWidth = maxWidth;
                            }
                            else
                            {
                                targetWidth = prefWidth + 10f;
                            }

                            // Включаем перенос обратно
                            wrappingProp.SetValue(comp, true);
                            t.GetProperty("isTextWrappingEnabled")?.SetValue(comp, true);
                            t.GetProperty("overflowMode")?.SetValue(comp, 0); // 0 = Overflow

                            // Применяем ширину к LayoutElement для надежности
                            var layout = comp.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
                            if (layout == null) layout = comp.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                            if (layout != null)
                            {
                                layout.preferredWidth = targetWidth;
                                layout.minWidth = targetWidth;
                            }

                            var rect = comp.GetComponent<RectTransform>();
                            if (rect != null)
                            {
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
                                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                            }

                            // Теперь получаем расчетную высоту при новой ширине
                            object? hVal = prefHeightProp.GetValue(comp);
                            float prefHeight = hVal != null ? (float)hVal : 0f;
                            float targetHeight = prefHeight + 10f;

                            if (layout != null)
                            {
                                layout.preferredHeight = targetHeight;
                                layout.minHeight = targetHeight;
                            }

                            if (rect != null)
                            {
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
                                // Центрируем RectTransform
                                rect.pivot = new Vector2(0.5f, rect.pivot.y);
                                rect.anchorMin = new Vector2(0.5f, rect.anchorMin.y);
                                rect.anchorMax = new Vector2(0.5f, rect.anchorMax.y);
                                rect.anchoredPosition = new Vector2(0, rect.anchoredPosition.y);
                            }

                            // Настраиваем родителя (Bg_Interact), чтобы он обтягивал текст
                            if (comp.transform.parent != null)
                            {
                                var parentTransform = comp.transform.parent;
                                var parentRect = parentTransform.GetComponent<RectTransform>();
                                var parentLE = parentTransform.GetComponent<UnityEngine.UI.LayoutElement>();
                                if (parentLE == null) parentLE = parentTransform.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                                
                                // Паддинги: 50 слева, 50 справа. По высоте добавим 40 для спрайта.
                                float parentWidth = targetWidth + 100f;
                                float parentHeight = targetHeight + 40f;

                                if (parentLE != null)
                                {
                                    parentLE.preferredWidth = parentWidth;
                                    parentLE.minWidth = parentWidth;
                                    parentLE.preferredHeight = parentHeight;
                                    parentLE.minHeight = parentHeight;
                                }

                                if (parentRect != null)
                                {
                                    parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, parentWidth);
                                    parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, parentHeight);
                                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
                                }
                            }

                            // Выравнивание по центру
                            var alignmentProp = t.GetProperty("alignment");
                            if (alignmentProp != null)
                            {
                                string typeFullName = t.FullName ?? "";
                                // 514 = Center для TextMeshPro
                                object centerAlignment = typeFullName.Contains("TMPro") ? Enum.ToObject(alignmentProp.PropertyType, 514) : Enum.Parse(alignmentProp.PropertyType, "MiddleCenter");
                                alignmentProp.SetValue(comp, centerAlignment);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private static string? FindTranslationWithWildcard(string inputKey, out List<string>? capturedValues)
        {
            // Метод для поиска перевода с использованием регулярных выражений (Wildcards).
            // Если точное совпадение ключа не найдено, проверяются все шаблоны,
            // и совпавшие группы подставляются в переведенную строку.
            capturedValues = null;
            // Сначала проверяем точное совпадение
            if (TextTranslations.TryGetValue(inputKey, out string? exactTranslation))
            {
                return exactTranslation;
            }

            // Если не найдено, ищем по шаблонам {WILDCARD}
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

        /// <summary>
        /// Очищает старые JSON-файлы с непереведенным текстом, чтобы они не переполняли папку Mods при обновлениях.
        /// </summary>
        private static void CleanupOldUntranslatedFiles()
        {
            try
            {
                string modsDir = Path.Combine(Directory.GetCurrentDirectory(), "Mods");
                if (!Directory.Exists(modsDir)) return;

                // Попытаемся распарсить текущую версию
                bool curParsed = Version.TryParse(CurrentVersion, out var parsedCur);

                var files = Directory.GetFiles(modsDir, "NeverGraveUntranslated*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var name = Path.GetFileName(file);
                        if (string.Equals(name, "NeverGraveUntranslated.json", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(file);
                            MelonLogger.Msg($"Deleted legacy untranslated file: {name}");
                            continue;
                        }

                        var m = Regex.Match(name, "^NeverGraveUntranslated_(.+)\\.json$", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            var verStr = m.Groups[1].Value;
                            if (curParsed && Version.TryParse(verStr, out var fileVer))
                            {
                                if (fileVer < parsedCur)
                                {
                                    File.Delete(file);
                                    MelonLogger.Msg($"Deleted outdated untranslated file: {name} (version {verStr})");
                                }
                            }
                            else
                            {
                                // Нельзя корректно распознать версию — пропускаем
                            }
                        }
                    }
                    catch (Exception exFile)
                    {
                        MelonLogger.Msg($"Error while processing untranslated file '{file}': {exFile.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"CleanupOldUntranslatedFiles error: {ex.Message}");
            }
        }

        /// <summary>
        /// Возвращает путь элемента управления в иерархии Unity (от корня сцены до компонента).
        /// </summary>
        private static string GetHierarchyPath(MonoBehaviour comp)
        {
            // Возвращает полный путь объекта в иерархии (например, Canvas>Panel>Text)
            // Используется для точной идентификации UI-элементов, к которым нужно применить патчи.
            List<string> p = new List<string> { comp.gameObject.name };
            Transform? c = comp.transform.parent;
            while (c != null)
            {
                p.Insert(0, c.gameObject.name);
                c = c.parent;
            }
            return string.Join(">", p);
        }

        private static void PatchSpecificUIElements(MonoBehaviour comp, string path)
        {
            // Применяет специфичные настройки Layout к определенным элементам интерфейса.
            // В основном используется для включения переноса слов и настройки ContentSizeFitter,
            // чтобы фоновые плашки растягивались по размеру переведенного (более длинного) текста.

            // Специфичный патч для диалогового окна TalkAuto
            if (path.Contains("MvHud_TalkAuto") && path.Contains("Txt_Talk_Sentence"))
            {
                int instanceId = comp.GetInstanceID();
                if (patchedComponents.Contains(instanceId)) return;
                patchedComponents.Add(instanceId);
                
                try
                {
                    var t = comp.GetType();
                    // Включаем перенос слов
                    t.GetProperty("enableWordWrapping")?.SetValue(comp, true);

                    // Отцентровка текста (TextAlignmentOptions.Center = 514)
                    var alignmentProp = t.GetProperty("alignment");
                    if (alignmentProp != null)
                    {
                        object centerAlignment = Enum.ToObject(alignmentProp.PropertyType, 514);
                        alignmentProp.SetValue(comp, centerAlignment);
                    }

                    // Убираем внутренние отступы (margin) у текста, если они есть
                    var marginProp = t.GetProperty("margin");
                    if (marginProp != null)
                    {
                        marginProp.SetValue(comp, Vector4.zero);
                    }

                    // 1. Настраиваем LayoutElement на тексте (ширина будет задаваться динамически в Postfix)
                    var layout = comp.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
                    if (layout == null)
                    {
                        layout = comp.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                    }
                    if (layout != null)
                    {
                        layout.minWidth = 0;
                    }

                    // 2. Настраиваем ContentSizeFitter на тексте (высота по контенту)
                    var fitter = comp.gameObject.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                    if (fitter == null)
                    {
                        fitter = comp.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
                    }
                    if (fitter != null)
                    {
                        fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                        fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                    }

                    // 3. Настраиваем родителя (Bg_Talk), чтобы он обтягивал текст по ширине и высоте
                    if (comp.transform.parent != null)
                    {
                        var parentTransform = comp.transform.parent;

                        var parentLayout = parentTransform.GetComponent<UnityEngine.UI.LayoutElement>();
                        if (parentLayout != null)
                        {
                            parentLayout.minWidth = 0;
                            parentLayout.preferredWidth = -1;
                        }

                        var parentFitter = parentTransform.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                        if (parentFitter != null)
                        {
                            parentFitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                            parentFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                        }

                        var hlg = parentTransform.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                        if (hlg != null)
                        {
                            hlg.childControlHeight = true;
                            hlg.childForceExpandHeight = false;
                            hlg.childControlWidth = true;
                            hlg.childForceExpandWidth = false;
                        }

                        var vlg = parentTransform.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                        if (vlg != null)
                        {
                            vlg.childControlHeight = true;
                            vlg.childForceExpandHeight = false;
                            vlg.childControlWidth = true;
                            vlg.childForceExpandWidth = false;
                        }
                    }
                }
                catch
                {
                }
            }
            else if (path.Contains("Bg_Interact") && path.Contains("Txt_Interact"))
            {
                int instanceId = comp.GetInstanceID();
                if (patchedComponents.Contains(instanceId)) return;
                patchedComponents.Add(instanceId);

                try
                {
                    var t = comp.GetType();
                    t.GetProperty("enableWordWrapping")?.SetValue(comp, true);
                    t.GetProperty("isTextWrappingEnabled")?.SetValue(comp, true);
                    t.GetProperty("overflowMode")?.SetValue(comp, 0); // 0 = Overflow
                    
                    var alignmentProp = t.GetProperty("alignment");
                    if (alignmentProp != null)
                    {
                        string typeFullName = t.FullName ?? "";
                        object centerAlignment = typeFullName.Contains("TMPro") ? Enum.ToObject(alignmentProp.PropertyType, 514) : Enum.Parse(alignmentProp.PropertyType, "MiddleCenter");
                        alignmentProp.SetValue(comp, centerAlignment);
                    }
                    var rect = comp.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        rect.pivot = new Vector2(0.5f, rect.pivot.y);
                        rect.anchorMin = new Vector2(0.5f, rect.anchorMin.y);
                        rect.anchorMax = new Vector2(0.5f, rect.anchorMax.y);
                        rect.anchoredPosition = new Vector2(0, rect.anchoredPosition.y);
                    }

                    // Настраиваем LayoutElement
                    var layout = comp.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
                    if (layout == null) layout = comp.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                    if (layout != null)
                    {
                        layout.minWidth = 0;
                        layout.preferredWidth = 400f;
                    }

                    // Настраиваем родителя (Bg_Interact), чтобы он обтягивал текст по высоте
                    if (comp.transform.parent != null)
                    {
                        var parentTransform = comp.transform.parent;
                        
                        // Если на родителе есть Image, делаем его Sliced, чтобы он мог растягиваться без искажений
                        var img = parentTransform.GetComponent<UnityEngine.UI.Image>();
                        if (img != null)
                        {
                            img.type = UnityEngine.UI.Image.Type.Sliced;
                        }

                        // Настраиваем LayoutElement на родителе, чтобы он не форсировал высоту
                        var parentLE = parentTransform.GetComponent<UnityEngine.UI.LayoutElement>();
                        if (parentLE == null) parentLE = parentTransform.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                        if (parentLE != null)
                        {
                            parentLE.minHeight = 0;
                            parentLE.preferredHeight = -1; // -1 означает "использовать расчетную высоту"
                            parentLE.flexibleHeight = 0;
                            parentLE.minWidth = 0;
                            parentLE.preferredWidth = -1;
                            parentLE.flexibleWidth = 0;
                        }

                        // Добавляем ContentSizeFitter родителю, если его нет
                        var parentFitter = parentTransform.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                        if (parentFitter == null) parentFitter = parentTransform.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
                        if (parentFitter != null)
                        {
                            parentFitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                            parentFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                        }

                        var hlg = parentTransform.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                        if (hlg != null)
                        {
                            hlg.childControlWidth = false;
                            hlg.childForceExpandWidth = false;
                            hlg.childControlHeight = false;
                            hlg.childForceExpandHeight = false;
                        }
                        var vlg = parentTransform.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                        if (vlg != null)
                        {
                            vlg.childControlWidth = false;
                            vlg.childForceExpandWidth = false;
                            vlg.childControlHeight = false;
                            vlg.childForceExpandHeight = false;
                        }
                    }

                    // На тексте тоже нужен ContentSizeFitter для правильного расчета высоты
                    var textFitter = comp.gameObject.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                    if (textFitter == null) textFitter = comp.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
                    if (textFitter != null)
                    {
                        textFitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                        textFitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                    }
                }
                catch { }
            }
            else if (path.Contains("MvUI_ElemUnlock") && path.Contains("Txt_Name"))
            {
                int instanceId = comp.GetInstanceID();
                if (patchedComponents.Contains(instanceId)) return;
                patchedComponents.Add(instanceId);

                try
                {
                    var t = comp.GetType();
                    // Включаем перенос слов
                    t.GetProperty("enableWordWrapping")?.SetValue(comp, true);
                    
                    // Отключаем ContentSizeFitter по вертикали на тексте, если есть, 
                    // так как мы будем управлять высотой вручную в Postfix
                    var fitter = comp.gameObject.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                    if (fitter != null)
                    {
                        fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                    }
                }
                catch
                {
                }
            }
            else if (path.Contains("MvUI_MenuMain_TabStatus") && 
                     (path.Contains("Txt_Title_Skill") || path.Contains("Txt_Title_Tool") || path.Contains("Txt_Title_Other")))
            {
                int instanceId = comp.GetInstanceID();
                if (patchedComponents.Contains(instanceId)) return;
                patchedComponents.Add(instanceId);

                try
                {
                    var t = comp.GetType();
                    t.GetProperty("enableAutoSizing")?.SetValue(comp, true);
                    t.GetProperty("fontSizeMin")?.SetValue(comp, 12f);
                }
                catch
                {
                }
            }
            else if (path.Contains("Info_Pl_LeftTop") && (path.Contains("Root_Btn_Def") || path.Contains("Root_Btn_Hat")))
            {
                try
                {
                    Transform? current = comp.transform;
                    while (current != null)
                    {
                        if (current.name == "Root_Btn_Def" || current.name == "Root_Btn_Hat")
                        {
                            var rect = current.GetComponent<RectTransform>();
                            if (rect != null)
                            {
                                int rectId = rect.GetInstanceID();
                                if (!originalRects.ContainsKey(rectId))
                                {
                                    originalRects[rectId] = new OriginalRectData { width = rect.rect.width };
                                }
                                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, originalRects[rectId].width + 100f);
                            }

                            // Ищем все дочерние элементы (даже вложенные), начинающиеся на "txt_Btn"
                            var children = current.GetComponentsInChildren<RectTransform>(true);
                            foreach (var childRect in children)
                            {
                                if (childRect.name.StartsWith("txt_Btn"))
                                {
                                    int childRectId = childRect.GetInstanceID();
                                    if (!originalRects.ContainsKey(childRectId))
                                    {
                                        originalRects[childRectId] = new OriginalRectData { width = childRect.rect.width };
                                    }
                                    childRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, originalRects[childRectId].width + 100f);
                                }
                            }
                            break;
                        }
                        current = current.parent;
                    }
                }
                catch
                {
                }
            }
            else if (path.Contains("MvUI_ElemLot"))
            {
                int instanceId = comp.GetInstanceID();
                if (patchedComponents.Contains(instanceId)) return;
                patchedComponents.Add(instanceId);

                try
                {
                    Transform? current = comp.transform;
                    while (current != null)
                    {
                        if (current.name.StartsWith("MvUI_ElemLot"))
                        {
                            break;
                        }
                        current = current.parent;
                    }
                }
                catch
                {
                }
            }
        }

        private static bool AddNewKeyToJson(string key)
        {
            // Добавление ненайденной строки в JSON-файл непереведенного текста
            lock (FileLock)
            {
                if (!HasLoadedUntranslated)
                {
                    if (File.Exists(UntranslatedJsonPath))
                    {
                        try
                        {
                            string json = File.ReadAllText(UntranslatedJsonPath);
                            UntranslatedCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                        }
                        catch
                        {
                        }
                    }
                    HasLoadedUntranslated = true;
                }
                
                if (!UntranslatedCache.ContainsKey(key))
                {
                    UntranslatedCache[key] = "";
                    try
                    {
                        var opt = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        };
                        File.WriteAllText(UntranslatedJsonPath, JsonSerializer.Serialize(UntranslatedCache, opt));
                        return true;
                    }
                    catch
                    {
                        UntranslatedCache.Remove(key);
                    }
                }
                
                return false;
            }
        }

        private void LoadTranslations()
        {
            // Загрузка словаря переводов из файла
            if (File.Exists(JsonPath))
                try { TextTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(JsonPath)) ?? new Dictionary<string, string>(); } catch { }
            TextTranslations ??= new Dictionary<string, string>();

            BuildWildcardCache();
        }

        private void BuildWildcardCache()
        {
            // Подготовка кэша регулярных выражений для строк с {WILDCARD}
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

        private static async Task CheckForUpdates()
        {
            // Асинхронно проверяет наличие обновлений перевода на GitHub.
            // Сравнивает текущую версию мода с версией последнего релиза.
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "NeverGraveRussianMod");
                    string json = await client.GetStringAsync("https://api.github.com/repos/Ziegmaster/NeverGraveRussian/releases/latest");
                    
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement))
                        {
                            string latest = tagElement.GetString()?.TrimStart('v') ?? "";
                            if (IsNewerVersion(latest, CurrentVersion))
                            {
                                isUpdateAvailable = true;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static bool IsNewerVersion(string latest, string current)
        {
            // Сравнивает две строки версий (например, "1.6.0" и "1.5.0").
            // Возвращает true, если latest версия новее current.
            try
            {
                Version vLatest = new Version(latest);
                Version vCurrent = new Version(current);
                return vLatest > vCurrent;
            }
            catch { return false; }
        }

        public override void OnApplicationQuit()
        {
            // Отмена патчей и принудительное закрытие процесса для предотвращения зависаний
            try
            {
                HarmonyInstance.UnpatchSelf();
                Process.GetCurrentProcess().Kill();
            }
            catch { }
        }
    }
}