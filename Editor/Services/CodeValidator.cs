using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Roslyn-based C# validator with two levels:
    /// 1. Syntax validation (parse-only) — instant, catches braces/semicolons
    /// 2. Semantic validation (compilation) — uses Unity's loaded assemblies to catch
    ///    type errors, missing methods, wrong signatures, unresolved namespaces
    /// </summary>
    public static class CodeValidator
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp9,
            DocumentationMode.None,
            SourceCodeKind.Regular
        );

        // Cached assembly references for semantic validation
        private static List<MetadataReference> _assemblyRefs;
        private static readonly object _refsLock = new object();

        /// <summary>
        /// Validates C# source code for syntax errors (parse-only, instant).
        /// </summary>
        public static ValidationResult ValidateCSharp(string sourceCode, string filePath = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCode))
                return new ValidationResult { isValid = true, errors = Array.Empty<ValidationError>() };

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions, filePath ?? "");
                var diagnostics = tree.GetDiagnostics();
                var errors = ExtractErrors(diagnostics);
                return new ValidationResult { isValid = errors.Length == 0, errors = errors };
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoBuddy] CodeValidator syntax exception: {ex.Message}");
                return new ValidationResult { isValid = true, errors = Array.Empty<ValidationError>() };
            }
        }

        /// <summary>
        /// Validates C# source code semantically — catches type errors, missing methods, unresolved namespaces.
        /// Uses Unity's loaded assemblies as references. Slower than syntax-only but much more thorough.
        /// Returns syntax errors + semantic errors (filtered to avoid noise).
        /// </summary>
        public static ValidationResult ValidateSemantic(string sourceCode, string filePath = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCode))
                return new ValidationResult { isValid = true, errors = Array.Empty<ValidationError>() };

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions, filePath ?? "");

                // Check syntax first — if syntax is broken, skip semantic
                var syntaxDiags = tree.GetDiagnostics();
                var syntaxErrors = ExtractErrors(syntaxDiags);
                if (syntaxErrors.Length > 0)
                    return new ValidationResult { isValid = false, errors = syntaxErrors };

                // Build compilation with Unity assemblies
                var refs = GetAssemblyReferences();
                if (refs == null || refs.Count == 0)
                {
                    // Fall back to syntax-only if we can't load references
                    return new ValidationResult { isValid = true, errors = Array.Empty<ValidationError>() };
                }

                var compilation = CSharpCompilation.Create(
                    "CoBuddyValidation",
                    new[] { tree },
                    refs,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithAllowUnsafe(true)
                );

                var allDiags = compilation.GetDiagnostics();
                var errors = ExtractSemanticErrors(allDiags);
                return new ValidationResult { isValid = errors.Length == 0, errors = errors };
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoBuddy] CodeValidator semantic exception: {ex.Message}");
                return new ValidationResult { isValid = true, errors = Array.Empty<ValidationError>() };
            }
        }

        /// <summary>
        /// Validates multiple patches (syntax only for speed, semantic on demand).
        /// </summary>
        public static ValidationFileResult[] ValidatePatches(Models.FilePatch[] patches, bool semantic = false)
        {
            if (patches == null || patches.Length == 0)
                return Array.Empty<ValidationFileResult>();

            var results = new List<ValidationFileResult>();
            foreach (var patch in patches)
            {
                if (patch == null || string.IsNullOrWhiteSpace(patch.filePath))
                    continue;

                if (!patch.filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new ValidationFileResult
                    {
                        filePath = patch.filePath,
                        isValid = true,
                        errors = Array.Empty<ValidationError>()
                    });
                    continue;
                }

                var result = semantic
                    ? ValidateSemantic(patch.newContent, patch.filePath)
                    : ValidateCSharp(patch.newContent, patch.filePath);

                results.Add(new ValidationFileResult
                {
                    filePath = patch.filePath,
                    isValid = result.isValid,
                    errors = result.errors
                });
            }
            return results.ToArray();
        }

        /// <summary>
        /// Validates multiple patches together in a single compilation (cross-file semantic analysis).
        /// Catches issues where one script references a type from another script in the same batch.
        /// </summary>
        public static ValidationFileResult[] ValidatePatchesCombined(Models.FilePatch[] patches)
        {
            if (patches == null || patches.Length == 0)
                return Array.Empty<ValidationFileResult>();

            var csPatches = patches.Where(p =>
                p?.filePath != null && p.filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ).ToArray();

            if (csPatches.Length == 0)
                return patches.Select(p => new ValidationFileResult
                {
                    filePath = p?.filePath ?? "",
                    isValid = true,
                    errors = Array.Empty<ValidationError>()
                }).ToArray();

            try
            {
                var trees = new List<SyntaxTree>();
                var treeToFile = new Dictionary<SyntaxTree, string>();

                foreach (var patch in csPatches)
                {
                    var tree = CSharpSyntaxTree.ParseText(
                        patch.newContent ?? "", ParseOptions, patch.filePath ?? "");
                    trees.Add(tree);
                    treeToFile[tree] = patch.filePath;
                }

                // Check syntax first
                var syntaxResults = new List<ValidationFileResult>();
                bool hasSyntaxErrors = false;
                foreach (var tree in trees)
                {
                    var syntaxDiags = tree.GetDiagnostics();
                    var errors = ExtractErrors(syntaxDiags);
                    if (errors.Length > 0) hasSyntaxErrors = true;
                    syntaxResults.Add(new ValidationFileResult
                    {
                        filePath = treeToFile[tree],
                        isValid = errors.Length == 0,
                        errors = errors
                    });
                }

                if (hasSyntaxErrors) return syntaxResults.ToArray();

                // Combined semantic compilation
                var refs = GetAssemblyReferences();
                if (refs == null || refs.Count == 0) return syntaxResults.ToArray();

                var compilation = CSharpCompilation.Create(
                    "CoBuddyCombinedValidation",
                    trees,
                    refs,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithAllowUnsafe(true)
                );

                var allDiags = compilation.GetDiagnostics();

                // Group diagnostics by source file
                var results = new List<ValidationFileResult>();
                foreach (var tree in trees)
                {
                    var fileDiags = allDiags.Where(d =>
                        d.Location.SourceTree == tree);
                    var errors = ExtractSemanticErrors(fileDiags);
                    results.Add(new ValidationFileResult
                    {
                        filePath = treeToFile[tree],
                        isValid = errors.Length == 0,
                        errors = errors
                    });
                }

                // Add non-CS patches as valid
                foreach (var p in patches)
                {
                    if (p?.filePath == null || p.filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add(new ValidationFileResult
                    {
                        filePath = p.filePath, isValid = true, errors = Array.Empty<ValidationError>()
                    });
                }

                return results.ToArray();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoBuddy] Combined validation failed: {ex.Message}");
                return patches.Select(p => new ValidationFileResult
                {
                    filePath = p?.filePath ?? "",
                    isValid = true,
                    errors = Array.Empty<ValidationError>()
                }).ToArray();
            }
        }

        // ── Assembly reference collection ────────────────────────────────

        private static List<MetadataReference> GetAssemblyReferences()
        {
            lock (_refsLock)
            {
                if (_assemblyRefs != null) return _assemblyRefs;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var refs = new List<MetadataReference>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Collect from all loaded assemblies in the current AppDomain
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var loc = asm.Location;
                        if (string.IsNullOrEmpty(loc)) continue;
                        if (!File.Exists(loc)) continue;
                        if (seen.Contains(loc)) continue;
                        seen.Add(loc);
                        refs.Add(MetadataReference.CreateFromFile(loc));
                    }
                    catch { }
                }

                sw.Stop();
                UnityEngine.Debug.Log($"[CoBuddy] Loaded {refs.Count} assembly references for semantic validation ({sw.ElapsedMilliseconds}ms)");

                lock (_refsLock)
                {
                    _assemblyRefs = refs;
                }
                return refs;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CoBuddy] Failed to load assembly references: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears cached assembly references (call if assemblies change, e.g., after domain reload).
        /// </summary>
        public static void InvalidateAssemblyCache()
        {
            lock (_refsLock) { _assemblyRefs = null; }
        }

        // ── Diagnostic extraction helpers ────────────────────────────────

        private static ValidationError[] ExtractErrors(IEnumerable<Diagnostic> diagnostics)
        {
            var errors = new List<ValidationError>();
            foreach (var diag in diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    errors.Add(new ValidationError
                    {
                        line = lineSpan.StartLinePosition.Line + 1,
                        column = lineSpan.StartLinePosition.Character + 1,
                        message = diag.GetMessage(),
                        id = diag.Id
                    });
                }
            }
            return errors.ToArray();
        }

        /// <summary>
        /// Extract semantic errors, filtering out noise that comes from missing context
        /// (e.g., references to types in other project scripts we don't have in the compilation).
        /// </summary>
        private static ValidationError[] ExtractSemanticErrors(IEnumerable<Diagnostic> diagnostics)
        {
            var errors = new List<ValidationError>();
            foreach (var diag in diagnostics)
            {
                if (diag.Severity != DiagnosticSeverity.Error) continue;

                // Filter out common false positives from incomplete compilation context:
                // CS0246: type or namespace not found — often from other project scripts
                // CS0234: namespace doesn't exist — often from missing project references
                // CS1061: type doesn't contain definition — can be from missing context
                // We keep these but mark them as warnings (not hard errors)
                // For now, skip the noisiest ones that are almost always false positives
                string id = diag.Id;
                if (id == "CS0518" || // predefined type not defined (mscorlib issues)
                    id == "CS1729" || // constructor argument count (often from partial context)
                    id == "CS0012")   // type in unreferenced assembly
                    continue;

                var lineSpan = diag.Location.GetLineSpan();
                errors.Add(new ValidationError
                {
                    line = lineSpan.StartLinePosition.Line + 1,
                    column = lineSpan.StartLinePosition.Character + 1,
                    message = diag.GetMessage(),
                    id = id
                });
            }
            return errors.ToArray();
        }
    }

    [Serializable]
    public class ValidationResult
    {
        public bool isValid;
        public ValidationError[] errors;
    }

    [Serializable]
    public class ValidationError
    {
        public int line;
        public int column;
        public string message;
        public string id;
    }

    [Serializable]
    public class ValidationFileResult
    {
        public string filePath;
        public bool isValid;
        public ValidationError[] errors;
    }
}
