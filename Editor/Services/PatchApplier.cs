using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    public static class PatchApplier
    {
        public static LastAppliedChange ApplyPatch(FilePatch patch, bool deferRefresh = false)
        {
            if (patch == null)
                throw new Exception("Patch is null.");

            if (string.IsNullOrWhiteSpace(patch.filePath))
                throw new Exception("Patch file path is missing.");

            if (patch.newContent == null)
                throw new Exception("Patch new content is null.");

            // Prefer Unity project root; fallback to CWD for compatibility
            string fullPath;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (!string.IsNullOrEmpty(projectRoot))
            {
                fullPath = Path.GetFullPath(Path.Combine(projectRoot, patch.filePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                fullPath = Path.GetFullPath(patch.filePath);
            }
            string normalized = fullPath.Replace("\\", "/");

            if (!normalized.Contains("/Assets/"))
                throw new Exception("Only files inside Assets are allowed in this prototype.");

            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new Exception("Invalid patch directory.");

            bool fileExistedBefore = File.Exists(fullPath);
            string previousContent = fileExistedBefore ? File.ReadAllText(fullPath) : "";

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write file immediately — no pre-validation (Roslyn runs post-write if needed)
            File.WriteAllText(fullPath, patch.newContent);

            // Use targeted ImportAsset instead of global Refresh when possible
            if (!deferRefresh)
            {
                // Convert to Unity-relative path for ImportAsset
                string unityPath = patch.filePath.Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/") && !unityPath.StartsWith("Packages/"))
                {
                    // Try to extract Assets-relative path
                    int assetsIdx = unityPath.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (assetsIdx >= 0)
                        unityPath = unityPath.Substring(assetsIdx);
                }
                try
                {
                    AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.Default);
                }
                catch
                {
                    AssetDatabase.Refresh(); // Fallback to global refresh
                }
            }

            return new LastAppliedChange
            {
                filePath = patch.filePath,
                previousContent = previousContent,
                fileExistedBefore = fileExistedBefore,
                syntaxErrors = null
            };
        }

        public static void RevertLastChange(LastAppliedChange change)
        {
            if (change == null)
                throw new Exception("No last change to revert.");

            if (string.IsNullOrWhiteSpace(change.filePath))
                throw new Exception("Last change file path is missing.");

            string fullPath;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (!string.IsNullOrEmpty(projectRoot))
            {
                fullPath = Path.GetFullPath(Path.Combine(projectRoot, change.filePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                fullPath = Path.GetFullPath(change.filePath);
            }
            string normalized = fullPath.Replace("\\", "/");

            if (!normalized.Contains("/Assets/"))
                throw new Exception("Only files inside Assets are allowed in this prototype.");

            if (change.fileExistedBefore)
            {
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, change.previousContent ?? "");
            }
            else
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                string metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }

            AssetDatabase.Refresh();
        }
    }
}
