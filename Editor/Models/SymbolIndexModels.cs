using System;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class ScriptIndexEntry
    {
        public string path;
        public string className;
        public string baseClass;
        public string[] interfaces;
        public string[] methods;
        public string[] referencedTypes;
    }

    [Serializable]
    public class DependencyEdge
    {
        public string fromPath;
        public string toPath;
        public string symbol;
    }

    [Serializable]
    public class CodeIndexSnapshot
    {
        public ScriptIndexEntry[] scripts;
        public DependencyEdge[] edges;
    }

    [Serializable]
    public class SymbolSearchResult
    {
        public ScriptIndexEntry script;
        public int score;
    }
}
