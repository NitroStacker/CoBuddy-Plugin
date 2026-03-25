using System;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class ComponentEntry
    {
        public string gameObjectPath;
        public string componentType;
        public string[] serializedFields;
    }
}
