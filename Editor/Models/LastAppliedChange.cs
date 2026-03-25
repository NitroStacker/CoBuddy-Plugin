using System;
using CoBuddy.Editor.Services;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class LastAppliedChange
    {
        public string filePath;
        public string previousContent;
        public bool fileExistedBefore;
        /// <summary>Roslyn syntax errors detected before write (soft validation). Null if no errors.</summary>
        public ValidationError[] syntaxErrors;
    }
}
