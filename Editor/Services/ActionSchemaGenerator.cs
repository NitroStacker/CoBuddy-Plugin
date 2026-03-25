using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Generates typed action schema definitions from the EditorAction model and SupportedActions list.
    /// Produces TypeScript-style interface definitions that give the LLM exact type info
    /// for every action parameter. Similar to the reference app's GetTypescriptDefinitions RPC.
    /// </summary>
    public static class ActionSchemaGenerator
    {
        // Action→required fields mapping (generated from SceneActionExecutor source analysis)
        private static readonly Dictionary<string, ActionFieldSpec[]> ActionSpecs = new Dictionary<string, ActionFieldSpec[]>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Create actions ──
            ["createGameObject"] = new[] {
                F("name", "string", true, "Name of the new GameObject"),
                F("parent", "string", false, "Parent path in hierarchy"),
                F("position", "float[3]", false, "World position [x,y,z]"),
                F("rotation", "float[3]", false, "Euler angles [x,y,z]"),
                F("scale", "float[3]", false, "Scale [x,y,z]"),
                F("tag", "string", false, "Tag to assign"),
            },
            ["instantiatePrefab"] = new[] {
                F("prefabPath", "string", true, "Asset path to .prefab"),
                F("name", "string", false, "Override instance name"),
                F("parent", "string", false, "Parent path"),
                F("position", "float[3]", false, "World position"),
            },
            ["createPrefab"] = new[] {
                F("target", "string", true, "Scene GameObject path to convert"),
                F("prefabPath", "string", true, "Output .prefab asset path"),
            },
            ["createPrimitive"] = new[] {
                F("primitiveType", "\"Cube\"|\"Sphere\"|\"Capsule\"|\"Cylinder\"|\"Plane\"|\"Quad\"", true, "Primitive type"),
                F("name", "string", false, "Override name"),
                F("parent", "string", false, "Parent path"),
                F("position", "float[3]", false, "Position"),
            },

            // ── Target actions ──
            ["addComponent"] = new[] {
                F("target", "string", true, "Target GameObject path"),
                F("componentType", "string", true, "Component type name"),
            },
            ["removeComponent"] = new[] {
                F("target", "string", true, "Target GameObject path"),
                F("componentType", "string", true, "Component type to remove"),
            },
            ["destroyGameObject"] = new[] {
                F("target", "string", true, "Path of GameObject to destroy"),
            },
            ["updategameobject"] = new[] {
                F("target", "string", true, "Target GameObject path"),
                F("name", "string", false, "New name"),
                F("position", "float[3]", false, "New position"),
                F("rotation", "float[3]", false, "New rotation"),
                F("scale", "float[3]", false, "New scale"),
                F("tag", "string", false, "New tag"),
                F("enabled", "bool", false, "Active state"),
            },
            ["reparentGameObject"] = new[] {
                F("target", "string", true, "Target GameObject path"),
                F("newParent", "string", true, "New parent path (or '/' for root)"),
            },
            ["setComponentProperty"] = new[] {
                F("target", "string", true, "Target GameObject path"),
                F("componentType", "string", true, "Component type name"),
                F("property", "string", true, "Property/field name"),
                F("value", "string", true, "Value (parsed via valueType)"),
                F("valueType", "\"float\"|\"int\"|\"bool\"|\"string\"|\"vector2\"|\"vector3\"|\"color\"|\"objectref\"|\"layermask\"|\"enum\"", false, "Type hint for parsing"),
            },
            ["assignMaterial"] = new[] {
                F("target", "string", true, "Target GameObject path (must have Renderer)"),
                F("materialPath", "string", true, "Asset path to .mat file"),
                F("slot", "int", false, "Material slot index (default 0)"),
            },

            // ── Scene/asset ──
            ["openScene"] = new[] { F("path", "string", true, "Scene asset path") },
            ["createScene"] = new[] { F("assetPath", "string", true, "New .unity path") },
            ["createTag"] = new[] { F("tag", "string", true, "Tag name") },
            ["createLayer"] = new[] { F("layerName", "string", true, "Layer name") },
            ["createSortingLayer"] = new[] { F("layerName", "string", true, "Sorting layer name") },

            // ── Script/asset creation ──
            ["createScript"] = new[] {
                F("assetPath", "string", true, "Output .cs path"),
                F("scriptContent", "string", true, "Full C# source code"),
            },
            ["createMaterial"] = new[] {
                F("assetPath", "string", true, "Output .mat path"),
                F("shaderName", "string", false, "Shader name (default Standard)"),
            },
            ["createShader"] = new[] {
                F("assetPath", "string", true, "Output .shader path"),
                F("shaderSource", "string", true, "Shader source code"),
            },
            ["updateMaterial"] = new[] {
                F("assetPath", "string", true, "Material asset path"),
                F("materialProperties", "string", true, "JSON properties"),
            },
            ["createScriptableObject"] = new[] {
                F("assetPath", "string", true, "Output .asset path"),
                F("scriptableObjectType", "string", true, "ScriptableObject class name"),
            },
            ["createInputActions"] = new[] {
                F("assetPath", "string", true, "Output .inputactions path"),
                F("inputActionsJson", "string", true, "Input actions JSON content"),
            },

            // ── UI ──
            ["createCanvas"] = new[] {
                F("name", "string", false, "Canvas name"),
                F("renderMode", "\"ScreenSpaceOverlay\"|\"ScreenSpaceCamera\"|\"WorldSpace\"", false, "Render mode"),
            },
            ["setRectTransformLayout"] = new[] {
                F("target", "string", true, "Target UI element path"),
                F("anchorMinX", "float", false, "0-1"), F("anchorMinY", "float", false, "0-1"),
                F("anchorMaxX", "float", false, "0-1"), F("anchorMaxY", "float", false, "0-1"),
                F("posX", "float", false, "Position X"), F("posY", "float", false, "Position Y"),
                F("sizeDeltaX", "float", false, "Width"), F("sizeDeltaY", "float", false, "Height"),
            },

            // ── Physics ──
            ["addRigidbody"] = new[] {
                F("target", "string", true, "Target path"),
                F("mass", "float", false, "Mass"),
                F("drag", "float", false, "Drag"),
                F("useGravity", "bool", false, "Use gravity"),
            },
            ["addCollider"] = new[] {
                F("target", "string", true, "Target path"),
                F("colliderType", "\"Box\"|\"Sphere\"|\"Capsule\"|\"Mesh\"", false, "Collider type"),
                F("isTrigger", "bool", false, "Is trigger"),
            },

            // ── Print/query ──
            ["printSceneHierarchy"] = new[] {
                F("detail", "\"path\"|\"props\"|\"full\"", false, "Detail level (path=200 max, props=50, full=10)"),
            },
            ["printGameObjects"] = new[] {
                F("path", "string", false, "Filter by path prefix"),
                F("detail", "\"path\"|\"props\"|\"full\"", false, "Detail level"),
            },
            ["printAssets"] = new[] {
                F("folder", "string", false, "Asset folder (default: Assets)"),
                F("detail", "\"path\"|\"props\"|\"full\"", false, "Detail level"),
            },

            // ── Lights ──
            ["createLight"] = new[] {
                F("lightType", "\"Directional\"|\"Point\"|\"Spot\"", true, "Light type"),
                F("name", "string", false, "Name"),
                F("position", "float[3]", false, "Position"),
                F("intensity", "float", false, "Intensity"),
                F("range", "float", false, "Range"),
                F("color", "float[3-4]", false, "Color [r,g,b] or [r,g,b,a] 0-1"),
            },

            // ── Animation ──
            ["createAnimationClip"] = new[] {
                F("assetPath", "string", true, "Output .anim path"),
                F("duration", "float", false, "Duration in seconds"),
                F("loop", "bool", false, "Loop animation"),
                F("createController", "bool", false, "Also create AnimatorController"),
            },

            // ── Build/settings ──
            ["setBuildScenes"] = new[] {
                F("scenePaths", "string[]", true, "Array of .unity scene paths"),
            },
            ["selectObject"] = new[] {
                F("objectPath", "string", true, "Path of object to select"),
            },
        };

        /// <summary>
        /// Generates TypeScript-style interface definitions for all supported actions.
        /// Returns a compact string suitable for injection into LLM system prompts.
        /// </summary>
        public static string GenerateDefinitions(string[] supportedActions = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// CoBuddy Action Type Definitions (auto-generated)");
            sb.AppendLine("// Every action has { action: string } plus the fields below.");
            sb.AppendLine();

            var actions = supportedActions ?? GetAllActionNames();

            foreach (var actionName in actions)
            {
                if (ActionSpecs.TryGetValue(actionName, out var fields))
                {
                    sb.Append($"interface {actionName} {{ action: \"{actionName}\"");
                    foreach (var f in fields)
                    {
                        sb.Append($"; {f.name}{(f.required ? "" : "?")}: {f.type}");
                    }
                    sb.AppendLine(" }");
                }
                else
                {
                    // No detailed spec — just show the action exists
                    sb.AppendLine($"interface {actionName} {{ action: \"{actionName}\"; [key: string]: any }}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generates a compact JSON schema for all actions (for structured output).
        /// </summary>
        public static string GenerateJsonSchema(string[] supportedActions = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");

            var actions = supportedActions ?? GetAllActionNames();
            bool first = true;

            foreach (var actionName in actions)
            {
                if (!first) sb.AppendLine(",");
                first = false;

                sb.Append($"  {{\"action\":\"{actionName}\"");

                if (ActionSpecs.TryGetValue(actionName, out var fields))
                {
                    var required = fields.Where(f => f.required).Select(f => $"\"{f.name}\"");
                    var optional = fields.Where(f => !f.required).Select(f => $"\"{f.name}\"");

                    if (required.Any())
                        sb.Append($",\"required\":[{string.Join(",", required)}]");
                    if (optional.Any())
                        sb.Append($",\"optional\":[{string.Join(",", optional)}]");
                }

                sb.Append("}");
            }

            sb.AppendLine("\n]");
            return sb.ToString();
        }

        private static string[] GetAllActionNames()
        {
            return ActionSpecs.Keys.OrderBy(k => k).ToArray();
        }

        private static ActionFieldSpec F(string name, string type, bool required, string description = null)
        {
            return new ActionFieldSpec { name = name, type = type, required = required, description = description };
        }

        private struct ActionFieldSpec
        {
            public string name;
            public string type;
            public bool required;
            public string description;
        }
    }
}
