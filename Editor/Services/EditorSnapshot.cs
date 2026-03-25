using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Captures and restores editor state (loaded scenes, active scene, prefab stage,
    /// camera position, selection) around action batches. On error, state is restored
    /// to pre-batch conditions. Usage: using (EditorSnapshot.Capture()) { /* actions */ }
    /// </summary>
    public struct EditorSnapshot : IDisposable
    {
        private List<string> _loadedScenePaths;
        private string _activeScenePath;
        private string _prefabStagePath;
        private Vector3 _cameraPivot;
        private Quaternion _cameraRotation;
        private float _cameraSize;
        private bool _cameraIs2D;
        private UnityEngine.Object[] _selection;
        private bool _disposed;
        private bool _restoreOnDispose;

        /// <summary>
        /// Captures the current editor state. Dispose to restore.
        /// </summary>
        public static EditorSnapshot Capture(bool restoreOnDispose = true)
        {
            var snap = new EditorSnapshot();
            snap._restoreOnDispose = restoreOnDispose;
            snap._disposed = false;

            try
            {
                // Capture loaded scenes
                snap._loadedScenePaths = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                        snap._loadedScenePaths.Add(scene.path);
                }

                // Active scene
                var activeScene = SceneManager.GetActiveScene();
                snap._activeScenePath = activeScene.IsValid() ? activeScene.path : null;

                // Prefab stage
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                snap._prefabStagePath = prefabStage != null
                    ? prefabStage.assetPath
                    : null;

                // Scene view camera
                var sv = SceneView.lastActiveSceneView;
                if (sv != null)
                {
                    snap._cameraPivot = sv.pivot;
                    snap._cameraRotation = sv.rotation;
                    snap._cameraSize = sv.size;
                    snap._cameraIs2D = sv.in2DMode;
                }

                // Selection
                snap._selection = Selection.objects != null
                    ? (UnityEngine.Object[])Selection.objects.Clone()
                    : Array.Empty<UnityEngine.Object>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] EditorSnapshot.Capture failed: {ex.Message}");
            }

            return snap;
        }

        /// <summary>
        /// Restores the captured editor state.
        /// </summary>
        public void Restore()
        {
            try
            {
                // Restore prefab stage
                var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                string currentPrefabPath = currentPrefabStage?.assetPath;

                if (_prefabStagePath != currentPrefabPath)
                {
                    if (string.IsNullOrEmpty(_prefabStagePath))
                    {
                        // Was not in prefab stage, but now we are — exit it
                        if (currentPrefabStage != null)
                            StageUtility.GoToMainStage();
                    }
                    else
                    {
                        // Was in a prefab stage — reopen it
                        PrefabStageUtility.OpenPrefab(_prefabStagePath);
                    }
                }

                // Restore loaded scenes (only if not in prefab stage)
                if (string.IsNullOrEmpty(_prefabStagePath) && _loadedScenePaths != null)
                {
                    // Check if scenes changed
                    var currentScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                            currentScenes.Add(scene.path);
                    }

                    var targetScenes = new HashSet<string>(_loadedScenePaths, StringComparer.OrdinalIgnoreCase);
                    bool scenesChanged = !currentScenes.SetEquals(targetScenes);

                    if (scenesChanged)
                    {
                        RestoreScenes();
                    }
                }

                // Restore active scene
                if (!string.IsNullOrEmpty(_activeScenePath))
                {
                    var activeScene = SceneManager.GetActiveScene();
                    if (activeScene.path != _activeScenePath)
                    {
                        var target = SceneManager.GetSceneByPath(_activeScenePath);
                        if (target.IsValid() && target.isLoaded)
                            SceneManager.SetActiveScene(target);
                    }
                }

                // Restore camera
                var sv = SceneView.lastActiveSceneView;
                if (sv != null)
                {
                    sv.pivot = _cameraPivot;
                    sv.rotation = _cameraRotation;
                    sv.size = _cameraSize;
                    sv.in2DMode = _cameraIs2D;
                    sv.Repaint();
                }

                // Restore selection (only non-null, still-alive objects)
                if (_selection != null && _selection.Length > 0)
                {
                    var alive = new List<UnityEngine.Object>();
                    foreach (var obj in _selection)
                    {
                        if (obj != null) alive.Add(obj);
                    }
                    if (alive.Count > 0)
                        Selection.objects = alive.ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] EditorSnapshot.Restore failed: {ex.Message}");
            }
        }

        private void RestoreScenes()
        {
            if (_loadedScenePaths == null || _loadedScenePaths.Count == 0) return;

            try
            {
                // Open the first scene (replaces current)
                bool first = true;
                foreach (var path in _loadedScenePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    try
                    {
                        if (first)
                        {
                            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                            first = false;
                        }
                        else
                        {
                            EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CoBuddy] Failed to restore scene {path}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] RestoreScenes failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a summary of captured state for logging.
        /// </summary>
        public string Summary()
        {
            return $"Scenes: [{string.Join(", ", _loadedScenePaths ?? new List<string>())}], " +
                   $"Active: {_activeScenePath ?? "none"}, " +
                   $"PrefabStage: {_prefabStagePath ?? "none"}, " +
                   $"Selection: {_selection?.Length ?? 0} objects";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_restoreOnDispose)
                Restore();
        }
    }
}
