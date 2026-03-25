using System;

namespace CoBuddy.Editor.Models
{
    [Serializable]
    public class EditorAction
    {
        public string action;
        public string name;
        public string parent;
        public string target;
        public string path;
        public string prefabPath;
        public string componentType;
        public float[] position;
        // createTag
        public string tag;
        // createPrimitive
        public string primitiveType;
        // reparentGameObject
        public string newParent;
        // updateGameObject
        public float[] rotation;
        public float[] scale;
        // createScriptableObject, createMaterial, createShader
        public string assetPath;
        public string scriptableObjectType;
        public string shaderName;
        public string shaderSource;
        // updateMaterial
        public string materialProperties;
        // setRectTransformLayout
        public float anchorMinX, anchorMinY, anchorMaxX, anchorMaxY;
        public float posX, posY, sizeDeltaX, sizeDeltaY, pivotX, pivotY;
        // printAssets
        public string folder;
        // print detail level: "path" (default, max 200), "props" (max 50), "full" (max 10)
        public string detail;
        // updateAssetImporter (TextureImporter)
        public int maxTextureSize;
        // updateCanvas
        public string renderMode;
        // updateUIImage, updateUIText - use color array [r,g,b,a] 0-1, or colorR/G/B/A
        public float[] color;
        public float colorR = -1f, colorG = -1f, colorB = -1f, colorA = -1f;
        public string imageType;
        public string spritePath;
        // updateUIText
        public string text;
        public int fontSize;
        public string alignment;
        // createCircleSprite
        public int spriteSize = 128;
        // createUXML, createUSS
        public string content;
        // createUIDocument
        public string uxmlPath;
        public string[] ussPaths;
        public string panelSettingsPath;
        // createAnimationClip
        public float duration = 1f;
        public string animationCurves; // JSON: {"curves":[{"path":"","type":"CanvasGroup","property":"m_Alpha","keyframes":[{"t":0,"v":0,"inT":0,"outT":0}]}]}
        public bool createController = true; // If true, also create AnimatorController with one state
        public bool loop = false; // If true, set WrapMode.Loop for looping animations
        // addAnimator
        public string controllerPath; // Path to AnimatorController asset
        // createUILoopingScript
        public string elementName; // VisualElement name to animate
        public string toggleClass; // USS class to add/remove for loop (e.g. "pulse-active")
        public string documentPath; // GameObject path of UIDocument (optional; script goes on same object)
        // copyAsset, moveAsset: path=source, assetPath=dest
        public string sourcePath;
        // createTexture, createSprite: base64 image data
        public string textureBase64;
        // createSprite: texturePath, rect x,y,w,h, pivot x,y (reuses pivotX, pivotY from setRectTransformLayout)
        public string texturePath;
        public float rectX, rectY, rectW, rectH;
        // addRigidbody, addCollider, createPhysicMaterial
        public string colliderType; // Box, Sphere, Capsule, Mesh
        public float sizeX, sizeY, sizeZ;
        public float radius;
        public float height;
        public string meshPath;
        public bool isTrigger;
        public float mass;
        public float drag;
        public float angularDrag;
        public bool useGravity;
        public string physicsMaterialPath;
        // createInputActions
        public string inputActionsJson;
        // createScript, createAssemblyDefinition
        public string scriptContent;
        public string assemblyName;
        public string[] references;
        // createPackageManifest
        public string packageName;
        public string packageVersion;
        // Tilemap
        public string tilePath;
        public int tileX, tileY, tileZ;
        public string tilesJson; // JSON array of {x,y,z,tilePath}
        // Timeline
        public string timelinePath;
        public string trackName;
        public string trackType; // Animation, Activation, etc.
        public string clipPath;
        public float clipStart;
        public float clipDuration;
        // Audio
        public string audioClipPath;
        public string videoClipPath;
        public string wavBase64;
        // updateParticleSystem
        public float startLifetime = -1f;
        public float startSpeed = -1f;
        public float startSize = -1f;
        // createLight, updateLight
        public string lightType; // Directional, Point, Spot
        public float intensity;
        public float range;
        public float spotAngle;
        // setBuildScenes
        public string[] scenePaths;
        public bool enabled;
        // setPlayerSettings
        public string companyName;
        public string productName;
        public string bundleIdentifier;
        // executeMenuItem
        public string menuPath;
        // enterPlayMode
        public bool play;
        // selectObject
        public string objectPath;
        // createPrefabVariant
        public string variantPath;
        // createPanelSettings, createThemeStyleSheet
        public string themePath;
        // Phase 15 - 2D
        public string bodyType;        // Dynamic, Static, Kinematic
        public float gravityScale = -1f;
        public int sortingOrder = 0;
        public string sortingLayerName;
        public string layerName;       // createLayer, createSortingLayer
        // Phase 16 - Canvas
        public string layoutType;      // Horizontal, Vertical, Grid
        public float spacing = -1f;
        public string canvasScalerMatch; // Width, Height, Both
        public string viewportPath;
        public string contentPath;
        // Phase 20 - Rendering
        public int positionCount = 2;
        public float width = 1f;
        public float trailTime = 2f;
        public string probeType;       // Baked, Realtime, Both
        // Phase 18 - Navigation
        public float agentRadius = -1f;
        public float agentHeight = -1f;
        public string obstacleShape;
        // Phase 19 - Animation
        public string blendType;       // BlendTree
        public string maskPath;
        // Phase 21 - Joints
        public string jointType;       // Hinge, Fixed, Spring, Configurable
        public string connectedBodyPath;
        public float[] center;         // CharacterController center
        public float[] anchor;
        public float[] axis;
        // Phase 22 - Terrain
        public string terrainDataPath;
        // Phase 24 - TMP
        public string textMeshType;    // UGUI, 3D
        public string sourceFontPath;
        // Phase 25 - Packages
        public string packageId;
        // Phase 30 - Addressables
        public string address;
        public string groupName;
        // Phase 29 - 2D Animation
        public string[] spritePaths;
        public float frameRate = 12f;
        // setComponentProperty
        public string property;      // field/property name on the component
        public string value;         // value as string (parsed via valueType hint)
        public string valueType;     // float | int | bool | string | vector2 | vector3 | color | objectref | layermask | rigidbodyconstraints | enum
        // assignMaterial
        public string materialPath;  // asset path to .mat file
        public int slot;             // renderer material slot index (default 0)
    }
}
