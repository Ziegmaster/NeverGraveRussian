using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

[assembly: MelonInfo(typeof(NeverGraveDumper.DumperMod), "Never Grave Dumper", "1.6.2", "Ziegmaster")]
[assembly: MelonGame(null, null)]

namespace NeverGraveDumper
{
    public class DumperMod : MelonMod
    {
        private static readonly Regex HasEnglishLetters = new Regex(@"[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex DynamicContentRegex = new Regex(@"<[^>]+>|\{[^}]+\}|\d+", RegexOptions.Compiled);
        private static readonly object FileLock = new object();
        
        private Dictionary<string, string> existingTranslations = new Dictionary<string, string>();
        private Dictionary<string, string> untranslatedStrings = new Dictionary<string, string>();
        
        private static readonly string RussianJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "NeverGraveRussian.json");
        private static string UntranslatedJsonPath = string.Empty;
        private static string CurrentVersion = "1.0.0";

        public override void OnInitializeMelon()
        {
            var info = typeof(DumperMod).Assembly.GetCustomAttribute<MelonInfoAttribute>();
            CurrentVersion = info?.Version ?? "1.0.0";
            UntranslatedJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", $"NeverGraveUntranslated_{CurrentVersion}.json");
        }

        public override void OnUpdate()
        {
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F11))
            {
                DumpLocalizationTables();
            }
        }

        private void LoadExistingData()
        {
            existingTranslations.Clear();
            untranslatedStrings.Clear();

            if (File.Exists(RussianJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(RussianJsonPath);
                    existingTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    MelonLogger.Msg($"Loaded {existingTranslations.Count} existing translations from NeverGraveRussian.json");
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"Error loading NeverGraveRussian.json: {ex.Message}");
                }
            }

            if (File.Exists(UntranslatedJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(UntranslatedJsonPath);
                    untranslatedStrings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    MelonLogger.Msg($"Loaded {untranslatedStrings.Count} existing untranslated strings from NeverGraveUntranslated.json");
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"Error loading NeverGraveUntranslated.json: {ex.Message}");
                }
            }
        }

        private void DumpLocalizationTables()
        {
            MelonLogger.Msg("Starting localization dump...");
            int dumpedCount = 0;

            try
            {
                LoadExistingData();

                var allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                var stringDict = new Dictionary<string, string>();
                
                foreach (var obj in allObjects)
                {
                    if (obj == null) continue;
                    
                    string il2cppTypeName = "";
                    try 
                    {
                        il2cppTypeName = obj.GetIl2CppType().Name;
                    } 
                    catch { }

                    string objName = obj.name;

                    if (il2cppTypeName == "StringTable")
                    {
                        MelonLogger.Msg($"Found StringTable: {objName}");
                        ExtractFromStringTable(obj, stringDict, objName);
                        dumpedCount++;
                    }
                }

                var options = new JsonSerializerOptions { 
                    WriteIndented = true, 
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
                };

                if (stringDict.Count > 0)
                {
                    string dumpPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "LocalizationDump.json");
                    File.WriteAllText(dumpPath, JsonSerializer.Serialize(stringDict, options));
                    MelonLogger.Msg($"Dumped {stringDict.Count} strings to {dumpPath}");
                }
                else
                {
                    MelonLogger.Msg("No strings found in StringTables.");
                }

                if (untranslatedStrings.Count > 0)
                {
                    File.WriteAllText(UntranslatedJsonPath, JsonSerializer.Serialize(untranslatedStrings, options));
                    MelonLogger.Msg($"Updated {UntranslatedJsonPath} with {untranslatedStrings.Count} untranslated strings.");
                }

                MelonLogger.Msg($"Finished dumping {dumpedCount} localization objects.");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"Error during dump: {ex.Message}");
            }
        }

        private void ExtractFromStringTable(UnityEngine.Object obj, Dictionary<string, string> dict, string tableName)
        {
            try
            {
                var il2cppType = obj.GetIl2CppType();
                var bindingFlags = (Il2CppSystem.Reflection.BindingFlags)54; // Instance | Public | NonPublic | DeclaredOnly
                
                // 1. Пробуем вытащить через m_TableData (List<StringTableEntry>)
                Il2CppSystem.Reflection.FieldInfo? tableDataField = null;
                var currentType = il2cppType;
                while (currentType != null && currentType.Name != "Object")
                {
                    tableDataField = currentType.GetField("m_TableData", bindingFlags);
                    if (tableDataField != null) break;
                    currentType = currentType.BaseType;
                }

                bool extracted = false;

                if (tableDataField != null)
                {
                    var tableDataObj = tableDataField.GetValue(obj);
                    if (tableDataObj != null)
                    {
                        // В Il2Cpp List<T> хранит элементы в массиве _items
                        var itemsField = tableDataObj.GetIl2CppType().GetField("_items", bindingFlags) ?? tableDataObj.GetIl2CppType().GetField("items", bindingFlags);
                        if (itemsField != null)
                        {
                            var itemsArray = itemsField.GetValue(tableDataObj);
                            if (itemsArray != null)
                            {
                                var arr = itemsArray.TryCast<Il2CppSystem.Array>();
                                if (arr != null)
                                {
                                    int count = arr.Length;
                                    MelonLogger.Msg($"Found array with length {count} in {tableName} via m_TableData");
                                    
                                    for (int i = 0; i < count; i++)
                                    {
                                        var entry = arr.GetValue(i);
                                        if (entry != null)
                                        {
                                            ExtractEntry(entry, dict, tableName, i);
                                            extracted = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Если m_TableData пуст, пробуем m_TableEntries (Dictionary<long, StringTableEntry>)
                if (!extracted)
                {
                    Il2CppSystem.Reflection.FieldInfo? tableEntriesField = null;
                    currentType = il2cppType;
                    while (currentType != null && currentType.Name != "Object")
                    {
                        tableEntriesField = currentType.GetField("m_TableEntries", bindingFlags);
                        if (tableEntriesField != null) break;
                        currentType = currentType.BaseType;
                    }

                    if (tableEntriesField != null)
                    {
                        var tableEntriesObj = tableEntriesField.GetValue(obj);
                        if (tableEntriesObj != null)
                        {
                            // В Il2Cpp Dictionary<K,V> хранит элементы в массиве _entries
                            var entriesField2 = tableEntriesObj.GetIl2CppType().GetField("_entries", bindingFlags) ?? tableEntriesObj.GetIl2CppType().GetField("entries", bindingFlags);
                            if (entriesField2 != null)
                            {
                                var entriesArray = entriesField2.GetValue(tableEntriesObj);
                                if (entriesArray != null)
                                {
                                    var arr = entriesArray.TryCast<Il2CppSystem.Array>();
                                    if (arr != null)
                                    {
                                        int count = arr.Length;
                                        MelonLogger.Msg($"Found array with length {count} in {tableName} via m_TableEntries");
                                        
                                        for (int i = 0; i < count; i++)
                                        {
                                            var dictEntry = arr.GetValue(i);
                                            if (dictEntry != null)
                                            {
                                                // dictEntry это структура Entry<K,V>, у которой есть поле value
                                                var valueField = dictEntry.GetIl2CppType().GetField("value", bindingFlags) ?? dictEntry.GetIl2CppType().GetField("Value", bindingFlags);
                                                if (valueField != null)
                                                {
                                                    var entry = valueField.GetValue(dictEntry);
                                                    if (entry != null)
                                                    {
                                                        ExtractEntry(entry, dict, tableName, i);
                                                        extracted = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (!extracted)
                {
                    MelonLogger.Msg($"Could not extract entries from {tableName}. Both m_TableData and m_TableEntries failed.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"Failed to extract from {tableName}: {ex.Message}");
            }
        }

        private void ExtractEntry(Il2CppSystem.Object entry, Dictionary<string, string> dict, string tableName, int index)
        {
            try
            {
                var entryType = entry.GetIl2CppType();
                var bindingFlags = (Il2CppSystem.Reflection.BindingFlags)54; // Instance | Public | NonPublic | DeclaredOnly
                
                Il2CppSystem.Reflection.FieldInfo? idField = null;
                Il2CppSystem.Reflection.FieldInfo? locField = null;
                Il2CppSystem.Reflection.PropertyInfo? locProp = null;
                
                var currType = entryType;
                while (currType != null && currType.Name != "Object")
                {
                    if (idField == null) idField = currType.GetField("m_Id", bindingFlags) ?? currType.GetField("m_KeyId", bindingFlags) ?? currType.GetField("m_Key", bindingFlags);
                    if (locField == null) locField = currType.GetField("m_Localized", bindingFlags) ?? currType.GetField("m_LocalizedValue", bindingFlags) ?? currType.GetField("m_Value", bindingFlags);
                    if (locProp == null) locProp = currType.GetProperty("Localized", bindingFlags) ?? currType.GetProperty("LocalizedValue", bindingFlags) ?? currType.GetProperty("Value", bindingFlags);
                    currType = currType.BaseType;
                }

                string key = "";
                if (idField != null)
                {
                    var keyObj = idField.GetValue(entry);
                    if (keyObj != null)
                    {
                        var typeName = keyObj.GetIl2CppType().Name;
                        if (typeName == "Int64") key = keyObj.Unbox<long>().ToString();
                        else if (typeName == "UInt32") key = keyObj.Unbox<uint>().ToString();
                        else if (typeName == "Int32") key = keyObj.Unbox<int>().ToString();
                        else key = keyObj.ToString() ?? "";
                    }
                }

                if (string.IsNullOrWhiteSpace(key) || key == "0") key = $"{tableName}_{index}";
                
                string val = "";
                if (locProp != null)
                {
                    var valObj = locProp.GetValue(entry);
                    if (valObj != null)
                    {
                        val = valObj.ToString() ?? "";
                    }
                }
                
                if (string.IsNullOrWhiteSpace(val) && locField != null)
                {
                    var valObj = locField.GetValue(entry);
                    if (valObj != null)
                    {
                        val = valObj.ToString() ?? "";
                    }
                }
                
                if (index < 5) MelonLogger.Msg($"Entry {index}: Key='{key}', Val='{val}'");

                if (!string.IsNullOrWhiteSpace(val))
                {
                    dict[key] = val;

                    // Логика проверки на перевод
                    string cleanVal = val.Replace('\u00A0', ' ').Trim();
                    
                    // Пропускаем строки с динамическим содержимым (теги, плейсхолдеры, числа)
                    // Исключение - \n (DynamicContentRegex его не трогает)
                    if (HasEnglishLetters.IsMatch(cleanVal) && !DynamicContentRegex.IsMatch(cleanVal))
                    {
                        lock (FileLock)
                        {
                            if (!existingTranslations.ContainsKey(cleanVal) && !untranslatedStrings.ContainsKey(cleanVal))
                            {
                                untranslatedStrings[cleanVal] = "";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (index < 5) MelonLogger.Msg($"Error extracting entry {index}: {ex.Message}");
            }
        }
    }
}
