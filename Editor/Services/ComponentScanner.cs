using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Scans the active scene hierarchy and collects component types (and optional serialized fields) per GameObject.
    /// Must run on main thread.
    /// </summary>
    public static class ComponentScanner
    {
        private const int MaxGameObjects = 200;
        private const int MaxComponentEntries = 500;
        private const int MaxSerializedFieldsPerComponent = 3;

        public static List<ComponentEntry> GetActiveSceneComponents()
        {
            var entries = new List<ComponentEntry>();
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return entries;

                var roots = new List<GameObject>();
                scene.GetRootGameObjects(roots);
                int gameObjectCount = 0;

                foreach (var root in roots)
                {
                    if (root == null) continue;
                    CollectComponents(root.transform, "", entries, ref gameObjectCount);
                    if (gameObjectCount >= MaxGameObjects || entries.Count >= MaxComponentEntries)
                        break;
                }

                if (entries.Count >= MaxComponentEntries)
                    entries.Add(new ComponentEntry
                    {
                        gameObjectPath = "... (truncated)",
                        componentType = "",
                        serializedFields = null
                    });
            }
            catch (Exception)
            {
                // Return empty on any error
            }
            return entries;
        }

        private static void CollectComponents(Transform t, string parentPath, List<ComponentEntry> entries, ref int gameObjectCount)
        {
            if (t == null || gameObjectCount >= MaxGameObjects || entries.Count >= MaxComponentEntries)
                return;

            gameObjectCount++;
            string path = string.IsNullOrEmpty(parentPath) ? t.gameObject.name : parentPath + "/" + t.gameObject.name;

            var components = t.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (entries.Count >= MaxComponentEntries) return;

                string typeName = comp.GetType().FullName;
                string[] fields = null;

                if (comp is MonoBehaviour mb && mb != null)
                {
                    try
                    {
                        var so = new SerializedObject(comp);
                        var prop = so.GetIterator();
                        var fieldList = new List<string>();
                        prop.Next(true);
                        while (prop.Next(false) && fieldList.Count < MaxSerializedFieldsPerComponent)
                        {
                            if (prop.propertyPath.StartsWith("m_") || prop.propertyPath == "m_Script")
                                continue;
                            if (prop.propertyType == SerializedPropertyType.Generic && prop.depth > 2)
                                continue;
                            string val = GetSerializedPropertyValue(prop);
                            if (!string.IsNullOrEmpty(val))
                                fieldList.Add(prop.name + "=" + val);
                        }
                        if (fieldList.Count > 0)
                            fields = fieldList.ToArray();
                    }
                    catch
                    {
                        // Ignore serialization errors
                    }
                }

                entries.Add(new ComponentEntry
                {
                    gameObjectPath = path,
                    componentType = typeName,
                    serializedFields = fields
                });
            }

            for (int i = 0; i < t.childCount && gameObjectCount < MaxGameObjects; i++)
            {
                var child = t.GetChild(i);
                if (child != null)
                    CollectComponents(child, path, entries, ref gameObjectCount);
            }
        }

        private static string GetSerializedPropertyValue(SerializedProperty prop)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return prop.intValue.ToString();
                    case SerializedPropertyType.Float:
                        return prop.floatValue.ToString("G2");
                    case SerializedPropertyType.Boolean:
                        return prop.boolValue.ToString();
                    case SerializedPropertyType.String:
                        return prop.stringValue?.Length > 20 ? prop.stringValue.Substring(0, 20) + "..." : prop.stringValue;
                    case SerializedPropertyType.ObjectReference:
                        return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "null";
                    case SerializedPropertyType.Enum:
                        return prop.enumDisplayNames[prop.enumValueIndex];
                    case SerializedPropertyType.Vector3:
                        return prop.vector3Value.ToString();
                    case SerializedPropertyType.Vector2:
                        return prop.vector2Value.ToString();
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
