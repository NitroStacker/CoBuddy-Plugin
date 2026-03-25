using System;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class FilePatch
    {
        public string filePath;
        public string originalContent;
        public string newContent;
        public string reason;
    }
}
