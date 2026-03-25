using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Scans the active scene hierarchy and returns GameObject paths for the index.
    /// Must run on main thread.
    /// </summary>
    public static class SceneHierarchyScanner
    {
        private const int MaxPaths = 500;

        public static List<string> GetActiveScenePaths()
        {
            var paths = new List<string>();
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return paths;

                var roots = new List<GameObject>();
                scene.GetRootGameObjects(roots);

                foreach (var root in roots)
                {
                    if (root == null) continue;
                    CollectPaths(root.transform, "", paths);
                    if (paths.Count >= MaxPaths) break;
                }

                if (paths.Count >= MaxPaths)
                    paths.Add("... (truncated)");
            }
            catch (Exception)
            {
                // Return empty on any error
            }
            return paths;
        }

        private static void CollectPaths(Transform t, string parentPath, List<string> paths)
        {
            if (t == null || paths.Count >= MaxPaths) return;

            string path = string.IsNullOrEmpty(parentPath) ? t.gameObject.name : parentPath + "/" + t.gameObject.name;
            paths.Add(path);

            for (int i = 0; i < t.childCount && paths.Count < MaxPaths; i++)
            {
                var child = t.GetChild(i);
                if (child != null)
                    CollectPaths(child, path, paths);
            }
        }
    }
}
