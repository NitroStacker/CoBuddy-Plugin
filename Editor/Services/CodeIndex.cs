using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    [InitializeOnLoad]
    public static class CodeIndex
    {
        private static readonly Dictionary<string, ScriptIndexEntry> ScriptsByPath =
            new Dictionary<string, ScriptIndexEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> PathByClassName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<DependencyEdge> Edges = new List<DependencyEdge>();

        private static bool isInitialized;

        static CodeIndex()
        {
            Initialize();
        }

        public static void Initialize()
        {
            if (isInitialized)
                return;

            isInitialized = true;

            RebuildIndex();
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationFinished(object context)
        {
            RebuildIndex();
        }

        public static void RebuildIndex()
        {
            ScriptsByPath.Clear();
            PathByClassName.Clear();
            Edges.Clear();

            List<ScriptFileData> scripts = ProjectScanner.GetAllScriptsInAssets();

            foreach (ScriptFileData script in scripts)
            {
                if (script == null || string.IsNullOrWhiteSpace(script.path))
                    continue;

                ScriptIndexEntry entry = ParseScript(script.path, script.content);
                if (entry == null)
                    continue;

                ScriptsByPath[entry.path] = entry;

                if (!string.IsNullOrWhiteSpace(entry.className) &&
                    !PathByClassName.ContainsKey(entry.className))
                {
                    PathByClassName[entry.className] = entry.path;
                }
            }

            BuildDependencyEdges();

            Debug.Log($"CoBuddy CodeIndex rebuilt. Scripts: {ScriptsByPath.Count}, Edges: {Edges.Count}");
        }

        public static CodeIndexSnapshot GetSnapshot()
        {
            return new CodeIndexSnapshot
            {
                scripts = ScriptsByPath.Values.OrderBy(s => s.path).ToArray(),
                edges = Edges.ToArray()
            };
        }

        public static ScriptIndexEntry GetScriptByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            ScriptsByPath.TryGetValue(path, out ScriptIndexEntry entry);
            return entry;
        }

        public static ScriptIndexEntry GetScriptByClassName(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
                return null;

            if (!PathByClassName.TryGetValue(className, out string path))
                return null;

            return GetScriptByPath(path);
        }

        public static ScriptIndexEntry[] GetDependentsOfClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
                return Array.Empty<ScriptIndexEntry>();

            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DependencyEdge edge in Edges)
            {
                if (string.Equals(edge.symbol, className, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(edge.fromPath);
                }
            }

            return paths
                .Select(GetScriptByPath)
                .Where(x => x != null)
                .OrderBy(x => x.path)
                .ToArray();
        }

        public static ScriptIndexEntry[] Search(string query, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<ScriptIndexEntry>();

            string[] terms = ExtractTerms(query).ToArray();
            if (terms.Length == 0)
                return Array.Empty<ScriptIndexEntry>();

            List<SymbolSearchResult> results = new List<SymbolSearchResult>();

            foreach (ScriptIndexEntry entry in ScriptsByPath.Values)
            {
                int score = ScoreEntry(entry, terms);
                if (score <= 0)
                    continue;

                results.Add(new SymbolSearchResult
                {
                    script = entry,
                    score = score
                });
            }

            return results
                .OrderByDescending(r => r.score)
                .ThenBy(r => r.script.path)
                .Take(maxResults)
                .Select(r => r.script)
                .ToArray();
        }

        private static void BuildDependencyEdges()
        {
            foreach (ScriptIndexEntry entry in ScriptsByPath.Values)
            {
                if (entry.referencedTypes == null)
                    continue;

                foreach (string symbol in entry.referencedTypes)
                {
                    if (string.IsNullOrWhiteSpace(symbol))
                        continue;

                    if (!PathByClassName.TryGetValue(symbol, out string targetPath))
                        continue;

                    if (string.Equals(entry.path, targetPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Edges.Add(new DependencyEdge
                    {
                        fromPath = entry.path,
                        toPath = targetPath,
                        symbol = symbol
                    });
                }
            }
        }

        private static ScriptIndexEntry ParseScript(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            // Try Roslyn-based parsing first (more accurate), fall back to regex
            try
            {
                var summary = CodeSummarizer.Summarize(content, path, 8000);
                if (summary.className != null)
                {
                    return new ScriptIndexEntry
                    {
                        path = path,
                        className = summary.className,
                        baseClass = summary.baseClass,
                        interfaces = summary.interfaces ?? System.Array.Empty<string>(),
                        methods = summary.methods ?? System.Array.Empty<string>(),
                        referencedTypes = ExtractReferencedTypes(content)
                    };
                }
            }
            catch
            {
                // Roslyn parse failed — fall through to regex
            }

            ScriptIndexEntry entry = new ScriptIndexEntry
            {
                path = path,
                className = ExtractFirstClassName(content),
                baseClass = ExtractBaseClass(content),
                interfaces = ExtractInterfaces(content),
                methods = ExtractMethodNames(content),
                referencedTypes = ExtractReferencedTypes(content)
            };

            return entry;
        }

        private static int ScoreEntry(ScriptIndexEntry entry, string[] terms)
        {
            int score = 0;

            foreach (string term in terms)
            {
                if (Contains(entry.path, term))
                    score += 20;

                if (Contains(entry.className, term))
                    score += 100;

                if (Contains(entry.baseClass, term))
                    score += 40;

                if (entry.interfaces != null)
                {
                    foreach (string iface in entry.interfaces)
                    {
                        if (Contains(iface, term))
                            score += 35;
                    }
                }

                if (entry.methods != null)
                {
                    foreach (string method in entry.methods)
                    {
                        if (Contains(method, term))
                            score += 50;
                    }
                }

                if (entry.referencedTypes != null)
                {
                    foreach (string type in entry.referencedTypes)
                    {
                        if (Contains(type, term))
                            score += 15;
                    }
                }
            }

            return score;
        }

        private static string ExtractFirstClassName(string content)
        {
            Match match = Regex.Match(content, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string ExtractBaseClass(string content)
        {
            Match match = Regex.Match(content, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*([A-Za-z_][A-Za-z0-9_]*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string[] ExtractInterfaces(string content)
        {
            Match match = Regex.Match(content, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*([^{]+)");
            if (!match.Success)
                return Array.Empty<string>();

            string inheritanceClause = match.Groups[1].Value;
            string[] parts = inheritanceClause
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            if (parts.Length <= 1)
                return Array.Empty<string>();

            return parts.Skip(1).ToArray();
        }

        private static string[] ExtractMethodNames(string content)
        {
            MatchCollection matches = Regex.Matches(
                content,
                @"\b(?:public|private|protected|internal)?\s*(?:static\s+)?(?:async\s+)?(?:[A-Za-z_<>\[\],]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*\(");

            HashSet<string> methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                    methods.Add(match.Groups[1].Value);
            }

            return methods.OrderBy(m => m).ToArray();
        }

        private static string[] ExtractReferencedTypes(string content)
        {
            HashSet<string> symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            MatchCollection newMatches = Regex.Matches(content, @"\bnew\s+([A-Z][A-Za-z0-9_]*)");
            foreach (Match match in newMatches)
            {
                symbols.Add(match.Groups[1].Value);
            }

            MatchCollection genericMatches = Regex.Matches(content, @"\b(?:List|HashSet|Dictionary|Queue|Stack)<\s*([A-Z][A-Za-z0-9_]*)");
            foreach (Match match in genericMatches)
            {
                symbols.Add(match.Groups[1].Value);
            }

            MatchCollection memberMatches = Regex.Matches(content, @"\b([A-Z][A-Za-z0-9_]*)\s*\.");
            foreach (Match match in memberMatches)
            {
                symbols.Add(match.Groups[1].Value);
            }

            MatchCollection typeDeclMatches = Regex.Matches(
                content,
                @"\b(?:public|private|protected|internal)\s+(?:static\s+)?([A-Z][A-Za-z0-9_]*)\s+[a-zA-Z_][A-Za-z0-9_]*\s*[;=)]");
            foreach (Match match in typeDeclMatches)
            {
                symbols.Add(match.Groups[1].Value);
            }

            string className = ExtractFirstClassName(content);
            if (!string.IsNullOrWhiteSpace(className))
            {
                symbols.Remove(className);
            }

            return symbols.OrderBy(s => s).ToArray();
        }

        private static IEnumerable<string> ExtractTerms(string query)
        {
            MatchCollection matches = Regex.Matches(query ?? "", @"[A-Za-z_][A-Za-z0-9_]{2,}");
            foreach (Match match in matches)
            {
                yield return match.Value;
            }
        }

        private static bool Contains(string source, string term)
        {
            return !string.IsNullOrWhiteSpace(source) &&
                   !string.IsNullOrWhiteSpace(term) &&
                   source.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
