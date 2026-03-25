using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Two-phase action validation: ResolveAndValidate before execution.
    /// Per-action-type validation with fuzzy match suggestions for failed lookups.
    /// </summary>
    public static class ActionValidator
    {
        public static ActionValidationResult[] ValidateActions(EditorAction[] actions)
        {
            if (actions == null || actions.Length == 0)
                return Array.Empty<ActionValidationResult>();

            // Track GameObjects created by prior actions in this batch (for cross-action references)
            var createdPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var results = new List<ActionValidationResult>();
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.action))
                {
                    results.Add(new ActionValidationResult
                    {
                        action = action?.action ?? "null",
                        isValid = false,
                        error = "Action is null or has no action type"
                    });
                    continue;
                }

                var result = ValidateAction(action, createdPaths);
                results.Add(result);

                // Track paths that will be created by this action
                if (result.isValid)
                    TrackCreatedPath(action, createdPaths);
            }
            return results.ToArray();
        }

        private static void TrackCreatedPath(EditorAction action, HashSet<string> created)
        {
            string a = action.action.ToLowerInvariant();
            switch (a)
            {
                case "creategameobject":
                    if (!string.IsNullOrWhiteSpace(action.name))
                    {
                        string path = !string.IsNullOrWhiteSpace(action.parent) ? action.parent + "/" + action.name : action.name;
                        created.Add(path);
                    }
                    break;
                case "instantiateprefab":
                    if (!string.IsNullOrWhiteSpace(action.name))
                        created.Add(action.name);
                    break;
                case "createprimitive":
                    if (!string.IsNullOrWhiteSpace(action.name))
                        created.Add(action.name);
                    break;
                case "createcanvas":
                    created.Add(action.name ?? "Canvas");
                    break;
            }
        }

        private static ActionValidationResult ValidateAction(EditorAction action, HashSet<string> createdPaths)
        {
            string a = action.action.ToLowerInvariant();

            switch (a)
            {
                // ── Target-required actions ──
                case "addcomponent": return ValidateAddComponent(action, createdPaths);
                case "setcomponentproperty": return ValidateSetComponentProperty(action, createdPaths);
                case "deletegameobject": return ValidateTargetExists(action, createdPaths, isDestructive: true);
                case "updategameobject": return ValidateUpdateGameObject(action, createdPaths);
                case "reparentgameobject": return ValidateReparentGameObject(action, createdPaths);
                case "destroygameobject": return ValidateTargetExists(action, createdPaths, isDestructive: true);
                case "removecomponent": return ValidateRemoveComponent(action, createdPaths);
                case "assignmaterial": return ValidateAssignMaterial(action, createdPaths);

                // ── Create actions ──
                case "creategameobject": return ValidateCreateGameObject(action);
                case "createscript": return ValidateCreateScript(action);
                case "instantiateprefab": return ValidateInstantiatePrefab(action);
                case "createprefab": return ValidateCreatePrefab(action, createdPaths);
                case "createprimitive": return ValidateCreatePrimitive(action);
                case "creatematerial": return ValidateAssetPathRequired(action, "createMaterial");
                case "createshader": return ValidateAssetPathRequired(action, "createShader");
                case "createscriptableobject": return ValidateCreateScriptableObject(action);
                case "createtexture": return ValidateAssetPathRequired(action, "createTexture");
                case "createscene": return ValidateAssetPathRequired(action, "createScene");
                case "createinputactions": return ValidateAssetPathRequired(action, "createInputActions");
                case "createassemblydefinition": return ValidateAssetPathRequired(action, "createAssemblyDefinition");
                case "createanimationclip": return ValidateAssetPathRequired(action, "createAnimationClip");

                // ── Physics actions ──
                case "addrigidbody":
                case "addcollider":
                case "addrigidbody2d":
                case "addcollider2d":
                    return ValidateTargetExists(action, createdPaths);

                // ── UI actions ──
                case "createcanvas":
                    return ValidateOk(action);
                case "setrectransformlayout":
                case "updatecanvas":
                case "updateuiimage":
                case "updateuitext":
                    return ValidateTargetExists(action, createdPaths);

                // ── Audio/Video ──
                case "addaudiosource":
                case "addvideoplayer":
                    return ValidateTargetExists(action, createdPaths);

                // ── Scene query actions (always valid) ──
                case "printscenehierarchy":
                case "printgameobjects":
                case "printassets":
                case "readfiles":
                    return ValidateOk(action);

                // ── Asset actions ──
                case "openscene":
                    return ValidateAssetExists(action, action.path ?? action.assetPath, ".unity");
                case "copyasset":
                case "moveasset":
                    return ValidateAssetExists(action, action.sourcePath ?? action.path, null);

                // ── Settings ──
                case "setbuildscenes":
                    if (action.scenePaths == null || action.scenePaths.Length == 0)
                        return ValidateFail(action, "setBuildScenes requires 'scenePaths' array");
                    return ValidateOk(action);
                case "setplayersettings":
                    return ValidateOk(action);

                // ── Other ──
                case "selectobject":
                    return ValidateOk(action);
                case "executemenuitem":
                    if (string.IsNullOrWhiteSpace(action.menuPath))
                        return ValidateFail(action, "executeMenuItem requires 'menuPath'");
                    return ValidateOk(action);

                default:
                    // Unknown actions pass — don't block
                    return ValidateOk(action);
            }
        }

        // ── Per-action validators ──────────────────────────────────────────

        private static ActionValidationResult ValidateAddComponent(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "addComponent requires a 'target' or 'path'");
            if (string.IsNullOrWhiteSpace(action.componentType))
                return ValidateFail(action, "addComponent requires a 'componentType'");

            return ResolveTarget(action, targetPath, createdPaths);
        }

        private static ActionValidationResult ValidateSetComponentProperty(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "setComponentProperty requires a 'target' or 'path'");
            if (string.IsNullOrWhiteSpace(action.componentType))
                return ValidateFail(action, "setComponentProperty requires a 'componentType'");
            if (string.IsNullOrWhiteSpace(action.property))
                return ValidateFail(action, "setComponentProperty requires a 'property' name");
            if (string.IsNullOrWhiteSpace(action.value))
                return ValidateFail(action, "setComponentProperty requires a 'value'");

            var result = ResolveTarget(action, targetPath, createdPaths);
            // If target exists, also validate component exists on it
            if (result.isValid && string.IsNullOrWhiteSpace(result.warning))
            {
                var go = GameObject.Find(targetPath.TrimStart('/'));
                if (go != null)
                {
                    var comp = go.GetComponent(action.componentType);
                    if (comp == null)
                    {
                        // Try case-insensitive
                        var allComps = go.GetComponents<Component>();
                        var match = allComps.FirstOrDefault(c => c != null && c.GetType().Name.Equals(action.componentType, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            var compNames = allComps.Where(c => c != null).Select(c => c.GetType().Name).Distinct().ToArray();
                            result.warning = $"Component '{action.componentType}' not found on '{targetPath}'. Available: {string.Join(", ", compNames)}";
                        }
                    }
                }
            }
            return result;
        }

        private static ActionValidationResult ValidateRemoveComponent(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "removeComponent requires a 'target' or 'path'");
            if (string.IsNullOrWhiteSpace(action.componentType))
                return ValidateFail(action, "removeComponent requires a 'componentType'");
            return ResolveTarget(action, targetPath, createdPaths);
        }

        private static ActionValidationResult ValidateUpdateGameObject(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "updateGameObject requires a 'target' or 'path'");
            return ResolveTarget(action, targetPath, createdPaths);
        }

        private static ActionValidationResult ValidateReparentGameObject(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "reparentGameObject requires a 'target' or 'path'");

            var result = ResolveTarget(action, targetPath, createdPaths);
            if (!string.IsNullOrWhiteSpace(action.newParent))
            {
                var newParentGo = GameObject.Find(action.newParent.TrimStart('/'));
                if (newParentGo == null && !createdPaths.Contains(action.newParent))
                {
                    var suggestions = FindSimilarScenePaths(action.newParent);
                    string suggest = suggestions.Length > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";
                    result.warning = (result.warning != null ? result.warning + " | " : "") +
                                     $"newParent '{action.newParent}' not found in scene.{suggest}";
                }
            }
            return result;
        }

        private static ActionValidationResult ValidateCreateGameObject(EditorAction action)
        {
            if (string.IsNullOrWhiteSpace(action.name))
                return ValidateFail(action, "createGameObject requires a 'name'");
            if (!string.IsNullOrWhiteSpace(action.parent))
            {
                var parentGo = GameObject.Find(action.parent.TrimStart('/'));
                if (parentGo == null)
                {
                    return new ActionValidationResult
                    {
                        action = action.action, isValid = true,
                        warning = $"Parent '{action.parent}' not found (may be created by a prior action)"
                    };
                }
            }
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateCreatePrimitive(EditorAction action)
        {
            if (string.IsNullOrWhiteSpace(action.primitiveType))
                return ValidateFail(action, "createPrimitive requires 'primitiveType' (Cube, Sphere, Capsule, Cylinder, Plane, Quad)");
            var validTypes = new[] { "cube", "sphere", "capsule", "cylinder", "plane", "quad" };
            if (!validTypes.Contains(action.primitiveType.ToLowerInvariant()))
            {
                return new ActionValidationResult
                {
                    action = action.action, isValid = true,
                    warning = $"Unknown primitiveType '{action.primitiveType}'. Valid: {string.Join(", ", validTypes)}"
                };
            }
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateCreateScript(EditorAction action)
        {
            if (string.IsNullOrWhiteSpace(action.assetPath))
                return ValidateFail(action, "createScript requires an 'assetPath'");
            if (!action.assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return new ActionValidationResult { action = action.action, isValid = true, warning = "assetPath should end with .cs" };
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateInstantiatePrefab(EditorAction action)
        {
            string prefab = action.prefabPath ?? action.path;
            if (string.IsNullOrWhiteSpace(prefab))
                return ValidateFail(action, "instantiatePrefab requires 'prefabPath' or 'path'");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            if (asset == null)
            {
                // Fuzzy search for the prefab
                var suggestions = FindSimilarAssets(prefab, "t:Prefab", 3);
                string suggest = suggestions.Length > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";
                return new ActionValidationResult
                {
                    action = action.action, isValid = true,
                    warning = $"Prefab '{prefab}' not found.{suggest}"
                };
            }
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateCreatePrefab(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "createPrefab requires a 'target' or 'path' (the scene GameObject to convert)");
            if (string.IsNullOrWhiteSpace(action.prefabPath) && string.IsNullOrWhiteSpace(action.assetPath))
                return ValidateFail(action, "createPrefab requires 'prefabPath' or 'assetPath' for the output .prefab file");
            return ResolveTarget(action, targetPath, createdPaths);
        }

        private static ActionValidationResult ValidateCreateScriptableObject(EditorAction action)
        {
            if (string.IsNullOrWhiteSpace(action.assetPath))
                return ValidateFail(action, "createScriptableObject requires 'assetPath'");
            if (string.IsNullOrWhiteSpace(action.scriptableObjectType))
                return ValidateFail(action, "createScriptableObject requires 'scriptableObjectType'");
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateAssignMaterial(EditorAction action, HashSet<string> createdPaths)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, "assignMaterial requires a 'target' or 'path'");
            if (string.IsNullOrWhiteSpace(action.materialPath))
                return ValidateFail(action, "assignMaterial requires a 'materialPath'");

            var result = ResolveTarget(action, targetPath, createdPaths);

            // Validate material exists
            var mat = AssetDatabase.LoadAssetAtPath<Material>(action.materialPath);
            if (mat == null)
            {
                var suggestions = FindSimilarAssets(action.materialPath, "t:Material", 3);
                string suggest = suggestions.Length > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";
                result.warning = (result.warning != null ? result.warning + " | " : "") +
                                 $"Material '{action.materialPath}' not found.{suggest}";
            }
            return result;
        }

        private static ActionValidationResult ValidateAssetPathRequired(EditorAction action, string actionName)
        {
            if (string.IsNullOrWhiteSpace(action.assetPath) && string.IsNullOrWhiteSpace(action.path))
                return ValidateFail(action, $"{actionName} requires 'assetPath' or 'path'");
            return ValidateOk(action);
        }

        private static ActionValidationResult ValidateAssetExists(EditorAction action, string assetPath, string expectedExt)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return ValidateFail(action, $"{action.action} requires a valid asset path");

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                string filter = expectedExt != null ? $"t:Object" : "t:Object";
                var suggestions = FindSimilarAssets(assetPath, filter, 3);
                string suggest = suggestions.Length > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";
                return new ActionValidationResult
                {
                    action = action.action, isValid = true,
                    warning = $"Asset '{assetPath}' not found.{suggest}"
                };
            }
            return ValidateOk(action);
        }

        // ── Shared resolution and fuzzy helpers ──────────────────────────

        private static ActionValidationResult ValidateTargetExists(EditorAction action, HashSet<string> createdPaths, bool isDestructive = false)
        {
            string targetPath = action.target ?? action.path;
            if (string.IsNullOrWhiteSpace(targetPath))
                return ValidateFail(action, $"{action.action} requires a 'target' or 'path'");
            return ResolveTarget(action, targetPath, createdPaths, isDestructive);
        }

        /// <summary>
        /// Resolves a target path: checks existence, suggests fuzzy matches if not found.
        /// If the path will be created by a prior action in the batch, returns a warning instead of error.
        /// </summary>
        private static ActionValidationResult ResolveTarget(EditorAction action, string targetPath, HashSet<string> createdPaths, bool isDestructive = false)
        {
            string clean = targetPath.TrimStart('/');
            var go = GameObject.Find(clean);

            if (go != null)
                return ValidateOk(action);

            // Check if a prior action will create this
            if (createdPaths.Contains(targetPath) || createdPaths.Contains(clean))
            {
                return new ActionValidationResult
                {
                    action = action.action, isValid = true,
                    warning = $"'{targetPath}' not in scene yet (will be created by a prior action)"
                };
            }

            // Fuzzy match
            var suggestions = FindSimilarScenePaths(targetPath);
            string suggest = suggestions.Length > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";

            if (isDestructive)
            {
                // Destructive actions should fail-hard if target doesn't exist
                return new ActionValidationResult
                {
                    action = action.action,
                    isValid = false,
                    error = $"Target '{targetPath}' not found in scene.{suggest}"
                };
            }

            return new ActionValidationResult
            {
                action = action.action, isValid = true,
                warning = $"Target '{targetPath}' not found in scene (may be created by a prior action).{suggest}"
            };
        }

        /// <summary>
        /// Find similar scene hierarchy paths using fuzzy name matching.
        /// </summary>
        private static string[] FindSimilarScenePaths(string targetPath, int maxSuggestions = 3)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return Array.Empty<string>();
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid()) return Array.Empty<string>();

                var targetName = targetPath.Contains("/") ? targetPath.Substring(targetPath.LastIndexOf('/') + 1) : targetPath;
                var targetLower = targetName.ToLowerInvariant();
                var candidates = new List<(string path, int dist)>();
                var roots = scene.GetRootGameObjects();

                foreach (var root in roots)
                    CollectFuzzySceneCandidates(root.transform, "", targetLower, candidates, 80);

                return candidates.OrderBy(c => c.dist).Take(maxSuggestions).Select(c => c.path).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static void CollectFuzzySceneCandidates(Transform t, string prefix, string targetLower, List<(string, int)> candidates, int limit)
        {
            if (candidates.Count >= limit) return;
            var path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            var nameLower = t.name.ToLowerInvariant();

            int dist;
            if (nameLower == targetLower) dist = 0;
            else if (nameLower.Contains(targetLower) || targetLower.Contains(nameLower))
                dist = Math.Abs(nameLower.Length - targetLower.Length) + 1;
            else
                dist = LevenshteinDistance(nameLower, targetLower);

            if (dist <= Math.Max(3, targetLower.Length / 2))
                candidates.Add((path, dist));

            for (int i = 0; i < t.childCount; i++)
            {
                if (candidates.Count >= limit) return;
                CollectFuzzySceneCandidates(t.GetChild(i), path, targetLower, candidates, limit);
            }
        }

        /// <summary>
        /// Find similar asset paths using AssetDatabase search + fuzzy file name matching.
        /// </summary>
        private static string[] FindSimilarAssets(string assetPath, string filter, int maxSuggestions)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return Array.Empty<string>();
            try
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
                // Search for assets with similar name
                var guids = AssetDatabase.FindAssets(fileName, new[] { "Assets" });
                var paths = guids
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct()
                    .Take(50)
                    .ToArray();

                if (paths.Length == 0) return Array.Empty<string>();

                return paths
                    .Select(p => (path: p, dist: LevenshteinDistance(
                        System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant(), fileName)))
                    .OrderBy(x => x.dist)
                    .Take(maxSuggestions)
                    .Select(x => x.path)
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int la = a.Length, lb = b.Length;
            if (la > 30) { a = a.Substring(0, 30); la = 30; }
            if (lb > 30) { b = b.Substring(0, 30); lb = 30; }
            var prev = new int[lb + 1];
            var curr = new int[lb + 1];
            for (int j = 0; j <= lb; j++) prev[j] = j;
            for (int i = 1; i <= la; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[lb];
        }

        // ── Result helpers ──

        private static ActionValidationResult ValidateOk(EditorAction action)
        {
            return new ActionValidationResult { action = action.action, isValid = true };
        }

        private static ActionValidationResult ValidateFail(EditorAction action, string error)
        {
            return new ActionValidationResult { action = action.action, isValid = false, error = error };
        }
    }

    [Serializable]
    public class ActionValidationResult
    {
        public string action;
        public bool isValid;
        public string error;
        public string warning;
    }
}
