using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Roslyn AST-based code summarizer. Extracts class/method/field/property signatures
    /// without method bodies. Used for compact LLM context (much smaller than full source).
    /// </summary>
    public static class CodeSummarizer
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp9,
            DocumentationMode.None,
            SourceCodeKind.Regular
        );

        /// <summary>
        /// Summarizes C# source code into signatures only (no method bodies).
        /// Returns a compact string within the given character budget.
        /// </summary>
        public static CodeSummary Summarize(string sourceCode, string filePath = null, int maxChars = 4000)
        {
            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                return new CodeSummary
                {
                    summary = "",
                    className = null,
                    baseClass = null,
                    interfaces = Array.Empty<string>(),
                    methods = Array.Empty<string>(),
                    fields = Array.Empty<string>(),
                    properties = Array.Empty<string>(),
                    usings = Array.Empty<string>()
                };
            }

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions, filePath ?? "");
                var root = tree.GetCompilationUnitRoot();
                var walker = new SummaryWalker(maxChars);
                walker.Visit(root);
                return walker.BuildSummary();
            }
            catch (Exception ex)
            {
                // Fallback: return truncated source
                UnityEngine.Debug.LogWarning($"[CoBuddy] CodeSummarizer exception: {ex.Message}");
                return new CodeSummary
                {
                    summary = sourceCode.Length > maxChars ? sourceCode.Substring(0, maxChars) : sourceCode,
                    className = null,
                    baseClass = null,
                    interfaces = Array.Empty<string>(),
                    methods = Array.Empty<string>(),
                    fields = Array.Empty<string>(),
                    properties = Array.Empty<string>(),
                    usings = Array.Empty<string>()
                };
            }
        }

        private class SummaryWalker : CSharpSyntaxWalker
        {
            private readonly int _maxChars;
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly List<string> _usings = new List<string>();
            private readonly List<string> _methods = new List<string>();
            private readonly List<string> _fields = new List<string>();
            private readonly List<string> _properties = new List<string>();
            private readonly List<string> _interfaces = new List<string>();
            private string _className;
            private string _baseClass;
            private string _namespace;
            private bool _budgetExhausted;

            public SummaryWalker(int maxChars) : base(SyntaxWalkerDepth.Node)
            {
                _maxChars = maxChars;
            }

            private bool TryAppend(string text)
            {
                if (_budgetExhausted) return false;
                if (_sb.Length + text.Length > _maxChars)
                {
                    _budgetExhausted = true;
                    return false;
                }
                _sb.Append(text);
                return true;
            }

            public override void VisitUsingDirective(UsingDirectiveSyntax node)
            {
                string u = node.ToString().Trim();
                _usings.Add(u);
                TryAppend(u + "\n");
            }

            public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            {
                _namespace = node.Name.ToString();
                TryAppend($"namespace {_namespace}\n{{\n");
                base.VisitNamespaceDeclaration(node);
                TryAppend("}\n");
            }

            public override void VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                _className = node.Identifier.Text;

                // Extract base types
                if (node.BaseList != null)
                {
                    bool first = true;
                    foreach (var baseType in node.BaseList.Types)
                    {
                        string typeName = baseType.Type.ToString();
                        if (first)
                        {
                            _baseClass = typeName;
                            first = false;
                        }
                        else
                        {
                            _interfaces.Add(typeName);
                        }
                    }
                }

                // Write class declaration with attributes
                foreach (var attrList in node.AttributeLists)
                {
                    TryAppend($"    {attrList.ToString().Trim()}\n");
                }

                string modifiers = node.Modifiers.ToString();
                string baseList = node.BaseList != null ? $" : {node.BaseList}" : "";
                TryAppend($"    {modifiers} class {_className}{baseList}\n    {{\n");

                // Visit members but skip method bodies
                foreach (var member in node.Members)
                {
                    if (_budgetExhausted) break;
                    VisitMember(member);
                }

                TryAppend("    }\n");
            }

            public override void VisitStructDeclaration(StructDeclarationSyntax node)
            {
                if (_className == null) _className = node.Identifier.Text;

                string modifiers = node.Modifiers.ToString();
                TryAppend($"    {modifiers} struct {node.Identifier.Text}\n    {{\n");

                foreach (var member in node.Members)
                {
                    if (_budgetExhausted) break;
                    VisitMember(member);
                }

                TryAppend("    }\n");
            }

            public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
            {
                TryAppend($"        {node.Modifiers} enum {node.Identifier.Text} {{ ");
                var names = new List<string>();
                foreach (var member in node.Members)
                {
                    names.Add(member.Identifier.Text);
                }
                TryAppend(string.Join(", ", names));
                TryAppend(" }\n");
            }

            private void VisitMember(MemberDeclarationSyntax member)
            {
                if (member is FieldDeclarationSyntax field)
                {
                    // Include attributes (like [SerializeField])
                    foreach (var attrList in field.AttributeLists)
                    {
                        TryAppend($"        {attrList.ToString().Trim()}\n");
                    }
                    string fieldStr = $"        {field.Modifiers} {field.Declaration};";
                    _fields.Add(field.Declaration.ToString());
                    TryAppend(fieldStr + "\n");
                }
                else if (member is PropertyDeclarationSyntax prop)
                {
                    foreach (var attrList in prop.AttributeLists)
                    {
                        TryAppend($"        {attrList.ToString().Trim()}\n");
                    }
                    string accessors = "";
                    if (prop.AccessorList != null)
                    {
                        var acc = new List<string>();
                        foreach (var a in prop.AccessorList.Accessors)
                        {
                            acc.Add(a.Keyword.Text + ";");
                        }
                        accessors = " { " + string.Join(" ", acc) + " }";
                    }
                    string propStr = $"        {prop.Modifiers} {prop.Type} {prop.Identifier}{accessors}";
                    _properties.Add($"{prop.Type} {prop.Identifier}");
                    TryAppend(propStr + "\n");
                }
                else if (member is MethodDeclarationSyntax method)
                {
                    foreach (var attrList in method.AttributeLists)
                    {
                        TryAppend($"        {attrList.ToString().Trim()}\n");
                    }
                    string sig = $"        {method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList};";
                    _methods.Add($"{method.ReturnType} {method.Identifier}{method.ParameterList}");
                    TryAppend(sig + "\n");
                }
                else if (member is ConstructorDeclarationSyntax ctor)
                {
                    string sig = $"        {ctor.Modifiers} {ctor.Identifier}{ctor.ParameterList};";
                    _methods.Add($"{ctor.Identifier}{ctor.ParameterList}");
                    TryAppend(sig + "\n");
                }
                else if (member is EnumDeclarationSyntax enumDecl)
                {
                    VisitEnumDeclaration(enumDecl);
                }
                else if (member is ClassDeclarationSyntax nested)
                {
                    TryAppend($"        // nested class {nested.Identifier.Text}\n");
                }
            }

            public CodeSummary BuildSummary()
            {
                return new CodeSummary
                {
                    summary = _sb.ToString(),
                    className = _className,
                    baseClass = _baseClass,
                    interfaces = _interfaces.ToArray(),
                    methods = _methods.ToArray(),
                    fields = _fields.ToArray(),
                    properties = _properties.ToArray(),
                    usings = _usings.ToArray()
                };
            }
        }
    }

    [Serializable]
    public class CodeSummary
    {
        public string summary;
        public string className;
        public string baseClass;
        public string[] interfaces;
        public string[] methods;
        public string[] fields;
        public string[] properties;
        public string[] usings;
    }
}
