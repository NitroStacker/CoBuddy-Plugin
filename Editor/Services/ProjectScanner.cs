using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    public static class ProjectScanner
    {
        public static string GetSelectedScriptPath()
        {
            UnityEngine.Object selectedObject = Selection.activeObject;
            if (selectedObject == null)
                return null;

            string assetPath = AssetDatabase.GetAssetPath(selectedObject);

            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return null;

            return assetPath;
        }

        public static string GetSelectedScriptContent()
        {
            string assetPath = GetSelectedScriptPath();
            if (string.IsNullOrEmpty(assetPath))
                return null;

            string fullPath = Path.GetFullPath(assetPath);

            if (!File.Exists(fullPath))
                return null;

            return File.ReadAllText(fullPath);
        }

        public static List<ScriptFileData> GetAllScriptsInAssets()
        {
            List<ScriptFileData> scripts = new List<ScriptFileData>();

            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return scripts;

            string[] files = Directory.GetFiles(assetsFullPath, "*.cs", SearchOption.AllDirectories);

            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);

                    scripts.Add(new ScriptFileData
                    {
                        path = relativePath,
                        content = content
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read script: {fullPath}\n{ex.Message}");
                }
            }

            return scripts;
        }

        public static List<PrefabFileData> GetAllPrefabsInAssets()
        {
            var prefabs = new List<PrefabFileData>();

            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return prefabs;

            string[] files = Directory.GetFiles(assetsFullPath, "*.prefab", SearchOption.AllDirectories);

            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);

                    prefabs.Add(new PrefabFileData
                    {
                        path = relativePath,
                        content = content
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read prefab: {fullPath}\n{ex.Message}");
                }
            }

            return prefabs;
        }

        public static ScriptFileData[] GetRelevantScripts(string prompt, string selectedScriptPath, int maxScripts = 8)
        {
            List<ScriptFileData> allScripts = GetAllScriptsInAssets();
            List<ScoredScript> scored = new List<ScoredScript>();

            HashSet<string> promptTerms = ExtractTerms(prompt);
            HashSet<string> selectedTerms = ExtractTermsFromPath(selectedScriptPath);

            foreach (ScriptFileData script in allScripts)
            {
                if (script == null || string.IsNullOrWhiteSpace(script.path))
                    continue;

                int score = 0;

                if (!string.IsNullOrWhiteSpace(selectedScriptPath) &&
                    string.Equals(script.path, selectedScriptPath, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1000;
                }

                string fileName = Path.GetFileNameWithoutExtension(script.path);
                string className = ExtractFirstClassName(script.content);

                foreach (string term in promptTerms)
                {
                    if (ContainsInsensitive(script.path, term))
                        score += 30;

                    if (!string.IsNullOrWhiteSpace(fileName) && fileName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 40;

                    if (!string.IsNullOrWhiteSpace(className) && className.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 50;

                    if (!string.IsNullOrWhiteSpace(script.content) && script.content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 10;
                }

                foreach (string term in selectedTerms)
                {
                    if (ContainsInsensitive(script.path, term))
                        score += 15;

                    if (!string.IsNullOrWhiteSpace(script.content) && script.content.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 5;
                }

                if (score > 0)
                {
                    scored.Add(new ScoredScript
                    {
                        script = script,
                        score = score
                    });
                }
            }

            return scored
                .OrderByDescending(s => s.score)
                .ThenBy(s => s.script.path)
                .Take(maxScripts)
                .Select(s => s.script)
                .ToArray();
        }

        public static string GetManifestJson()
        {
            string manifestPath = Path.GetFullPath("Packages/manifest.json");
            if (!File.Exists(manifestPath))
                return null;

            try
            {
                return File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read manifest.json\n{ex.Message}");
                return null;
            }
        }

        public static AsmdefFileData[] GetAllAsmdefs()
        {
            List<AsmdefFileData> asmdefs = new List<AsmdefFileData>();

            string projectRoot = Path.GetFullPath(".");
            string[] files = Directory.GetFiles(projectRoot, "*.asmdef", SearchOption.AllDirectories);

            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);

                    asmdefs.Add(new AsmdefFileData
                    {
                        path = relativePath,
                        content = content
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read asmdef: {fullPath}\n{ex.Message}");
                }
            }

            return asmdefs.ToArray();
        }

        public static List<MaterialFileData> GetAllMaterialsInAssets()
        {
            var materials = new List<MaterialFileData>();
            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return materials;

            string[] files = Directory.GetFiles(assetsFullPath, "*.mat", SearchOption.AllDirectories);
            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);
                    materials.Add(new MaterialFileData { path = relativePath, content = content });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read material: {fullPath}\n{ex.Message}");
                }
            }
            return materials;
        }

        public static List<ShaderFileData> GetAllShadersInAssets()
        {
            var shaders = new List<ShaderFileData>();
            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return shaders;

            string[] files = Directory.GetFiles(assetsFullPath, "*.shader", SearchOption.AllDirectories);
            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);
                    shaders.Add(new ShaderFileData { path = relativePath, content = content });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read shader: {fullPath}\n{ex.Message}");
                }
            }
            return shaders;
        }

        public static List<InputActionFileData> GetAllInputActionsInAssets()
        {
            var inputActions = new List<InputActionFileData>();
            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return inputActions;

            string[] files = Directory.GetFiles(assetsFullPath, "*.inputactions", SearchOption.AllDirectories);
            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    string relativePath = MakeProjectRelativePath(fullPath);
                    inputActions.Add(new InputActionFileData { path = relativePath, content = content });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read InputActions: {fullPath}\n{ex.Message}");
                }
            }
            return inputActions;
        }

        private const int MaxAssetContentBytes = 5120;

        public static List<ScriptableObjectFileData> GetAllScriptableObjectsInAssets()
        {
            var assets = new List<ScriptableObjectFileData>();
            string assetsFullPath = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsFullPath))
                return assets;

            string[] files = Directory.GetFiles(assetsFullPath, "*.asset", SearchOption.AllDirectories);
            foreach (string fullPath in files)
            {
                try
                {
                    string content = File.ReadAllText(fullPath);
                    if (content.Length > MaxAssetContentBytes)
                        content = content.Substring(0, MaxAssetContentBytes) + "\n... (truncated)";
                    string relativePath = MakeProjectRelativePath(fullPath);
                    string assetType = ExtractAssetTypeFromYaml(content);
                    assets.Add(new ScriptableObjectFileData
                    {
                        path = relativePath,
                        content = content,
                        assetType = assetType
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to read asset: {fullPath}\n{ex.Message}");
                }
            }
            return assets;
        }

        private static string ExtractAssetTypeFromYaml(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                return null;
            if (yaml.IndexOf("MonoBehaviour:", StringComparison.Ordinal) >= 0)
                return "ScriptableObject";
            if (yaml.IndexOf("ScriptableObject", StringComparison.Ordinal) >= 0)
                return "ScriptableObject";
            return "Asset";
        }

        public static List<CoBuddy.Editor.Models.PackageInfo> GetPackagesInfo()
        {
            var packages = new List<CoBuddy.Editor.Models.PackageInfo>();
            string manifest = GetManifestJson();
            if (string.IsNullOrEmpty(manifest))
                return packages;

            try
            {
                int depsStart = manifest.IndexOf("\"dependencies\"", StringComparison.OrdinalIgnoreCase);
                if (depsStart < 0)
                    return packages;
                int blockStart = manifest.IndexOf('{', depsStart);
                if (blockStart < 0)
                    return packages;
                int depth = 1;
                int blockEnd = blockStart;
                for (int i = blockStart + 1; i < manifest.Length && depth > 0; i++)
                {
                    if (manifest[i] == '{') depth++;
                    else if (manifest[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { blockEnd = i; break; }
                    }
                }
                if (blockEnd <= blockStart)
                    return packages;
                string depsBlock = manifest.Substring(blockStart, blockEnd - blockStart + 1);
                var matches = Regex.Matches(depsBlock, @"\""([^""]+)\""\s*:\s*\""([^""]*)""");
                foreach (Match m in matches)
                {
                    if (m.Groups.Count >= 3)
                    {
                        string name = m.Groups[1].Value;
                        string version = m.Groups[2].Value;
                        if (!string.IsNullOrEmpty(name))
                        {
                            packages.Add(new CoBuddy.Editor.Models.PackageInfo
                            {
                                name = name,
                                version = version,
                                path = version.StartsWith("file:", StringComparison.Ordinal) ? version.Substring(5) : null
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse manifest dependencies: {ex.Message}");
            }
            return packages;
        }

        private static string MakeProjectRelativePath(string fullPath)
        {
            string projectPath = Path.GetFullPath(".");
            string normalizedFullPath = fullPath.Replace("\\", "/");
            string normalizedProjectPath = projectPath.Replace("\\", "/");

            if (normalizedFullPath.StartsWith(normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFullPath.Substring(normalizedProjectPath.Length + 1);
            }

            return normalizedFullPath;
        }

        private static string ExtractFirstClassName(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            Match match = Regex.Match(content, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static HashSet<string> ExtractTerms(string text)
        {
            HashSet<string> terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(text))
                return terms;

            MatchCollection matches = Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]{2,}");
            foreach (Match match in matches)
            {
                string value = match.Value.Trim();
                if (value.Length >= 3)
                    terms.Add(value);
            }

            return terms;
        }

        private static HashSet<string> ExtractTermsFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string fileName = Path.GetFileNameWithoutExtension(path);
            return ExtractTerms(fileName);
        }

        private static bool ContainsInsensitive(string source, string value)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   !string.IsNullOrWhiteSpace(value) &&
                   source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private class ScoredScript
        {
            public ScriptFileData script;
            public int score;
        }
    }
}
