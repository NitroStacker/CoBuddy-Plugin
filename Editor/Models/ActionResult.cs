using System;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class ActionResult
    {
        public bool success;
        public string message;
        public string path;
        /// <summary>JSON string for print actions (printSceneHierarchy, printGameObjects, printAssets)</summary>
        public string data;
    }
}
