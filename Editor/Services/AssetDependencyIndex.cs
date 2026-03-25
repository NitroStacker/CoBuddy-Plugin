using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Pre-computed asset dependency reverse-lookup index.
    /// Enables O(1) "which assets reference this asset?" queries.
    /// Lazily built and cached with TTL.
    /// </summary>
    public static class AssetDependencyIndex
    {
        // Forward: asset → assets it depends on (direct only)
        private static Dictionary<string, HashSet<string>> _forwardDeps;
        // Reverse: asset → assets that depend on it
        private static Dictionary<string, HashSet<string>> _reverseDeps;
        private static DateTime _builtAt = DateTime.MinValue;
        private static readonly object _lock = new object();
        private static bool _building = false;

        private const double CacheTtlSeconds = 120; // Rebuild every 2 minutes at most

        /// <summary>
        /// Returns all assets that reference the given asset path (reverse lookup).
        /// Returns empty array if not indexed yet.
        /// </summary>
        public static string[] GetReferencedBy(string assetPath, bool triggerBuild = true)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return Array.Empty<string>();
            lock (_lock)
            {
                if (_reverseDeps == null && triggerBuild)
                {
                    BuildIndex();
                }
                if (_reverseDeps == null) return Array.Empty<string>();
                var key = assetPath.Replace('\\', '/');
                return _reverseDeps.TryGetValue(key, out var refs) ? refs.ToArray() : Array.Empty<string>();
            }
        }

        /// <summary>
        /// Returns direct dependencies of the given asset (forward lookup).
        /// </summary>
        public static string[] GetDependencies(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return Array.Empty<string>();
            lock (_lock)
            {
                if (_forwardDeps == null) BuildIndex();
                if (_forwardDeps == null) return Array.Empty<string>();
                var key = assetPath.Replace('\\', '/');
                return _forwardDeps.TryGetValue(key, out var deps) ? deps.ToArray() : Array.Empty<string>();
            }
        }

        /// <summary>
        /// Returns true if the index is built and fresh.
        /// </summary>
        public static bool IsReady => _reverseDeps != null && (DateTime.UtcNow - _builtAt).TotalSeconds < CacheTtlSeconds;

        /// <summary>
        /// Number of indexed assets.
        /// </summary>
        public static int AssetCount => _forwardDeps?.Count ?? 0;

        /// <summary>
        /// Builds the index synchronously. Should be called from main thread or background.
        /// Thread-safe (only one build at a time).
        /// </summary>
        public static void BuildIndex()
        {
            lock (_lock)
            {
                if (_building) return;
                if (IsReady) return;
                _building = true;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var forward = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var reverse = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                // Scan all assets under Assets/
                var guids = AssetDatabase.FindAssets("", new[] { "Assets" });
                var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(p) && !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        allPaths.Add(p);
                }

                // Process in batches to avoid stalling
                int processed = 0;
                foreach (var path in allPaths)
                {
                    try
                    {
                        // Direct dependencies only (recursive=false for forward map)
                        var deps = AssetDatabase.GetDependencies(path, false);
                        var directDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var dep in deps)
                        {
                            if (string.IsNullOrEmpty(dep) || dep.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (string.Equals(dep, path, StringComparison.OrdinalIgnoreCase))
                                continue;
                            directDeps.Add(dep);

                            // Build reverse index
                            if (!reverse.TryGetValue(dep, out var refSet))
                            {
                                refSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                reverse[dep] = refSet;
                            }
                            refSet.Add(path);
                        }
                        forward[path] = directDeps;
                    }
                    catch
                    {
                        // Skip problematic assets
                    }
                    processed++;
                }

                lock (_lock)
                {
                    _forwardDeps = forward;
                    _reverseDeps = reverse;
                    _builtAt = DateTime.UtcNow;
                }

                sw.Stop();
                Debug.Log($"[CoBuddy] Asset dependency index built: {processed} assets, {reverse.Count} with references, {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] Asset dependency index build failed: {ex.Message}");
            }
            finally
            {
                lock (_lock) { _building = false; }
            }
        }

        /// <summary>
        /// Invalidates the cache, forcing a rebuild on next query.
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                _forwardDeps = null;
                _reverseDeps = null;
                _builtAt = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Incremental update: when a single asset changes, update only its dependencies.
        /// Much faster than full rebuild for single-file edits.
        /// </summary>
        public static void UpdateAsset(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            lock (_lock)
            {
                if (_forwardDeps == null || _reverseDeps == null) return;

                var key = assetPath.Replace('\\', '/');

                // Remove old forward deps from reverse index
                if (_forwardDeps.TryGetValue(key, out var oldDeps))
                {
                    foreach (var dep in oldDeps)
                    {
                        if (_reverseDeps.TryGetValue(dep, out var refSet))
                            refSet.Remove(key);
                    }
                }

                // Recompute forward deps
                try
                {
                    var deps = AssetDatabase.GetDependencies(key, false);
                    var newDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dep in deps)
                    {
                        if (string.IsNullOrEmpty(dep) || dep.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(dep, key, StringComparison.OrdinalIgnoreCase))
                            continue;
                        newDeps.Add(dep);

                        if (!_reverseDeps.TryGetValue(dep, out var refSet))
                        {
                            refSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _reverseDeps[dep] = refSet;
                        }
                        refSet.Add(key);
                    }
                    _forwardDeps[key] = newDeps;
                }
                catch { }
            }
        }
    }
}
