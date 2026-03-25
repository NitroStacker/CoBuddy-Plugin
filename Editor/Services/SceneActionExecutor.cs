using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    public static class SceneActionExecutor
    {
        internal static Func<bool> CurrentCancelCheck;

        /// <summary>The undo group index from the last ExecuteActions batch. -1 if none.</summary>
        public static int LastUndoGroup { get; private set; } = -1;

        /// <summary>Index of the last successfully executed action in the most recent batch.</summary>
        public static int LastSuccessfulActionIndex { get; private set; } = -1;

        /// <summary>
        /// Reverts all undo operations down to the specified group (or LastUndoGroup if -1).
        /// Returns true if undo was performed.
        /// </summary>
        public static bool RevertUndoGroup(int undoGroup = -1)
        {
            int group = undoGroup >= 0 ? undoGroup : LastUndoGroup;
            if (group < 0) return false;
            try
            {
                Undo.RevertAllDownToGroup(group);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] Undo revert failed: {ex.Message}");
                return false;
            }
        }

        public static (ActionResult[] results, List<ActionRevertRecord> revertRecords, List<(int index, EditorAction action)> deferred) ExecuteActions(
            EditorAction[] actions,
            Func<bool> cancelCheck = null,
            Action<float, string> onProgress = null)
        {
            CurrentCancelCheck = cancelCheck;
            LastSuccessfulActionIndex = -1;
            try
            {
            var deferred = new List<(int index, EditorAction action)>();
            if (actions == null || actions.Length == 0)
            {
                LastUndoGroup = -1;
                return (Array.Empty<ActionResult>(), new List<ActionRevertRecord>(), deferred);
            }

            // Start a named undo group so the entire batch can be reverted atomically with Ctrl+Z
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("CoBuddy Actions");
            LastUndoGroup = undoGroup;

            var results = new ActionResult[actions.Length];
            var revertRecords = new List<ActionRevertRecord>();
            for (int i = 0; i < actions.Length; i++)
            {
                if (cancelCheck != null && cancelCheck())
                {
                    for (int j = i; j < actions.Length; j++)
                        results[j] = new ActionResult { success = false, message = "Operation cancelled", path = "" };
                    break;
                }
                var a = actions[i];
                onProgress?.Invoke((i + 1) / (float)actions.Length, a?.action ?? "");
                var prevAction = i > 0 ? actions[i - 1] : null;
                var shouldDefer = ShouldDeferAddComponentAfterScript(a, prevAction);
                if (shouldDefer)
                {
                    deferred.Add((i, a));
                    results[i] = new ActionResult { success = true, message = "Deferred until compile", path = "" };
                    continue;
                }
                var prevResults = i > 0 ? results.Take(i).ToArray() : Array.Empty<ActionResult>();
                var (result, record) = ExecuteActionWithRevert(a, prevResults);
                results[i] = result;
                if (record != null)
                    revertRecords.Add(record);
                if (result.success)
                    LastSuccessfulActionIndex = i;
            }

            // Close the undo group so all actions are grouped as one Ctrl+Z operation
            Undo.CollapseUndoOperations(undoGroup);

            return (results, revertRecords, deferred);
            }
            finally
            {
                CurrentCancelCheck = null;
            }
        }

        private static bool ShouldDeferAddComponentAfterScript(EditorAction a, EditorAction prevAction)
        {
            if (a == null || prevAction == null) return false;
            var act = (a.action ?? "").ToLowerInvariant();
            var prevAct = (prevAction.action ?? "").ToLowerInvariant();
            if (act != "addcomponent" || prevAct != "createuiloopingscript") return false;
            var compType = (a.componentType ?? "").Trim();
            return compType.Equals("UILoopingAnimation", StringComparison.OrdinalIgnoreCase);
        }

        public static void RevertActions(List<ActionRevertRecord> records)
        {
            if (records == null || records.Count == 0) return;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];
                if (r == null || string.IsNullOrWhiteSpace(r.action)) continue;
                try
                {
                    switch (r.action.ToLowerInvariant())
                    {
                        case "creategameobject":
                        case "instantiateprefab":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "addcomponent":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createprefab":
                            if (!string.IsNullOrWhiteSpace(r.prefabPath))
                                RevertDeletePrefab(r.prefabPath);
                            break;
                        case "createscene":
                            if (!string.IsNullOrWhiteSpace(r.scenePath))
                                RevertDeleteScene(r.scenePath);
                            break;
                        case "createprimitive":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "reparentgameobject":
                            if (!string.IsNullOrWhiteSpace(r.path) && !string.IsNullOrWhiteSpace(r.oldParentPath))
                                RevertReparent(r.path, r.oldParentPath);
                            break;
                        case "updategameobject":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertUpdateGameObject(r);
                            break;
                        case "removecomponent":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertAddComponent(r.targetPath, r.componentType);
                            break;
                        case "setrecttransformlayout":
                            if (!string.IsNullOrWhiteSpace(r.targetPath))
                                RevertRectTransform(r);
                            break;
                        case "createscriptableobject":
                        case "creatematerial":
                        case "createshader":
                        case "createcirclesprite":
                        case "createuxml":
                        case "createuss":
                        case "createanimationclip":
                            if (!string.IsNullOrWhiteSpace(r.controllerPath))
                                RevertDeleteAsset(r.controllerPath);
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createuiloopingscript":
                        case "copyasset":
                        case "createfolder":
                        case "createtexture":
                        case "createsprite":
                        case "createinputactions":
                        case "createscript":
                        case "createassemblydefinition":
                        case "createpanelsettings":
                        case "createthemestylesheet":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "moveasset":
                            if (!string.IsNullOrWhiteSpace(r.assetPath) && !string.IsNullOrWhiteSpace(r.oldAssetPath))
                                AssetDatabase.MoveAsset(r.assetPath, r.oldAssetPath);
                            break;
                        case "addanimator":
                        case "addrigidbody":
                        case "addcollider":
                        case "assigninputactions":
                        case "addplayabledirector":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createphysicmaterial":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createaudioclip":
                        case "createaudiomixer":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "addaudiosource":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "addparticlesystem":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            else if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "addcamera":
                        case "addcinemachinevirtualcamera":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            else if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "addrigidbody2d":
                        case "addcollider2d":
                        case "addspriterenderer":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createspriteatlas":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createcanvas":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "addlinerenderer":
                        case "addtrailrenderer":
                        case "addreflectionprobe":
                        case "addlightprobegroup":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "addnavmeshagent":
                        case "addnavmeshobstacle":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createanimatoroverridecontroller":
                        case "createblendtree":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "addavatarmask":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "addjoint":
                        case "addcharactercontroller":
                        case "addcloth":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createterrain":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "addterrainlayer":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "addvideoplayer":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "addtextmeshpro":
                            if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createtmpfontasset":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "loadsceneadditive":
                            if (!string.IsNullOrWhiteSpace(r.scenePath))
                                try { EditorSceneManager.CloseScene(SceneManager.GetSceneByPath(r.scenePath), true); } catch { }
                            break;
                        case "createvfxgraph":
                        case "createshadergraph":
                        case "createspriteanimation":
                        case "createruletile":
                        case "createlocalizationtable":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "addeventsystem":
                        case "addlayoutgroup":
                        case "addscrollrect":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            else if (!string.IsNullOrWhiteSpace(r.targetPath) && !string.IsNullOrWhiteSpace(r.componentType))
                                RevertRemoveComponent(r.targetPath, r.componentType);
                            break;
                        case "createtilemap":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "createtile":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createlight":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "createrendertexture":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createprefabvariant":
                            if (!string.IsNullOrWhiteSpace(r.assetPath))
                                RevertDeleteAsset(r.assetPath);
                            break;
                        case "createuidocument":
                            if (!string.IsNullOrWhiteSpace(r.path))
                                RevertDestroyGameObject(r.path);
                            break;
                        case "updatematerial":
                            if (!string.IsNullOrWhiteSpace(r.assetPath) && !string.IsNullOrWhiteSpace(r.previousMaterialState))
                                RevertMaterialProperties(r);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoBuddy] Revert action failed: {ex.Message}");
                }
            }
        }

        private static void RevertDestroyGameObject(string path)
        {
            var go = FindByPath(path);
            if (go != null)
                Undo.DestroyObjectImmediate(go);
        }

        private static void RevertRemoveComponent(string targetPath, string componentType)
        {
            var go = FindByPath(targetPath);
            if (go == null) return;
            var type = ResolveComponentType(componentType);
            if (type == null) return;
            var comp = go.GetComponent(type);
            if (comp != null)
                Undo.DestroyObjectImmediate(comp);
        }

        private static void RevertDeletePrefab(string prefabPath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(projectRoot, prefabPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                AssetDatabase.Refresh();
            }
        }

        private static void RevertDeleteScene(string scenePath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(projectRoot, scenePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                AssetDatabase.Refresh();
            }
        }

        private static void RevertReparent(string path, string oldParentPath)
        {
            var go = FindByPath(path);
            var oldParent = GetParentTransform(oldParentPath);
            if (go != null && oldParent != null)
                go.transform.SetParent(oldParent, true);
        }

        private static void RevertUpdateGameObject(ActionRevertRecord r)
        {
            var go = FindByPath(r.path);
            if (go == null) return;
            if (r.previousPosition != null && r.previousPosition.Length >= 3)
                go.transform.position = new Vector3(r.previousPosition[0], r.previousPosition[1], r.previousPosition[2]);
            if (r.previousRotation != null && r.previousRotation.Length >= 3)
                go.transform.eulerAngles = new Vector3(r.previousRotation[0], r.previousRotation[1], r.previousRotation[2]);
            if (r.previousScale != null && r.previousScale.Length >= 3)
                go.transform.localScale = new Vector3(r.previousScale[0], r.previousScale[1], r.previousScale[2]);
            if (!string.IsNullOrWhiteSpace(r.previousTag) && !go.CompareTag(r.previousTag))
            {
                try { go.tag = r.previousTag; } catch { }
            }
        }

        private static void RevertAddComponent(string targetPath, string componentType)
        {
            var go = FindByPath(targetPath);
            if (go == null) return;
            var type = ResolveComponentType(componentType);
            if (type == null) return;
            if (go.GetComponent(type) == null)
                Undo.AddComponent(go, type);
        }

        private static void RevertRectTransform(ActionRevertRecord r)
        {
            var go = FindByPath(r.targetPath);
            if (go == null) return;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(r.prevAnchorMinX, r.prevAnchorMinY);
            rt.anchorMax = new Vector2(r.prevAnchorMaxX, r.prevAnchorMaxY);
            rt.anchoredPosition = new Vector2(r.prevPosX, r.prevPosY);
            rt.sizeDelta = new Vector2(r.prevSizeDeltaX, r.prevSizeDeltaY);
            rt.pivot = new Vector2(r.prevPivotX, r.prevPivotY);
        }

        private static void RevertDeleteAsset(string assetPath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                AssetDatabase.Refresh();
            }
        }

        public static (ActionResult result, ActionRevertRecord record) ExecuteSingleAction(EditorAction a, ActionResult[] prevResults = null)
        {
            return ExecuteActionWithRevert(a, prevResults);
        }

        private static (ActionResult result, ActionRevertRecord record) ExecuteActionWithRevert(EditorAction a, ActionResult[] prevResults = null)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.action))
                return (Fail("Missing action"), null);
            prevResults ??= Array.Empty<ActionResult>();

            try
            {
                switch (a.action.ToLowerInvariant())
                {
                    case "creategameobject": return CreateGameObject(a);
                    case "addcomponent": return AddComponent(a);
                    case "instantiateprefab": return InstantiatePrefab(a);
                    case "createprefab": return CreatePrefab(a);
                    case "openscene": return (OpenScene(a), null);
                    case "createscene": return CreateScene(a);
                    case "destroygameobject": return (DestroyGameObject(a), null);
                    case "createtag": return (CreateTag(a), null);
                    case "createprimitive": return CreatePrimitive(a);
                    case "reparentgameobject": return ReparentGameObject(a);
                    case "updategameobject": return UpdateGameObject(a);
                    case "removecomponent": return RemoveComponent(a);
                    case "setrecttransformlayout": return SetRectTransformLayout(a);
                    case "createscriptableobject": return CreateScriptableObject(a);
                    case "creatematerial": return CreateMaterial(a);
                    case "createshader": return CreateShader(a);
                    case "updatematerial": return UpdateMaterial(a);
                    case "printscenehierarchy": return (PrintSceneHierarchy(a), null);
                    case "printgameobjects": return (PrintGameObjects(a), null);
                    case "printassets": return (PrintAssets(a), null);
                    case "updateassetimporter": return (UpdateAssetImporter(a), null);
                    case "updatecanvas": return (UpdateCanvas(a), null);
                    case "updateuiimage": return (UpdateUIImage(a), null);
                    case "updateuitext": return (UpdateUIText(a), null);
                    case "createcirclesprite": return CreateCircleSprite(a);
                    case "createuxml": return CreateUXML(a);
                    case "createuss": return CreateUSS(a);
                    case "createuidocument": return CreateUIDocument(a);
                    case "createanimationclip": return CreateAnimationClip(a);
                    case "addanimator": return AddAnimator(a, prevResults);
                    case "createuiloopingscript": return CreateUILoopingScript(a);
                    case "copyasset": return CopyAsset(a);
                    case "moveasset": return MoveAsset(a);
                    case "deleteasset": return DeleteAsset(a);
                    case "createfolder": return CreateFolder(a);
                    case "importasset": return ImportAsset(a);
                    case "createtexture": return CreateTexture(a);
                    case "createsprite": return CreateSprite(a);
                    case "executemenuitem": return ExecuteMenuItem(a);
                    case "enterplaymode": return EnterPlayMode(a);
                    case "exitplaymode":
                    {
                        if (Application.isPlaying)
                            EditorApplication.ExitPlaymode();
                        return (Ok("Exited Play Mode"), null);
                    }
                    case "selectobject": return SelectObject(a);
                    case "focussceneview": return FocusSceneView(a);
                    case "addrigidbody": return AddRigidbody(a);
                    case "addcollider": return AddCollider(a);
                    case "setcollidersize": return SetColliderSize(a);
                    case "createphysicmaterial": return CreatePhysicMaterial(a);
                    case "createinputactions": return CreateInputActions(a);
                    case "assigninputactions": return AssignInputActions(a);
                    case "createscript": return CreateScript(a);
                    case "createassemblydefinition": return CreateAssemblyDefinition(a);
                    case "createpackagemanifest": return CreatePackageManifest(a);
                    case "createtilemap": return CreateTilemap(a);
                    case "createtile": return CreateTile(a);
                    case "settile": return SetTile(a);
                    case "settiles": return SetTiles(a);
                    case "createtimeline": return CreateTimeline(a);
                    case "addtimelinetrack": return AddTimelineTrack(a);
                    case "addtimelineclip": return AddTimelineClip(a);
                    case "addplayabledirector": return AddPlayableDirector(a);
                    case "createaudioclip": return CreateAudioClip(a);
                    case "addaudiosource": return AddAudioSource(a);
                    case "createaudiomixer": return CreateAudioMixer(a);
                    case "createlight": return CreateLight(a);
                    case "updatelight": return UpdateLight(a);
                    case "createrendertexture": return CreateRenderTexture(a);
                    case "addparticlesystem": return AddParticleSystem(a);
                    case "updateparticlesystem": return UpdateParticleSystem(a);
                    case "addcamera": return AddCamera(a);
                    case "addcinemachinevirtualcamera": return AddCinemachineVirtualCamera(a);
                    case "setbuildscenes": return SetBuildScenes(a);
                    case "setplayersettings": return SetPlayerSettings(a);
                    case "switchplatform": return SwitchPlatform(a);
                    case "executebuild": return ExecuteBuild(a);
                    case "createprefabvariant": return CreatePrefabVariant(a);
                    case "unpackprefab": return UnpackPrefab(a);
                    case "createpanelsettings": return CreatePanelSettings(a);
                    case "createthemestylesheet": return CreateThemeStyleSheet(a);
                    case "addrigidbody2d": return AddRigidbody2D(a);
                    case "addcollider2d": return AddCollider2D(a);
                    case "addspriterenderer": return AddSpriteRenderer(a);
                    case "setsortinglayer": return SetSortingLayer(a);
                    case "createsortinglayer": return CreateSortingLayer(a);
                    case "createspriteatlas": return CreateSpriteAtlas(a);
                    case "createcanvas": return CreateCanvas(a);
                    case "addeventsystem": return AddEventSystem(a);
                    case "addlayoutgroup": return AddLayoutGroup(a);
                    case "addscrollrect": return AddScrollRect(a);
                    case "createlayer": return CreateLayer(a);
                    case "addlinerenderer": return AddLineRenderer(a);
                    case "addtrailrenderer": return AddTrailRenderer(a);
                    case "addreflectionprobe": return AddReflectionProbe(a);
                    case "addlightprobegroup": return AddLightProbeGroup(a);
                    case "bakenavmesh": return BakeNavMesh(a);
                    case "addnavmeshagent": return AddNavMeshAgent(a);
                    case "addnavmeshobstacle": return AddNavMeshObstacle(a);
                    case "createanimatoroverridecontroller": return CreateAnimatorOverrideController(a);
                    case "createblendtree": return CreateBlendTree(a);
                    case "addavatarmask": return AddAvatarMask(a);
                    case "addjoint": return AddJoint(a);
                    case "addcharactercontroller": return AddCharacterController(a);
                    case "addcloth": return AddCloth(a);
                    case "createterrain": return CreateTerrain(a);
                    case "addterrainlayer": return AddTerrainLayer(a);
                    case "addvideoplayer": return AddVideoPlayer(a);
                    case "addtextmeshpro": return AddTextMeshPro(a);
                    case "createtmpfontasset": return CreateTMPFontAsset(a);
                    case "addpackage": return AddPackage(a);
                    case "removepackage": return RemovePackage(a);
                    case "loadsceneadditive": return LoadSceneAdditive(a);
                    case "unloadscene": return UnloadScene(a);
                    case "createvfxgraph": return CreateVFXGraph(a);
                    case "createshadergraph": return CreateShaderGraph(a);
                    case "createspriteanimation": return CreateSpriteAnimation(a);
                    case "createruletile": return CreateRuleTile(a);
                    case "markaddressable": return MarkAddressable(a);
                    case "createaddressablesgroup": return CreateAddressablesGroup(a);
                    case "createlocalizationtable": return CreateLocalizationTable(a);
                    case "setcomponentproperty": return SetComponentProperty(a);
                    case "assignmaterial": return AssignMaterial(a);
                    case "generateinputactionscsharp": return GenerateInputActionsCSharp(a);
                    default: return (Fail($"Unknown action: {a.action}"), null);
                }
            }
            catch (Exception ex)
            {
                return (Fail(ex.Message), null);
            }
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
                return null;
            string clean = path.TrimStart('/');
            var go = GameObject.Find(clean);
            if (go != null) return go;

            // Try case-insensitive match by scanning scene hierarchy
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            var roots = scene.GetRootGameObjects();
            var cleanLower = clean.ToLowerInvariant();
            foreach (var root in roots)
            {
                var found = FindByPathCaseInsensitive(root.transform, "", cleanLower);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindByPathCaseInsensitive(Transform t, string prefix, string targetLower)
        {
            var path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            if (path.ToLowerInvariant() == targetLower) return t.gameObject;
            for (int i = 0; i < t.childCount; i++)
            {
                var found = FindByPathCaseInsensitive(t.GetChild(i), path, targetLower);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Finds close matches for a path that wasn't found. Returns up to 3 suggestions.
        /// Uses name-based fuzzy matching (Levenshtein distance on the last path segment).
        /// </summary>
        private static string[] FindSimilarPaths(string path, int maxSuggestions = 3)
        {
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return Array.Empty<string>();

            var targetName = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
            var targetLower = targetName.ToLowerInvariant();
            var candidates = new List<(string path, int distance)>();
            var roots = scene.GetRootGameObjects();

            foreach (var root in roots)
                CollectFuzzyCandidates(root.transform, "", targetLower, candidates, 100);

            return candidates
                .OrderBy(c => c.distance)
                .Take(maxSuggestions)
                .Select(c => c.path)
                .ToArray();
        }

        private static void CollectFuzzyCandidates(Transform t, string prefix, string targetLower, List<(string path, int distance)> candidates, int limit)
        {
            if (candidates.Count >= limit) return;
            var path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            var nameLower = t.name.ToLowerInvariant();

            // Score: prioritize substring match, then Levenshtein on name
            int dist;
            if (nameLower == targetLower)
                dist = 0;
            else if (nameLower.Contains(targetLower) || targetLower.Contains(nameLower))
                dist = Math.Abs(nameLower.Length - targetLower.Length);
            else
                dist = LevenshteinDistance(nameLower, targetLower);

            if (dist <= Math.Max(3, targetLower.Length / 2))
                candidates.Add((path, dist));

            for (int i = 0; i < t.childCount; i++)
            {
                if (candidates.Count >= limit) return;
                CollectFuzzyCandidates(t.GetChild(i), path, targetLower, candidates, limit);
            }
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            int la = a.Length, lb = b.Length;
            // Optimization: truncate to first 30 chars for very long names
            if (la > 30) { a = a.Substring(0, 30); la = 30; }
            if (lb > 30) { b = b.Substring(0, 30); lb = 30; }
            var prev = new int[lb + 1];
            var curr = new int[lb + 1];
            for (int j = 0; j <= lb; j++) prev[j] = j;
            for (int i = 1; i <= la; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[lb];
        }

        private static Transform GetParentTransform(string parentPath)
        {
            if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
                return null;
            var go = FindByPath(parentPath);
            return go != null ? go.transform : null;
        }

        private static (ActionResult, ActionRevertRecord) CreateGameObject(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.name))
                return (Fail("createGameObject requires name"), null);

            var parent = GetParentTransform(a.parent);
            var go = new GameObject(a.name ?? "GameObject");

            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            if (a.position != null && a.position.Length >= 3)
            {
                go.transform.position = new Vector3(a.position[0], a.position[1], a.position[2]);
            }

            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createGameObject");
            var path = GetPath(go);
            return (Ok(path), new ActionRevertRecord { action = "createGameObject", path = path });
        }

        private static (ActionResult, ActionRevertRecord) AddComponent(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("addComponent requires target"), null);
            if (string.IsNullOrWhiteSpace(a.componentType))
                return (Fail("addComponent requires componentType"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var type = ResolveComponentType(a.componentType);
            if (type == null)
                return (Fail($"Component type not found: {a.componentType}"), null);

            Undo.AddComponent(go, type);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addComponent", targetPath = a.target, componentType = a.componentType });
        }

        private static Type ResolveComponentType(string componentType)
        {
            var c = componentType?.Trim() ?? "";
            if (c.Equals("CanvasGroup", StringComparison.OrdinalIgnoreCase)) return typeof(CanvasGroup);
            if (c.Equals("Animator", StringComparison.OrdinalIgnoreCase)) return typeof(Animator);
            if (c.Equals("PlayableDirector", StringComparison.OrdinalIgnoreCase))
                return Type.GetType("UnityEngine.Playables.PlayableDirector, UnityEngine.CoreModule");
            if (c.Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) return typeof(ParticleSystem);
            if (c.Equals("Camera", StringComparison.OrdinalIgnoreCase)) return typeof(Camera);
            if (c.Equals("CinemachineVirtualCamera", StringComparison.OrdinalIgnoreCase))
                return Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine");
            if (c.Equals("Rigidbody2D", StringComparison.OrdinalIgnoreCase)) return typeof(Rigidbody2D);
            if (c.Equals("BoxCollider2D", StringComparison.OrdinalIgnoreCase)) return typeof(BoxCollider2D);
            if (c.Equals("CircleCollider2D", StringComparison.OrdinalIgnoreCase)) return typeof(CircleCollider2D);
            if (c.Equals("PolygonCollider2D", StringComparison.OrdinalIgnoreCase)) return typeof(PolygonCollider2D);
            if (c.Equals("SpriteRenderer", StringComparison.OrdinalIgnoreCase)) return typeof(SpriteRenderer);
            if (c.Equals("EventSystem", StringComparison.OrdinalIgnoreCase))
                return Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
            if (c.Equals("HorizontalLayoutGroup", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.UI.HorizontalLayoutGroup);
            if (c.Equals("VerticalLayoutGroup", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.UI.VerticalLayoutGroup);
            if (c.Equals("GridLayoutGroup", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.UI.GridLayoutGroup);
            if (c.Equals("ScrollRect", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.UI.ScrollRect);
            if (c.Equals("LineRenderer", StringComparison.OrdinalIgnoreCase)) return typeof(LineRenderer);
            if (c.Equals("TrailRenderer", StringComparison.OrdinalIgnoreCase)) return typeof(TrailRenderer);
            if (c.Equals("ReflectionProbe", StringComparison.OrdinalIgnoreCase)) return typeof(ReflectionProbe);
            if (c.Equals("LightProbeGroup", StringComparison.OrdinalIgnoreCase)) return typeof(LightProbeGroup);
            if (c.Equals("NavMeshAgent", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.AI.NavMeshAgent);
            if (c.Equals("NavMeshObstacle", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.AI.NavMeshObstacle);
            if (c.Equals("VideoPlayer", StringComparison.OrdinalIgnoreCase)) return typeof(UnityEngine.Video.VideoPlayer);
            if (c.Equals("TextMeshProUGUI", StringComparison.OrdinalIgnoreCase)) return Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            var t = Type.GetType(componentType)
                ?? Type.GetType(componentType + ", UnityEngine")
                ?? Type.GetType(componentType + ", Unity.TextMeshPro")
                ?? Type.GetType(componentType + ", UnityEngine.UI");
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(componentType);
                    if (t != null && typeof(Component).IsAssignableFrom(t))
                        return t;
                }
                catch { }
            }
            return null;
        }

        private static (ActionResult, ActionRevertRecord) InstantiatePrefab(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.prefabPath))
                return (Fail("instantiatePrefab requires prefabPath"), null);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(a.prefabPath);
            if (prefab == null)
                return (Fail($"Prefab not found: {a.prefabPath}"), null);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return (Fail("Failed to instantiate prefab"), null);

            var parent = GetParentTransform(a.parent);
            if (parent != null)
            {
                instance.transform.SetParent(parent, false);
            }

            if (!string.IsNullOrWhiteSpace(a.name))
            {
                instance.name = a.name;
            }

            Undo.RegisterCreatedObjectUndo(instance, "CoBuddy instantiatePrefab");
            var path = GetPath(instance);
            return (Ok(path), new ActionRevertRecord { action = "instantiatePrefab", path = path });
        }

        private static (ActionResult, ActionRevertRecord) CreatePrefab(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("createPrefab requires target"), null);
            if (string.IsNullOrWhiteSpace(a.prefabPath))
                return (Fail("createPrefab requires prefabPath"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var dir = Path.GetDirectoryName(a.prefabPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(a.prefabPath);
            bool success;
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, uniquePath, InteractionMode.UserAction, out success);
            if (!success)
                return (Fail("Failed to save prefab"), null);

            return (Ok(uniquePath), new ActionRevertRecord { action = "createPrefab", prefabPath = uniquePath });
        }

        private static ActionResult OpenScene(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.path))
                return Fail("openScene requires path");

            var scene = EditorSceneManager.OpenScene(a.path, OpenSceneMode.Single);
            return Ok(scene.path);
        }

        private static (ActionResult, ActionRevertRecord) CreateScene(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.path))
                return (Fail("createScene requires path"), null);

            var dir = Path.GetDirectoryName(a.path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var saved = EditorSceneManager.SaveScene(scene, a.path);
            if (!saved)
                return (Fail("Failed to save scene"), null);

            return (Ok(a.path), new ActionRevertRecord { action = "createScene", scenePath = a.path });
        }

        private static ActionResult DestroyGameObject(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.path))
                return Fail("destroyGameObject requires path");

            var go = FindByPath(a.path);
            if (go == null)
                return Fail($"GameObject not found: {a.path}");

            Undo.DestroyObjectImmediate(go);
            return Ok(a.path);
        }

        private static ActionResult CreateTag(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.tag))
                return Fail("createTag requires tag");
            var tagName = a.tag.Trim();
            if (string.IsNullOrEmpty(tagName))
                return Fail("createTag requires non-empty tag");
            try
            {
                var tagManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")?[0];
                if (tagManager == null)
                    return Fail("Could not load TagManager");
                var so = new SerializedObject(tagManager);
                var tagsProp = so.FindProperty("tags");
                if (tagsProp == null)
                    return Fail("Could not find tags property");
                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (tagsProp.GetArrayElementAtIndex(i).stringValue == tagName)
                        return Ok(null, tagName);
                }
                if (tagsProp.arraySize >= 10000)
                    return Fail("Tag limit reached");
                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tagName;
                so.ApplyModifiedProperties();
                return Ok(null, tagName);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static (ActionResult, ActionRevertRecord) CreatePrimitive(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.primitiveType))
                return (Fail("createPrimitive requires primitiveType"), null);
            var pt = ParsePrimitiveType(a.primitiveType);
            if (!pt.HasValue)
                return (Fail($"Unknown primitiveType: {a.primitiveType}. Use Cube, Sphere, Capsule, Cylinder, Plane, Quad"), null);

            var go = GameObject.CreatePrimitive(pt.Value);
            if (go == null)
                return (Fail("Failed to create primitive"), null);

            if (!string.IsNullOrWhiteSpace(a.name))
                go.name = a.name;

            var parent = GetParentTransform(a.parent);
            if (parent != null)
                go.transform.SetParent(parent, false);

            if (a.position != null && a.position.Length >= 3)
                go.transform.position = new Vector3(a.position[0], a.position[1], a.position[2]);

            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createPrimitive");
            var path = GetPath(go);
            return (Ok(path), new ActionRevertRecord { action = "createPrimitive", path = path });
        }

        private static PrimitiveType? ParsePrimitiveType(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var lower = s.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "cube": return PrimitiveType.Cube;
                case "sphere": return PrimitiveType.Sphere;
                case "capsule": return PrimitiveType.Capsule;
                case "cylinder": return PrimitiveType.Cylinder;
                case "plane": return PrimitiveType.Plane;
                case "quad": return PrimitiveType.Quad;
                default: return null;
            }
        }

        private static (ActionResult, ActionRevertRecord) ReparentGameObject(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.path))
                return (Fail("reparentGameObject requires path"), null);
            if (string.IsNullOrWhiteSpace(a.newParent))
                return (Fail("reparentGameObject requires newParent"), null);

            var go = FindByPath(a.path);
            if (go == null)
                return (Fail($"GameObject not found: {a.path}"), null);

            var newParent = GetParentTransform(a.newParent);
            if (newParent == null)
                return (Fail($"New parent not found: {a.newParent}"), null);

            var oldParentPath = go.transform.parent != null ? GetPath(go.transform.parent.gameObject) : "";
            Undo.SetTransformParent(go.transform, newParent, "CoBuddy reparentGameObject");
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "reparentGameObject", path = GetPath(go), oldParentPath = oldParentPath });
        }

        private static (ActionResult, ActionRevertRecord) UpdateGameObject(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.path))
                return (Fail("updateGameObject requires path"), null);

            var go = FindByPath(a.path);
            if (go == null)
                return (Fail($"GameObject not found: {a.path}"), null);

            var prevPos = go.transform.position;
            var prevRot = go.transform.eulerAngles;
            var prevScale = go.transform.localScale;
            var prevTag = go.tag;

            Undo.RecordObject(go.transform, "CoBuddy updateGameObject");
            if (a.position != null && a.position.Length >= 3)
                go.transform.position = new Vector3(a.position[0], a.position[1], a.position[2]);
            if (a.rotation != null && a.rotation.Length >= 3)
                go.transform.eulerAngles = new Vector3(a.rotation[0], a.rotation[1], a.rotation[2]);
            if (a.scale != null && a.scale.Length >= 3)
                go.transform.localScale = new Vector3(a.scale[0], a.scale[1], a.scale[2]);
            if (!string.IsNullOrWhiteSpace(a.tag))
            {
                // Auto-create the tag if it doesn't exist — prevents "Tag X is not defined" errors
                CreateTag(new EditorAction { tag = a.tag.Trim() });
                try { go.tag = a.tag.Trim(); } catch (Exception ex) { return (Fail($"Invalid tag: {ex.Message}"), null); }
            }
            return (Ok(GetPath(go)), new ActionRevertRecord
            {
                action = "updateGameObject",
                path = GetPath(go),
                previousPosition = new[] { prevPos.x, prevPos.y, prevPos.z },
                previousRotation = new[] { prevRot.x, prevRot.y, prevRot.z },
                previousScale = new[] { prevScale.x, prevScale.y, prevScale.z },
                previousTag = prevTag
            });
        }

        private static (ActionResult, ActionRevertRecord) RemoveComponent(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("removeComponent requires target"), null);
            if (string.IsNullOrWhiteSpace(a.componentType))
                return (Fail("removeComponent requires componentType"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var type = ResolveComponentType(a.componentType);
            if (type == null)
                return (Fail($"Component type not found: {a.componentType}"), null);

            var comp = go.GetComponent(type);
            if (comp == null)
                return (Fail($"Component {a.componentType} not found on {a.target}"), null);

            Undo.DestroyObjectImmediate(comp);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "removeComponent", targetPath = a.target, componentType = a.componentType });
        }

        private static (ActionResult, ActionRevertRecord) SetRectTransformLayout(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("setRectTransformLayout requires target"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var rt = go.GetComponent<RectTransform>();
            if (rt == null)
                return (Fail($"Target {a.target} has no RectTransform"), null);

            var prevAnchorMin = rt.anchorMin;
            var prevAnchorMax = rt.anchorMax;
            var prevPos = rt.anchoredPosition;
            var prevSize = rt.sizeDelta;
            var prevPivot = rt.pivot;

            Undo.RecordObject(rt, "CoBuddy setRectTransformLayout");

            rt.anchorMin = new Vector2(a.anchorMinX, a.anchorMinY);
            rt.anchorMax = new Vector2(a.anchorMaxX, a.anchorMaxY);
            rt.anchoredPosition = new Vector2(a.posX, a.posY);
            rt.sizeDelta = new Vector2(a.sizeDeltaX, a.sizeDeltaY);
            var pivotX = (a.pivotX == 0 && a.pivotY == 0) ? 0.5f : a.pivotX;
            var pivotY = (a.pivotX == 0 && a.pivotY == 0) ? 0.5f : a.pivotY;
            rt.pivot = new Vector2(pivotX, pivotY);

            return (Ok(GetPath(go)), new ActionRevertRecord
            {
                action = "setRectTransformLayout",
                targetPath = a.target,
                prevAnchorMinX = prevAnchorMin.x, prevAnchorMinY = prevAnchorMin.y,
                prevAnchorMaxX = prevAnchorMax.x, prevAnchorMaxY = prevAnchorMax.y,
                prevPosX = prevPos.x, prevPosY = prevPos.y,
                prevSizeDeltaX = prevSize.x, prevSizeDeltaY = prevSize.y,
                prevPivotX = prevPivot.x, prevPivotY = prevPivot.y
            });
        }

        private static (ActionResult, ActionRevertRecord) CreateScriptableObject(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createScriptableObject requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.scriptableObjectType))
                return (Fail("createScriptableObject requires scriptableObjectType"), null);

            var type = ResolveScriptableObjectType(a.scriptableObjectType);
            if (type == null)
                return (Fail($"ScriptableObject type not found: {a.scriptableObjectType}"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                path += ".asset";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
                return (Fail("Failed to create ScriptableObject instance"), null);

            AssetDatabase.CreateAsset(instance, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createScriptableObject", assetPath = uniquePath });
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            var t = Type.GetType(typeName)
                ?? Type.GetType(typeName + ", UnityEngine")
                ?? Type.GetType(typeName + ", Assembly-CSharp");
            if (t != null && typeof(ScriptableObject).IsAssignableFrom(t))
                return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(typeName);
                    if (t != null && typeof(ScriptableObject).IsAssignableFrom(t))
                        return t;
                }
                catch { }
            }
            return null;
        }

        private static (ActionResult, ActionRevertRecord) CreateMaterial(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createMaterial requires assetPath"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                path += ".mat";

            var shaderName = !string.IsNullOrWhiteSpace(a.shaderName) ? a.shaderName.Trim() : "Standard";
            Shader shader = null;
            if (shaderName.Contains("/") || shaderName.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderName);
            }
            if (shader == null)
                shader = Shader.Find(shaderName);
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
                return (Fail($"Shader not found: {shaderName}"), null);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createMaterial", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateShader(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createShader requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.shaderSource))
                return (Fail("createShader requires shaderSource"), null);

            var source = a.shaderSource.Trim();
            if (source.Length < 10 || !source.Contains("Shader") || !source.Contains("{"))
                return (Fail("createShader requires valid ShaderLab source (must contain Shader and {)"), null);
            if (source.Length > 65536)
                source = source.Substring(0, 65536);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                path += ".shader";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var fullPath = Path.GetFullPath(uniquePath);
            File.WriteAllText(fullPath, source);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createShader", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) UpdateMaterial(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("updateMaterial requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.materialProperties))
                return (Fail("updateMaterial requires materialProperties"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                path += ".mat";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                return (Fail($"Material not found: {path}"), null);

            var props = ParseMaterialPropertiesJson(a.materialProperties.Trim());
            if (props == null || props.Count == 0)
                return (Fail("invalid materialProperties JSON"), null);

            var previousState = new Dictionary<string, object>();
            foreach (var kv in props)
            {
                try
                {
                    if (mat.HasProperty(kv.Key))
                    {
                        var propType = GetMaterialPropertyType(mat, kv.Key);
                        if (propType == MaterialPropertyType.Color)
                        {
                            previousState[kv.Key] = mat.GetColor(kv.Key);
                        }
                        else if (propType == MaterialPropertyType.Float || propType == MaterialPropertyType.Range)
                        {
                            previousState[kv.Key] = mat.GetFloat(kv.Key);
                        }
                        else if (propType == MaterialPropertyType.Int)
                        {
                            previousState[kv.Key] = mat.GetInteger(kv.Key);
                        }
                        else if (propType == MaterialPropertyType.Texture)
                        {
                            var tex = mat.GetTexture(kv.Key);
                            previousState[kv.Key] = tex != null ? AssetDatabase.GetAssetPath(tex) : "";
                        }
                    }
                }
                catch { }
            }

            foreach (var kv in props)
            {
                try
                {
                    if (!mat.HasProperty(kv.Key)) continue;
                    if (kv.Value is float[] arr && arr.Length >= 4)
                    {
                        mat.SetColor(kv.Key, new Color(arr[0], arr[1], arr[2], arr.Length > 3 ? arr[3] : 1f));
                    }
                    else if (kv.Value is float f)
                    {
                        mat.SetFloat(kv.Key, f);
                    }
                    else if (kv.Value is int i)
                    {
                        mat.SetInteger(kv.Key, i);
                    }
                    else if (kv.Value is string s && !string.IsNullOrEmpty(s))
                    {
                        var tex = AssetDatabase.LoadAssetAtPath<Texture>(s);
                        if (tex != null)
                            mat.SetTexture(kv.Key, tex);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoBuddy] updateMaterial: failed to set {kv.Key}: {ex.Message}");
                }
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            var prevJson = SerializeMaterialStateForRevert(previousState);
            return (Ok(path), new ActionRevertRecord { action = "updateMaterial", assetPath = path, previousMaterialState = prevJson });
        }

        private enum MaterialPropertyType { Unknown, Float, Int, Color, Texture, Range }
        private static MaterialPropertyType GetMaterialPropertyType(Material mat, string name)
        {
            for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
            {
                if (mat.shader.GetPropertyName(i) == name)
                {
                    var t = mat.shader.GetPropertyType(i);
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Color || t == UnityEngine.Rendering.ShaderPropertyType.Vector)
                        return MaterialPropertyType.Color;
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range)
                        return MaterialPropertyType.Float;
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        return MaterialPropertyType.Texture;
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Int)
                        return MaterialPropertyType.Int;
                    return MaterialPropertyType.Unknown;
                }
            }
            return MaterialPropertyType.Unknown;
        }

        private static Dictionary<string, object> ParseMaterialPropertiesJson(string json)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(json) || !json.StartsWith("{")) return result;
            int i = 1;
            while (i < json.Length)
            {
                var key = ParseJsonString(json, ref i);
                if (key == null) break;
                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++;
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                object val = null;
                if (json[i] == '[')
                {
                    val = ParseJsonNumberArray(json, ref i);
                }
                else if (json[i] == '"')
                {
                    val = ParseJsonString(json, ref i);
                }
                else if (json[i] == '-' || json[i] == '.' || char.IsDigit(json[i]))
                {
                    val = ParseJsonNumberValue(json, ref i);
                }
                if (val != null)
                    result[key] = val;
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; SkipWhitespace(json, ref i); }
            }
            return result;
        }
        private static void SkipWhitespace(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        private static string ParseJsonString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\') { i++; if (i < s.Length) sb.Append(s[i++]); }
                else sb.Append(s[i++]);
            }
            if (i < s.Length) i++;
            return sb.ToString();
        }
        private static float[] ParseJsonNumberArray(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '[') return null;
            i++;
            var list = new List<float>();
            SkipWhitespace(s, ref i);
            while (i < s.Length && s[i] != ']')
            {
                var n = ParseJsonNumber(s, ref i);
                if (n.HasValue) list.Add(n.Value);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; SkipWhitespace(s, ref i); }
            }
            if (i < s.Length) i++;
            return list.ToArray();
        }
        private static float? ParseJsonNumber(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+')) i++;
            if (i == start) return null;
            return float.TryParse(s.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
        }
        private static object ParseJsonNumberValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+')) i++;
            if (i == start) return null;
            var sub = s.Substring(start, i - start);
            if (sub.Contains(".") || sub.ToLowerInvariant().Contains("e"))
                return float.TryParse(sub, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? (object)f : null;
            return int.TryParse(sub, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? (object)n : null;
        }
        private static string SerializeMaterialStateForRevert(Dictionary<string, object> state)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{");
            bool first = true;
            foreach (var kv in state)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\":");
                if (kv.Value is Color c)
                    sb.Append("[").Append(c.r.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(c.g.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(c.b.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(c.a.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("]");
                else if (kv.Value is float f)
                    sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else if (kv.Value is int n)
                    sb.Append(n);
                else if (kv.Value is string str)
                    sb.Append("\"").Append(str.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                else
                    sb.Append("null");
            }
            sb.Append("}");
            return sb.ToString();
        }
        private static void RevertMaterialProperties(ActionRevertRecord r)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(r.assetPath);
            if (mat == null) return;
            var props = ParseMaterialPropertiesJson(r.previousMaterialState);
            if (props == null || props.Count == 0) return;
            foreach (var kv in props)
            {
                try
                {
                    if (!mat.HasProperty(kv.Key)) continue;
                    if (kv.Value is float[] arr && arr.Length >= 4)
                    {
                        mat.SetColor(kv.Key, new Color(arr[0], arr[1], arr[2], arr.Length > 3 ? arr[3] : 1f));
                    }
                    else if (kv.Value is float f)
                    {
                        mat.SetFloat(kv.Key, f);
                    }
                    else if (kv.Value is int i)
                    {
                        mat.SetInteger(kv.Key, i);
                    }
                    else if (kv.Value is string s)
                    {
                        var tex = string.IsNullOrEmpty(s) ? null : AssetDatabase.LoadAssetAtPath<Texture>(s);
                        mat.SetTexture(kv.Key, tex);
                    }
                }
                catch { }
            }
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }

        // ── Print Detail Levels ─────────────────────────────────────────────
        // "path" (default): paths only, max 200 results
        // "props": path + tag + layer + component names, max 50 results
        // "full": path + tag + layer + all component properties, max 10 results

        private static int GetDetailLimit(string detail)
        {
            switch ((detail ?? "").ToLowerInvariant())
            {
                case "full": return 10;
                case "props": return 50;
                default: return 200;
            }
        }

        private static ActionResult PrintSceneHierarchy(EditorAction a)
        {
            var detailLevel = (a.detail ?? "path").ToLowerInvariant();
            int limit = GetDetailLimit(detailLevel);
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            if (detailLevel == "path")
            {
                var paths = new List<string>();
                foreach (var root in roots)
                    CollectPaths(root.transform, "", paths, limit);
                var json = "[" + string.Join(",", paths.Select(p => "\"" + EscapeJson(p) + "\"")) + "]";
                return Ok(null, json);
            }

            // props or full
            var items = new List<Transform>();
            foreach (var root in roots)
                CollectTransforms(root.transform, "", null, items, limit);

            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var t = items[i];
                var go = t.gameObject;
                var goPath = GetHierarchyPath(t);
                sb.Append("{\"path\":\"").Append(EscapeJson(goPath))
                  .Append("\",\"name\":\"").Append(EscapeJson(go.name))
                  .Append("\",\"tag\":\"").Append(EscapeJson(go.tag))
                  .Append("\",\"layer\":\"").Append(EscapeJson(LayerMask.LayerToName(go.layer)))
                  .Append("\",\"active\":").Append(go.activeSelf ? "true" : "false");

                var comps = go.GetComponents<Component>();
                sb.Append(",\"components\":[");
                bool firstComp = true;
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    if (!firstComp) sb.Append(",");
                    firstComp = false;

                    if (detailLevel == "full")
                    {
                        // Full detail: component type + serialized properties
                        sb.Append("{\"type\":\"").Append(EscapeJson(comp.GetType().Name)).Append("\"");
                        if (comp is Transform tr)
                        {
                            sb.Append(",\"position\":[").Append(tr.localPosition.x).Append(",").Append(tr.localPosition.y).Append(",").Append(tr.localPosition.z).Append("]");
                            sb.Append(",\"rotation\":[").Append(tr.localEulerAngles.x).Append(",").Append(tr.localEulerAngles.y).Append(",").Append(tr.localEulerAngles.z).Append("]");
                            sb.Append(",\"scale\":[").Append(tr.localScale.x).Append(",").Append(tr.localScale.y).Append(",").Append(tr.localScale.z).Append("]");
                        }
                        else
                        {
                            try
                            {
                                var so = new SerializedObject(comp);
                                var prop = so.GetIterator();
                                sb.Append(",\"props\":{");
                                bool firstProp = true;
                                int propCount = 0;
                                if (prop.NextVisible(true))
                                {
                                    do
                                    {
                                        if (prop.name == "m_Script" || prop.name == "m_ObjectHideFlags") continue;
                                        if (propCount >= 20) { sb.Append(",\"...\":\"truncated\""); break; }
                                        if (!firstProp) sb.Append(",");
                                        firstProp = false;
                                        sb.Append("\"").Append(EscapeJson(prop.name)).Append("\":");
                                        AppendSerializedPropertyValue(sb, prop);
                                        propCount++;
                                    } while (prop.NextVisible(false));
                                }
                                sb.Append("}");
                            }
                            catch
                            {
                                sb.Append(",\"props\":{}");
                            }
                        }
                        sb.Append("}");
                    }
                    else
                    {
                        // Props detail: just component type name
                        sb.Append("\"").Append(EscapeJson(comp.GetType().Name)).Append("\"");
                    }
                }
                sb.Append("]}");
            }
            sb.Append("]");
            return Ok(null, sb.ToString());
        }

        private static void AppendSerializedPropertyValue(System.Text.StringBuilder sb, SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: sb.Append(prop.intValue); break;
                case SerializedPropertyType.Boolean: sb.Append(prop.boolValue ? "true" : "false"); break;
                case SerializedPropertyType.Float: sb.Append(prop.floatValue); break;
                case SerializedPropertyType.String: sb.Append("\"").Append(EscapeJson(prop.stringValue ?? "")).Append("\""); break;
                case SerializedPropertyType.Enum: sb.Append("\"").Append(EscapeJson(prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString())).Append("\""); break;
                case SerializedPropertyType.ObjectReference:
                    var obj = prop.objectReferenceValue;
                    sb.Append(obj != null ? "\"" + EscapeJson(obj.name) + "\"" : "null");
                    break;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    sb.Append("[").Append(c.r).Append(",").Append(c.g).Append(",").Append(c.b).Append(",").Append(c.a).Append("]");
                    break;
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    sb.Append("[").Append(v2.x).Append(",").Append(v2.y).Append("]");
                    break;
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    sb.Append("[").Append(v3.x).Append(",").Append(v3.y).Append(",").Append(v3.z).Append("]");
                    break;
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    sb.Append("[").Append(v4.x).Append(",").Append(v4.y).Append(",").Append(v4.z).Append(",").Append(v4.w).Append("]");
                    break;
                default: sb.Append("\"(").Append(prop.propertyType).Append(")\""); break;
            }
        }

        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void CollectPaths(Transform t, string prefix, List<string> paths, int limit = 200)
        {
            if (paths.Count >= limit) return;
            var name = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            paths.Add(name);
            for (int i = 0; i < t.childCount; i++)
            {
                if (paths.Count >= limit) return;
                CollectPaths(t.GetChild(i), name, paths, limit);
            }
        }

        private static void CollectTransforms(Transform t, string prefix, string filter, List<Transform> list, int limit)
        {
            if (list.Count >= limit) return;
            var path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            if (filter == null || path.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                list.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                if (list.Count >= limit) return;
                CollectTransforms(t.GetChild(i), path, filter, list, limit);
            }
        }

        private static ActionResult PrintGameObjects(EditorAction a)
        {
            var detailLevel = (a.detail ?? "path").ToLowerInvariant();
            int limit = GetDetailLimit(detailLevel);
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var filter = !string.IsNullOrWhiteSpace(a.path) ? a.path.Trim() : null;

            if (detailLevel == "path")
            {
                // Path-only: just paths filtered by prefix
                var paths = new List<string>();
                foreach (var root in roots)
                    CollectFilteredPaths(root.transform, "", filter, paths, limit);
                var json = "[" + string.Join(",", paths.Select(p => "\"" + EscapeJson(p) + "\"")) + "]";
                return Ok(null, json);
            }

            // props or full: delegate to hierarchy printer with filter
            var items = new List<Transform>();
            foreach (var root in roots)
                CollectTransforms(root.transform, "", filter, items, limit);

            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var t = items[i];
                var go = t.gameObject;
                var goPath = GetHierarchyPath(t);
                sb.Append("{\"path\":\"").Append(EscapeJson(goPath))
                  .Append("\",\"name\":\"").Append(EscapeJson(go.name))
                  .Append("\",\"tag\":\"").Append(EscapeJson(go.tag))
                  .Append("\",\"layer\":\"").Append(EscapeJson(LayerMask.LayerToName(go.layer)));

                if (detailLevel == "full")
                {
                    sb.Append("\",\"active\":").Append(go.activeSelf ? "true" : "false");
                    var comps = go.GetComponents<Component>();
                    sb.Append(",\"components\":[");
                    bool firstComp = true;
                    foreach (var comp in comps)
                    {
                        if (comp == null) continue;
                        if (!firstComp) sb.Append(",");
                        firstComp = false;
                        sb.Append("{\"type\":\"").Append(EscapeJson(comp.GetType().Name)).Append("\"}");
                    }
                    sb.Append("]}");
                }
                else
                {
                    // props level: add component names
                    var comps = go.GetComponents<Component>();
                    var compNames = comps.Where(c => c != null).Select(c => c.GetType().Name).ToArray();
                    sb.Append("\",\"components\":[")
                      .Append(string.Join(",", compNames.Select(cn => "\"" + EscapeJson(cn) + "\"")))
                      .Append("]}");
                }
            }
            sb.Append("]");
            return Ok(null, sb.ToString());
        }

        private static void CollectFilteredPaths(Transform t, string prefix, string filter, List<string> paths, int limit)
        {
            if (paths.Count >= limit) return;
            var path = string.IsNullOrEmpty(prefix) ? t.name : prefix + "/" + t.name;
            if (filter == null || path.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
            for (int i = 0; i < t.childCount; i++)
            {
                if (paths.Count >= limit) return;
                CollectFilteredPaths(t.GetChild(i), path, filter, paths, limit);
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static ActionResult PrintAssets(EditorAction a)
        {
            var detailLevel = (a.detail ?? "path").ToLowerInvariant();
            int limit = GetDetailLimit(detailLevel);
            var folder = !string.IsNullOrWhiteSpace(a.folder) ? a.folder.Trim() : "Assets";
            if (!folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                folder = "Assets/" + folder.TrimStart('/');
            var guids = AssetDatabase.FindAssets("t:Object", new[] { folder });
            var allPaths = guids.Select(g => AssetDatabase.GUIDToAssetPath(g)).Where(p => !string.IsNullOrEmpty(p)).Take(limit).ToList();

            if (detailLevel == "path")
            {
                var json = "[" + string.Join(",", allPaths.Select(p => "\"" + EscapeJson(p) + "\"")) + "]";
                return Ok(null, json);
            }

            // props or full: include asset type and size
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            for (int i = 0; i < allPaths.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var p = allPaths[i];
                var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
                sb.Append("{\"path\":\"").Append(EscapeJson(p))
                  .Append("\",\"type\":\"").Append(EscapeJson(ext));

                if (detailLevel == "full")
                {
                    try
                    {
                        var fi = new System.IO.FileInfo(System.IO.Path.Combine(Application.dataPath.Replace("/Assets", ""), p));
                        if (fi.Exists) sb.Append("\",\"size\":").Append(fi.Length);
                        else sb.Append("\"");
                        // Load the main asset to get its Unity type
                        var asset = AssetDatabase.LoadMainAssetAtPath(p);
                        if (asset != null)
                        {
                            sb.Append(",\"assetType\":\"").Append(EscapeJson(asset.GetType().Name)).Append("\"");
                            // Get dependencies
                            var deps = AssetDatabase.GetDependencies(p, false);
                            var directDeps = deps.Where(d => !string.Equals(d, p, StringComparison.OrdinalIgnoreCase)).Take(10).ToArray();
                            if (directDeps.Length > 0)
                            {
                                sb.Append(",\"deps\":[").Append(string.Join(",", directDeps.Select(d => "\"" + EscapeJson(d) + "\""))).Append("]");
                            }
                        }
                        else
                        {
                            sb.Append("\"");
                        }
                    }
                    catch
                    {
                        sb.Append("\"");
                    }
                    sb.Append("}");
                }
                else
                {
                    sb.Append("\"}");
                }
            }
            sb.Append("]");
            return Ok(null, sb.ToString());
        }

        private static ActionResult UpdateAssetImporter(EditorAction a)
        {
            var path = !string.IsNullOrWhiteSpace(a.assetPath) ? a.assetPath.Trim() : (a.path ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return Fail("updateAssetImporter requires assetPath or path");

            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
                return Fail($"Asset not found: {path}");

            var textureImporter = importer as TextureImporter;
            if (textureImporter != null && a.maxTextureSize > 0)
            {
                Undo.RecordObject(textureImporter, "CoBuddy updateAssetImporter");
                textureImporter.maxTextureSize = a.maxTextureSize;
                textureImporter.SaveAndReimport();
                return Ok(path);
            }

            if (textureImporter == null)
                return Fail($"Asset at {path} is not a texture; only TextureImporter (maxTextureSize) is supported");
            return Fail("updateAssetImporter requires maxTextureSize > 0 for textures");
        }

        private static ActionResult UpdateCanvas(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return Fail("updateCanvas requires target");
            var go = FindByPath(a.target);
            if (go == null)
                return FailTargetNotFound(a.target);
            var canvas = go.GetComponent<UnityEngine.Canvas>();
            if (canvas == null)
                return Fail($"Target {a.target} has no Canvas component");
            var modeStr = (a.renderMode ?? "").Trim();
            if (string.IsNullOrEmpty(modeStr))
                return Fail("updateCanvas requires renderMode (ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace)");
            UnityEngine.RenderMode mode;
            switch (modeStr.ToLowerInvariant())
            {
                case "screenspaceoverlay":
                case "screen space overlay":
                case "overlay":
                    mode = UnityEngine.RenderMode.ScreenSpaceOverlay;
                    break;
                case "screenspacecamera":
                case "screen space camera":
                case "camera":
                    mode = UnityEngine.RenderMode.ScreenSpaceCamera;
                    break;
                case "worldspace":
                case "world space":
                case "world":
                    mode = UnityEngine.RenderMode.WorldSpace;
                    break;
                default:
                    return Fail($"Unknown renderMode: {modeStr}. Use ScreenSpaceOverlay, ScreenSpaceCamera, or WorldSpace");
            }
            Undo.RecordObject(canvas, "CoBuddy updateCanvas");
            canvas.renderMode = mode;
            return Ok(GetPath(go));
        }

        private static ActionResult UpdateUIImage(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return Fail("updateUIImage requires target");
            var go = FindByPath(a.target);
            if (go == null)
                return FailTargetNotFound(a.target);
            var img = go.GetComponent<UnityEngine.UI.Image>();
            if (img == null)
                return Fail($"Target {a.target} has no Image component");
            Undo.RecordObject(img, "CoBuddy updateUIImage");
            Color? newColor = null;
            if (a.color != null && a.color.Length >= 3)
            {
                var r = a.color[0]; var g = a.color[1]; var b = a.color[2];
                if (r > 1 || g > 1 || b > 1) { r /= 255f; g /= 255f; b /= 255f; }
                var alpha = a.color.Length >= 4 ? a.color[3] : 1f;
                if (alpha > 1) alpha /= 255f;
                newColor = new Color(r, g, b, alpha);
            }
            else if (a.colorR >= 0 || a.colorG >= 0 || a.colorB >= 0 || a.colorA >= 0)
            {
                var r = a.colorR >= 0 ? (a.colorR > 1 ? a.colorR / 255f : a.colorR) : img.color.r;
                var g = a.colorG >= 0 ? (a.colorG > 1 ? a.colorG / 255f : a.colorG) : img.color.g;
                var b = a.colorB >= 0 ? (a.colorB > 1 ? a.colorB / 255f : a.colorB) : img.color.b;
                var alpha = a.colorA >= 0 ? (a.colorA > 1 ? a.colorA / 255f : a.colorA) : img.color.a;
                newColor = new Color(r, g, b, alpha);
            }
            if (newColor.HasValue)
                img.color = newColor.Value;
            if (!string.IsNullOrWhiteSpace(a.imageType))
            {
                var t = a.imageType.Trim().ToLowerInvariant();
                if (t == "simple") img.type = UnityEngine.UI.Image.Type.Simple;
                else if (t == "sliced") img.type = UnityEngine.UI.Image.Type.Sliced;
                else if (t == "tiled") img.type = UnityEngine.UI.Image.Type.Tiled;
                else if (t == "filled") img.type = UnityEngine.UI.Image.Type.Filled;
            }
            if (!string.IsNullOrWhiteSpace(a.spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<UnityEngine.Sprite>(a.spritePath.Trim());
                if (sprite != null)
                    img.sprite = sprite;
            }
            return Ok(GetPath(go));
        }

        private static ActionResult UpdateUIText(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return Fail("updateUIText requires target");
            var go = FindByPath(a.target);
            if (go == null)
                return FailTargetNotFound(a.target);
            var text = go.GetComponent<UnityEngine.UI.Text>();
            if (text == null)
                return Fail($"Target {a.target} has no Text component");
            Undo.RecordObject(text, "CoBuddy updateUIText");
            if (a.text != null)
                text.text = a.text;
            Color? newColor = null;
            if (a.color != null && a.color.Length >= 3)
            {
                var r = a.color[0]; var g = a.color[1]; var b = a.color[2];
                if (r > 1 || g > 1 || b > 1) { r /= 255f; g /= 255f; b /= 255f; }
                var alpha = a.color.Length >= 4 ? a.color[3] : 1f;
                if (alpha > 1) alpha /= 255f;
                newColor = new Color(r, g, b, alpha);
            }
            else if (a.colorR >= 0 || a.colorG >= 0 || a.colorB >= 0 || a.colorA >= 0)
            {
                var r = a.colorR >= 0 ? (a.colorR > 1 ? a.colorR / 255f : a.colorR) : text.color.r;
                var g = a.colorG >= 0 ? (a.colorG > 1 ? a.colorG / 255f : a.colorG) : text.color.g;
                var b = a.colorB >= 0 ? (a.colorB > 1 ? a.colorB / 255f : a.colorB) : text.color.b;
                var alpha = a.colorA >= 0 ? (a.colorA > 1 ? a.colorA / 255f : a.colorA) : text.color.a;
                newColor = new Color(r, g, b, alpha);
            }
            if (newColor.HasValue)
                text.color = newColor.Value;
            if (a.fontSize > 0)
                text.fontSize = a.fontSize;
            if (!string.IsNullOrWhiteSpace(a.alignment))
            {
                var align = a.alignment.Trim().ToLowerInvariant();
                UnityEngine.TextAnchor anchor;
                switch (align)
                {
                    case "upperleft": anchor = UnityEngine.TextAnchor.UpperLeft; break;
                    case "uppercenter":
                    case "uppercentre": anchor = UnityEngine.TextAnchor.UpperCenter; break;
                    case "upperright": anchor = UnityEngine.TextAnchor.UpperRight; break;
                    case "middleleft": anchor = UnityEngine.TextAnchor.MiddleLeft; break;
                    case "middlecenter":
                    case "middlecentre":
                    case "center":
                    case "centre": anchor = UnityEngine.TextAnchor.MiddleCenter; break;
                    case "middleright": anchor = UnityEngine.TextAnchor.MiddleRight; break;
                    case "lowerleft": anchor = UnityEngine.TextAnchor.LowerLeft; break;
                    case "lowercenter":
                    case "lowercentre": anchor = UnityEngine.TextAnchor.LowerCenter; break;
                    case "lowerright": anchor = UnityEngine.TextAnchor.LowerRight; break;
                    default: anchor = UnityEngine.TextAnchor.MiddleCenter; break;
                }
                text.alignment = anchor;
            }
            return Ok(GetPath(go));
        }

        private static (ActionResult, ActionRevertRecord) CreateUXML(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createUXML requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.content))
                return (Fail("createUXML requires content"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                path += ".uxml";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, a.content.Trim());
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createUXML", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateUSS(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createUSS requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.content))
                return (Fail("createUSS requires content"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                path += ".uss";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, a.content.Trim());
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createUSS", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateUIDocument(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.uxmlPath))
                return (Fail("createUIDocument requires uxmlPath"), null);

            var uxmlPath = a.uxmlPath.Trim();
            var treeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (treeAsset == null)
                return (Fail($"UXML not found: {uxmlPath}. Create UXML first."), null);

            var name = !string.IsNullOrWhiteSpace(a.name) ? a.name.Trim() : "UI_Document";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createUIDocument");

            var uiDoc = go.AddComponent<UIDocument>();
            uiDoc.visualTreeAsset = treeAsset;

            if (!string.IsNullOrWhiteSpace(a.panelSettingsPath))
            {
                var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(a.panelSettingsPath.Trim());
                if (panelSettings != null)
                    uiDoc.panelSettings = panelSettings;
            }
            else
            {
                var defaultPanel = AssetDatabase.FindAssets("t:PanelSettings").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                if (string.IsNullOrEmpty(defaultPanel))
                    defaultPanel = EnsureDefaultPanelSettingsExists();
                if (!string.IsNullOrEmpty(defaultPanel))
                    uiDoc.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(defaultPanel);
            }

            var pathStr = GetPath(go);
            return (Ok(pathStr), new ActionRevertRecord { action = "createUIDocument", path = pathStr });
        }

        private static (ActionResult, ActionRevertRecord) CreateAnimationClip(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createAnimationClip requires assetPath"), null);
            if (string.IsNullOrWhiteSpace(a.animationCurves))
                return (Fail("createAnimationClip requires animationCurves (JSON)"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                path += ".anim";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var clip = new AnimationClip();
            clip.legacy = false;
            if (a.loop)
                clip.wrapMode = WrapMode.Loop;

            var curvesJson = a.animationCurves.Trim();
            if (curvesJson.StartsWith("["))
                curvesJson = "{\"curves\":" + curvesJson + "}";
            AnimationCurvesWrapper wrapper;
            try
            {
                wrapper = JsonUtility.FromJson<AnimationCurvesWrapper>(curvesJson);
            }
            catch (Exception ex)
            {
                return (Fail("createAnimationClip: animationCurves must be valid JSON. Expected: {\"curves\":[{\"path\":\"\",\"type\":\"CanvasGroup\",\"property\":\"m_Alpha\",\"keyframes\":[{\"t\":0,\"v\":0}]}]}. Error: " + (ex.Message ?? "Parse failed")), null);
            }
            if (wrapper?.curves == null || wrapper.curves.Length == 0)
                return (Fail("createAnimationClip: animationCurves must contain at least one curve with path, type, property, and keyframes array"), null);

            float maxTime = 0f;
            foreach (var c in wrapper.curves)
            {
                if (string.IsNullOrWhiteSpace(c.type) || string.IsNullOrWhiteSpace(c.property) || c.keyframes == null || c.keyframes.Length == 0)
                    continue;
                var curveType = ResolveAnimationCurveType(c.type);
                if (curveType == null)
                    continue;
                var curve = new AnimationCurve();
                var keyCount = c.keyframes.Length;
                for (int ki = 0; ki < keyCount; ki++)
                {
                    var kf = c.keyframes[ki];
                    var key = new Keyframe(kf.t, kf.v);
                    float inT = kf.inT, outT = kf.outT;
                    if (!string.IsNullOrWhiteSpace(kf.easing))
                    {
                        var e = kf.easing.Trim().ToLowerInvariant();
                        if (e == "ease-in") { inT = ki > 0 ? 2f : 0; outT = ki < keyCount - 1 ? 0.5f : 0; }
                        else if (e == "ease-out") { inT = ki > 0 ? 0.5f : 0; outT = ki < keyCount - 1 ? 2f : 0; }
                        else if (e == "ease-in-out") { inT = 0.8f; outT = 0.8f; }
                        else { inT = 0; outT = 0; }
                    }
                    if (inT != 0 || outT != 0)
                    {
                        key.inTangent = inT;
                        key.outTangent = outT;
                    }
                    curve.AddKey(key);
                    if (kf.t > maxTime) maxTime = kf.t;
                }
                var curvePath = string.IsNullOrWhiteSpace(c.path) ? "" : c.path;
                clip.SetCurve(curvePath, curveType, c.property, curve);
            }

            clip.frameRate = 60f;
            AssetDatabase.CreateAsset(clip, uniquePath);
            AssetDatabase.SaveAssets();

            string controllerPath = null;
            if (a.createController)
            {
                var ctrlPath = Path.Combine(Path.GetDirectoryName(uniquePath), Path.GetFileNameWithoutExtension(uniquePath) + "Controller.controller");
                ctrlPath = AssetDatabase.GenerateUniqueAssetPath(ctrlPath);
                var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(ctrlPath, clip);
                if (controller != null)
                {
                    controllerPath = ctrlPath;
                    AssetDatabase.SaveAssets();
                }
            }

            var data = controllerPath != null ? "{\"clipPath\":\"" + uniquePath + "\",\"controllerPath\":\"" + controllerPath + "\"}" : "{\"clipPath\":\"" + uniquePath + "\"}";
            var record = new ActionRevertRecord { action = "createAnimationClip", assetPath = uniquePath };
            if (!string.IsNullOrEmpty(controllerPath))
                record.controllerPath = controllerPath;
            return (Ok(uniquePath, data), record);
        }

        private static Type ResolveAnimationCurveType(string typeName)
        {
            var t = typeName.Trim();
            if (t.Equals("RectTransform", StringComparison.OrdinalIgnoreCase) || t.Equals("Transform"))
                return typeof(RectTransform);
            if (t.Equals("Image", StringComparison.OrdinalIgnoreCase))
                return typeof(UnityEngine.UI.Image);
            if (t.Equals("Text", StringComparison.OrdinalIgnoreCase))
                return typeof(UnityEngine.UI.Text);
            if (t.Equals("CanvasGroup", StringComparison.OrdinalIgnoreCase))
                return typeof(CanvasGroup);
            return Type.GetType(t + ", UnityEngine") ?? Type.GetType(t + ", UnityEngine.CoreModule");
        }

        [Serializable]
        private class KeyframeDef { public float t; public float v; public float inT; public float outT; public string easing; }
        [Serializable]
        private class CurveDef { public string path; public string type; public string property; public KeyframeDef[] keyframes; }
        [Serializable]
        private class AnimationCurvesWrapper { public CurveDef[] curves; }

        private static (ActionResult, ActionRevertRecord) AddAnimator(EditorAction a, ActionResult[] prevResults = null)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("addAnimator requires target (GameObject path)"), null);

            var ctrlPath = a.controllerPath?.Trim();
            if (string.IsNullOrWhiteSpace(ctrlPath) || ctrlPath == "@prev" || ctrlPath == "@last")
            {
                for (int i = (prevResults?.Length ?? 0) - 1; i >= 0; i--)
                {
                    var r = prevResults[i];
                    if (r?.success == true && !string.IsNullOrEmpty(r.data) && r.data.Contains("controllerPath"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(r.data, @"""controllerPath""\s*:\s*""([^""]+)""");
                        if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                        {
                            ctrlPath = match.Groups[1].Value;
                            break;
                        }
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(ctrlPath))
                return (Fail("addAnimator requires controllerPath or a preceding createAnimationClip with createController:true"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
            if (controller == null)
                return (Fail($"AnimatorController not found: {ctrlPath}"), null);

            var animator = go.GetComponent<Animator>();
            if (animator == null)
            {
                animator = Undo.AddComponent<Animator>(go);
            }
            animator.runtimeAnimatorController = controller;

            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addAnimator", targetPath = a.target, componentType = "Animator" });
        }

        private static (ActionResult, ActionRevertRecord) CreateUILoopingScript(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.assetPath))
                return (Fail("createUILoopingScript requires assetPath"), null);

            string scriptContent;
            if (!string.IsNullOrWhiteSpace(a.content))
            {
                scriptContent = a.content.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(a.elementName) && !string.IsNullOrWhiteSpace(a.toggleClass))
            {
                var elem = EscapeCSharpString(a.elementName.Trim());
                var cls = EscapeCSharpString(a.toggleClass.Trim());
                scriptContent = $@"using UnityEngine;
using UnityEngine.UIElements;

/// <summary>UI Toolkit looping animation via TransitionEndEvent. Add to UIDocument GameObject.</summary>
public class UILoopingAnimation : MonoBehaviour
{{
    public string elementName = ""{elem}"";
    public string toggleClass = ""{cls}"";
    private UIDocument document;
    private VisualElement element;
    private bool isActive;

    void OnEnable()
    {{
        document = GetComponent<UIDocument>();
        if (document == null) return;
        element = document.rootVisualElement.Q(elementName);
        if (element == null) return;
        element.RegisterCallback<TransitionEndEvent>(OnTransitionEnd);
        isActive = false;
        element.AddToClassList(toggleClass);
        isActive = true;
    }}

    void OnDisable()
    {{
        if (element != null)
            element.UnregisterCallback<TransitionEndEvent>(OnTransitionEnd);
    }}

    void OnTransitionEnd(TransitionEndEvent evt)
    {{
        if (element == null) return;
        if (isActive)
        {{
            element.RemoveFromClassList(toggleClass);
            isActive = false;
        }}
        else
        {{
            element.AddToClassList(toggleClass);
            isActive = true;
        }}
    }}
}}";
            }
            else
                return (Fail("createUILoopingScript requires either content (C# source) or both elementName and toggleClass"), null);

            var path = a.assetPath.Trim();
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                path += ".cs";
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, scriptContent);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createUILoopingScript", assetPath = uniquePath });
        }

        private static string EscapeCSharpString(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static (ActionResult, ActionRevertRecord) CopyAsset(EditorAction a)
        {
            var src = (a.path ?? a.sourcePath ?? "").Trim();
            var dest = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(src)) return (Fail("copyAsset requires path (source)"), null);
            if (string.IsNullOrEmpty(dest)) return (Fail("copyAsset requires assetPath (destination)"), null);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(src) == null)
                return (Fail($"Source asset not found: {src}"), null);
            var uniqueDest = AssetDatabase.GenerateUniqueAssetPath(dest);
            if (!AssetDatabase.CopyAsset(src, uniqueDest))
                return (Fail($"Failed to copy {src} to {uniqueDest}"), null);
            AssetDatabase.Refresh();
            return (Ok(uniqueDest), new ActionRevertRecord { action = "copyAsset", assetPath = uniqueDest });
        }

        private static (ActionResult, ActionRevertRecord) MoveAsset(EditorAction a)
        {
            var src = (a.path ?? a.sourcePath ?? "").Trim();
            var dest = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(src)) return (Fail("moveAsset requires path (source)"), null);
            if (string.IsNullOrEmpty(dest)) return (Fail("moveAsset requires assetPath (destination)"), null);
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(src);
            if (obj == null) return (Fail($"Source asset not found: {src}"), null);
            var err = AssetDatabase.MoveAsset(src, dest);
            if (!string.IsNullOrEmpty(err)) return (Fail(err), null);
            AssetDatabase.Refresh();
            return (Ok(dest), new ActionRevertRecord { action = "moveAsset", assetPath = dest, oldAssetPath = src });
        }

        private static (ActionResult, ActionRevertRecord) DeleteAsset(EditorAction a)
        {
            var p = (a.assetPath ?? a.path ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("deleteAsset requires assetPath"), null);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p) == null)
                return (Fail($"Asset not found: {p}"), null);
            if (!AssetDatabase.DeleteAsset(p))
                return (Fail($"Failed to delete {p}"), null);
            AssetDatabase.Refresh();
            return (Ok(p), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateFolder(EditorAction a)
        {
            var p = (a.assetPath ?? a.path ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createFolder requires assetPath"), null);
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), p.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(fullPath))
                return (Ok(p), null);
            Directory.CreateDirectory(fullPath);
            AssetDatabase.Refresh();
            return (Ok(p), new ActionRevertRecord { action = "createFolder", assetPath = p });
        }

        private static (ActionResult, ActionRevertRecord) ImportAsset(EditorAction a)
        {
            if (CurrentCancelCheck != null && CurrentCancelCheck()) return (Fail("Operation cancelled"), null);
            var p = (a.assetPath ?? a.path ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("importAsset requires assetPath"), null);
            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            return (Ok(p), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateTexture(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var data = (a.textureBase64 ?? a.content ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createTexture requires assetPath"), null);
            if (string.IsNullOrEmpty(data)) return (Fail("createTexture requires textureBase64 or content"), null);
            if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                p += ".png";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data.Contains(",") ? data.Split(',')[1] : data); }
            catch { return (Fail("createTexture: invalid base64"), null); }
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(fullPath, bytes);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createTexture", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateSprite(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var texPath = (a.texturePath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createSprite requires assetPath"), null);
            if (string.IsNullOrEmpty(texPath)) return (Fail("createSprite requires texturePath"), null);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null) return (Fail($"Texture not found: {texPath}"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer == null) return (Fail($"Texture not importable: {texPath}"), null);
            var rect = new Rect(a.rectX, a.rectY, a.rectW > 0 ? a.rectW : tex.width, a.rectH > 0 ? a.rectH : tex.height);
            var pivot = new Vector2(a.pivotX >= 0 ? a.pivotX : 0.5f, a.pivotY >= 0 ? a.pivotY : 0.5f);
            var sprite = UnityEngine.Sprite.Create(tex, rect, pivot);
            AssetDatabase.CreateAsset(sprite, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createSprite", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) ExecuteMenuItem(EditorAction a)
        {
            var menuPath = (a.menuPath ?? "").Trim();
            if (string.IsNullOrEmpty(menuPath)) return (Fail("executeMenuItem requires menuPath (e.g. File/Save)"), null);
            try
            {
                if (!EditorApplication.ExecuteMenuItem(menuPath))
                    return (Fail($"Menu item not found or failed: {menuPath}"), null);
            }
            catch (Exception ex) { return (Fail(ex.Message), null); }
            return (Ok(), null);
        }

        private static (ActionResult, ActionRevertRecord) EnterPlayMode(EditorAction a)
        {
            try
            {
                if (a.play && !Application.isPlaying)
                    EditorApplication.EnterPlaymode();
                else if (!a.play && Application.isPlaying)
                    EditorApplication.ExitPlaymode();
                else
                    return (Ok(Application.isPlaying ? "Playing" : "Stopped"), null);
            }
            catch (Exception ex) { return (Fail(ex.Message), null); }
            return (Ok(), null);
        }

        private static (ActionResult, ActionRevertRecord) SelectObject(EditorAction a)
        {
            var objPath = (a.objectPath ?? a.path ?? a.target ?? "").Trim();
            if (string.IsNullOrEmpty(objPath)) return (Fail("selectObject requires objectPath or path"), null);
            UnityEngine.Object obj = null;
            if (objPath.StartsWith("Assets/"))
                obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(objPath);
            else
            {
                var go = FindByPath(objPath);
                if (go != null) obj = go;
            }
            if (obj == null) return (Fail($"Object not found: {objPath}"), null);
            Selection.activeObject = obj;
            var resultPath = obj is GameObject goObj ? GetPath(goObj) : objPath;
            return (Ok(resultPath), null);
        }

        private static (ActionResult, ActionRevertRecord) FocusSceneView(EditorAction a)
        {
            var view = UnityEditor.SceneView.lastActiveSceneView;
            if (view == null) return (Fail("No Scene view available"), null);
            view.Focus();
            return (Ok(), null);
        }

        private static (ActionResult, ActionRevertRecord) AddRigidbody(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addRigidbody requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = Undo.AddComponent<Rigidbody>(go);
            if (a.mass > 0) rb.mass = a.mass;
            if (a.drag >= 0) rb.linearDamping = a.drag;
            if (a.angularDrag >= 0) rb.angularDamping = a.angularDrag;
            rb.useGravity = a.useGravity;
            if (!string.IsNullOrWhiteSpace(a.physicsMaterialPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(a.physicsMaterialPath.Trim());
                if (mat != null)
                {
                    foreach (var col in go.GetComponents<Collider>())
                    {
                        Undo.RecordObject(col, "Set PhysicMaterial");
                        col.sharedMaterial = mat;
                    }
                }
            }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addRigidbody", targetPath = a.target, componentType = "Rigidbody" });
        }

        private static (ActionResult, ActionRevertRecord) AddCollider(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addCollider requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var t = (a.colliderType ?? "Box").Trim().ToLowerInvariant();
            Collider col = null;
            if (t == "box")
            {
                var c = go.GetComponent<BoxCollider>();
                if (c == null) c = Undo.AddComponent<BoxCollider>(go);
                if (a.sizeX > 0 || a.sizeY > 0 || a.sizeZ > 0) c.size = new Vector3(a.sizeX > 0 ? a.sizeX : 1, a.sizeY > 0 ? a.sizeY : 1, a.sizeZ > 0 ? a.sizeZ : 1);
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else if (t == "sphere")
            {
                var c = go.GetComponent<SphereCollider>();
                if (c == null) c = Undo.AddComponent<SphereCollider>(go);
                if (a.radius > 0) c.radius = a.radius;
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else if (t == "capsule")
            {
                var c = go.GetComponent<CapsuleCollider>();
                if (c == null) c = Undo.AddComponent<CapsuleCollider>(go);
                if (a.radius > 0) c.radius = a.radius;
                if (a.height > 0) c.height = a.height;
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else if (t == "mesh" && !string.IsNullOrWhiteSpace(a.meshPath))
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(a.meshPath.Trim());
                if (mesh == null) return (Fail($"Mesh not found: {a.meshPath}"), null);
                var c = go.GetComponent<MeshCollider>();
                if (c == null) c = Undo.AddComponent<MeshCollider>(go);
                c.sharedMesh = mesh;
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else return (Fail("addCollider requires colliderType (Box/Sphere/Capsule/Mesh) and meshPath for Mesh"), null);
            if (!string.IsNullOrWhiteSpace(a.physicsMaterialPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(a.physicsMaterialPath.Trim());
                if (mat != null) col.material = mat;
            }
            var compType = t == "box" ? "BoxCollider" : t == "sphere" ? "SphereCollider" : t == "capsule" ? "CapsuleCollider" : "MeshCollider";
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCollider", targetPath = a.target, componentType = compType });
        }

        private static (ActionResult, ActionRevertRecord) SetColliderSize(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("setColliderSize requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var col = go.GetComponent<Collider>();
            if (col == null) return (Fail("Target has no Collider"), null);
            if (col is BoxCollider bc)
            {
                if (a.sizeX > 0 || a.sizeY > 0 || a.sizeZ > 0)
                { Undo.RecordObject(bc, "SetColliderSize"); bc.size = new Vector3(a.sizeX > 0 ? a.sizeX : bc.size.x, a.sizeY > 0 ? a.sizeY : bc.size.y, a.sizeZ > 0 ? a.sizeZ : bc.size.z); }
            }
            else if (col is SphereCollider sc && a.radius > 0)
            { Undo.RecordObject(sc, "SetColliderSize"); sc.radius = a.radius; }
            else if (col is CapsuleCollider cc)
            {
                if (a.radius > 0) { Undo.RecordObject(cc, "SetColliderSize"); cc.radius = a.radius; }
                if (a.height > 0) { Undo.RecordObject(cc, "SetColliderSize"); cc.height = a.height; }
            }
            return (Ok(GetPath(go)), null);
        }

        private static (ActionResult, ActionRevertRecord) CreatePhysicMaterial(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createPhysicMaterial requires assetPath"), null);
            if (!p.EndsWith(".physicMaterial", StringComparison.OrdinalIgnoreCase)) p += ".physicMaterial";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var mat = new PhysicsMaterial(Path.GetFileNameWithoutExtension(uniquePath));
            AssetDatabase.CreateAsset(mat, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createPhysicMaterial", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateInputActions(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var json = (a.inputActionsJson ?? a.content ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createInputActions requires assetPath"), null);
            if (string.IsNullOrEmpty(json)) return (Fail("createInputActions requires inputActionsJson or content"), null);
            if (!p.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase)) p += ".inputactions";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, json);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createInputActions", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) GenerateInputActionsCSharp(EditorAction a)
        {
            var assetPath = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(assetPath)) return (Fail("generateInputActionsCSharp requires assetPath"), null);
            if (!assetPath.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                assetPath += ".inputactions";

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
                return (Fail($"Asset not found at '{assetPath}'. Ensure the .inputactions file was created first."), null);

            // Use reflection to avoid a hard compile-time dependency on the Input System editor assembly.
            // IgnoreCase handles any capitalisation differences across Input System versions (e.g. generateCSharpClass vs generateCsharpClass).
            var reflFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
            var genProp = importer.GetType().GetProperty("generateCSharpClass", reflFlags);
            if (genProp == null)
                return (Fail("Could not find 'generateCSharpClass' property on InputActionImporter. Ensure com.unity.inputsystem is installed."), null);

            genProp.SetValue(importer, true);

            // Set class name (default to file name without extension)
            var className = (!string.IsNullOrEmpty(a.name) ? a.name : Path.GetFileNameWithoutExtension(assetPath).ToString()).Trim();
            var classNameProp = importer.GetType().GetProperty("csharpClassName", reflFlags)
                             ?? importer.GetType().GetProperty("className", reflFlags);
            classNameProp?.SetValue(importer, className);

            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var generatedCs = Path.ChangeExtension(assetPath, ".cs").ToString();
            return (Ok($"C# class '{className}' generation triggered. Unity will compile '{generatedCs}'."), null);
        }

        private static (ActionResult, ActionRevertRecord) AssignInputActions(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("assignInputActions requires target"), null);
            var assetPath = (a.assetPath ?? a.controllerPath ?? "").Trim();
            if (string.IsNullOrEmpty(assetPath)) return (Fail("assignInputActions requires assetPath (InputActionAsset)"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null) return (Fail($"InputActionAsset not found: {assetPath}"), null);
            var playerInputType = Type.GetType("UnityEngine.InputSystem.PlayerInput, Unity.InputSystem");
            if (playerInputType == null) return (Fail("Input System package not installed. Add com.unity.inputsystem"), null);
            var pi = go.GetComponent(playerInputType);
            if (pi == null) pi = Undo.AddComponent(go, playerInputType);
            var actionsProp = playerInputType.GetProperty("actions");
            if (actionsProp != null) actionsProp.SetValue(pi, asset);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "assignInputActions", targetPath = a.target, componentType = "PlayerInput" });
        }

        private static (ActionResult, ActionRevertRecord) CreateScript(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var content = (a.scriptContent ?? a.content ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createScript requires assetPath"), null);
            if (string.IsNullOrEmpty(content)) return (Fail("createScript requires scriptContent or content"), null);
            if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) p += ".cs";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createScript", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateAssemblyDefinition(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var asmName = (a.assemblyName ?? Path.GetFileNameWithoutExtension(p ?? "Assembly")).Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createAssemblyDefinition requires assetPath"), null);
            if (!p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) p += ".asmdef";
            var refs = a.references ?? Array.Empty<string>();
            var refList = refs.Length > 0 ? string.Join(",", refs.Select(r => "\"" + (r ?? "").Replace("\"", "\\\"") + "\"")) : "";
            var json = $@"{{
  ""name"": ""{asmName.Replace("\\", "\\\\").Replace("\"", "\\\"")}"",
  ""rootNamespace"": """",
  ""references"": [{refList}],
  ""includePlatforms"": [],
  ""excludePlatforms"": [],
  ""allowUnsafeCode"": false,
  ""overrideReferences"": false,
  ""precompiledReferences"": [],
  ""autoReferenced"": true,
  ""defineConstraints"": [],
  ""versionDefines"": [],
  ""noEngineReferences"": false
}}";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, json);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createAssemblyDefinition", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreatePackageManifest(EditorAction a)
        {
            var pkgName = (a.packageName ?? "").Trim();
            var pkgVersion = (a.packageVersion ?? "1.0.0").Trim();
            if (string.IsNullOrEmpty(pkgName)) return (Fail("createPackageManifest requires packageName"), null);
            var packagesPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages", "manifest.json");
            if (!File.Exists(packagesPath)) return (Fail("Packages/manifest.json not found"), null);
            string manifest;
            try { manifest = File.ReadAllText(packagesPath); }
            catch (Exception ex) { return (Fail(ex.Message), null); }
            if (manifest.Contains($"\"{pkgName}\"")) return (Ok(packagesPath), null);
            var insert = $"\"{pkgName}\": \"{pkgVersion}\"";
            if (manifest.Contains("\"dependencies\":{}") || manifest.Contains("\"dependencies\": {}"))
                manifest = manifest.Replace("\"dependencies\":{}", "\"dependencies\":{" + insert + "}").Replace("\"dependencies\": {}", "\"dependencies\": {" + insert + "}");
            else
                manifest = System.Text.RegularExpressions.Regex.Replace(manifest, @"(""dependencies""\s*:\s*\{[^}]*)(\})", "$1, " + insert + "$2", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!manifest.Contains(pkgName)) return (Fail("Could not add package to manifest"), null);
            try { File.WriteAllText(packagesPath, manifest); }
            catch (Exception ex) { return (Fail(ex.Message), null); }
            AssetDatabase.Refresh();
            return (Ok(packagesPath), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateTilemap(EditorAction a)
        {
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "Tilemap").Trim();
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            var gridType = Type.GetType("UnityEngine.Grid, UnityEngine.CoreModule");
            var tilemapType = Type.GetType("UnityEngine.Tilemaps.Tilemap, UnityEngine.TilemapModule");
            var rendererType = Type.GetType("UnityEngine.Tilemaps.TilemapRenderer, UnityEngine.TilemapModule");
            if (gridType == null || tilemapType == null) return (Fail("Tilemap module not available. Enable 2D Tilemap."), null);
            GameObject gridGo = null, tilemapGo = null;
            if (parent == null)
            {
                gridGo = new GameObject("Grid");
                Undo.RegisterCreatedObjectUndo(gridGo, "CoBuddy createTilemap");
                tilemapGo = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(tilemapGo, "CoBuddy createTilemap");
                tilemapGo.transform.SetParent(gridGo.transform, false);
            }
            else
            {
                var grid = parent.GetComponentInParent(gridType);
                if (grid != null)
                {
                    tilemapGo = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(tilemapGo, "CoBuddy createTilemap");
                    tilemapGo.transform.SetParent(grid.transform, false);
                }
                else
                {
                    gridGo = new GameObject("Grid");
                    Undo.RegisterCreatedObjectUndo(gridGo, "CoBuddy createTilemap");
                    gridGo.transform.SetParent(parent, false);
                    tilemapGo = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(tilemapGo, "CoBuddy createTilemap");
                    tilemapGo.transform.SetParent(gridGo.transform, false);
                }
            }
            if (gridGo != null) Undo.AddComponent(gridGo, gridType);
            Undo.AddComponent(tilemapGo, tilemapType);
            if (rendererType != null) Undo.AddComponent(tilemapGo, rendererType);
            var path = GetPath(tilemapGo);
            return (Ok(path), new ActionRevertRecord { action = "createTilemap", path = GetPath(gridGo ?? tilemapGo) });
        }

        private static (ActionResult, ActionRevertRecord) CreateTile(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var spritePath = (a.spritePath ?? a.texturePath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createTile requires assetPath"), null);
            if (string.IsNullOrEmpty(spritePath)) return (Fail("createTile requires spritePath"), null);
            var tileType = Type.GetType("UnityEngine.Tilemaps.Tile, UnityEngine.TilemapModule");
            if (tileType == null) return (Fail("Tilemap module not available"), null);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (sprite == null) return (Fail($"Sprite not found: {spritePath}"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var tile = ScriptableObject.CreateInstance(tileType);
            if (tile == null) return (Fail("Failed to create Tile"), null);
            var spriteProp = tileType.GetProperty("sprite");
            spriteProp?.SetValue(tile, sprite);
            AssetDatabase.CreateAsset(tile, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createTile", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) SetTile(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("setTile requires target (Tilemap path)"), null);
            var tilePath = (a.tilePath ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(tilePath)) return (Fail("setTile requires tilePath"), null);
            var tilemapGo = FindByPath(a.target);
            if (tilemapGo == null) return (FailTargetNotFound(a.target), null);
            var tilemap = tilemapGo.GetComponent(Type.GetType("UnityEngine.Tilemaps.Tilemap, UnityEngine.TilemapModule"));
            if (tilemap == null) return (Fail("Target has no Tilemap component"), null);
            var tile = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(tilePath);
            if (tile == null) return (Fail($"Tile not found: {tilePath}"), null);
            var pos = new Vector3Int(a.tileX, a.tileY, a.tileZ);
            var tileBaseType = Type.GetType("UnityEngine.Tilemaps.TileBase, UnityEngine.TilemapModule");
            var setTileMethod = tilemap.GetType().GetMethod("SetTile", new[] { typeof(Vector3Int), tileBaseType });
            if (setTileMethod == null) return (Fail("Tilemap.SetTile not found"), null);
            Undo.RecordObject(tilemap, "SetTile");
            setTileMethod.Invoke(tilemap, new object[] { pos, tile });
            return (Ok(GetPath(tilemapGo)), null);
        }

        private static (ActionResult, ActionRevertRecord) SetTiles(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("setTiles requires target"), null);
            var tilesJson = (a.tilesJson ?? a.content ?? "").Trim();
            if (string.IsNullOrEmpty(tilesJson)) return (Fail("setTiles requires tilesJson"), null);
            var tilemapGo = FindByPath(a.target);
            if (tilemapGo == null) return (FailTargetNotFound(a.target), null);
            var tilemap = tilemapGo.GetComponent(Type.GetType("UnityEngine.Tilemaps.Tilemap, UnityEngine.TilemapModule"));
            if (tilemap == null) return (Fail("Target has no Tilemap component"), null);
            var wrapper = JsonUtility.FromJson<TilesBatchWrapper>(tilesJson.StartsWith("[") ? "{\"tiles\":" + tilesJson + "}" : tilesJson);
            if (wrapper?.tiles == null || wrapper.tiles.Length == 0) return (Fail("setTiles: tilesJson must contain tiles array"), null);
            var positions = new List<Vector3Int>();
            var tileBases = new List<UnityEngine.Object>();
            foreach (var t in wrapper.tiles)
            {
                if (string.IsNullOrEmpty(t.tilePath)) continue;
                var tile = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(t.tilePath);
                if (tile != null) { positions.Add(new Vector3Int(t.x, t.y, t.z)); tileBases.Add(tile); }
            }
            if (positions.Count == 0) return (Fail("No valid tiles in tilesJson"), null);
            var tileBaseType = Type.GetType("UnityEngine.Tilemaps.TileBase, UnityEngine.TilemapModule");
            if (tileBaseType == null) return (Fail("Tilemap module not available"), null);
            var tileBaseArray = Array.CreateInstance(tileBaseType, tileBases.Count);
            for (int i = 0; i < tileBases.Count; i++) tileBaseArray.SetValue(tileBases[i], i);
            var setTilesMethod = tilemap.GetType().GetMethod("SetTiles", new[] { typeof(Vector3Int[]), tileBaseType.MakeArrayType() });
            if (setTilesMethod == null) return (Fail("Tilemap.SetTiles not found"), null);
            Undo.RecordObject(tilemap, "SetTiles");
            setTilesMethod.Invoke(tilemap, new object[] { positions.ToArray(), tileBaseArray });
            return (Ok(GetPath(tilemapGo)), null);
        }

        [Serializable]
        private class TileEntry { public int x, y, z; public string tilePath; }
        [Serializable]
        private class TilesBatchWrapper { public TileEntry[] tiles; }

        private static (ActionResult, ActionRevertRecord) CreateTimeline(EditorAction a)
        {
            var p = (a.assetPath ?? a.timelinePath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createTimeline requires assetPath"), null);
            if (!p.EndsWith(".playable", StringComparison.OrdinalIgnoreCase)) p += ".playable";
            var timelineType = Type.GetType("UnityEngine.Timeline.TimelineAsset, Unity.Timeline");
            if (timelineType == null) return (Fail("Timeline package not available. Add com.unity.timeline."), null);
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var timeline = ScriptableObject.CreateInstance(timelineType);
            if (timeline == null) return (Fail("Failed to create TimelineAsset"), null);
            Undo.RegisterCreatedObjectUndo(timeline, "CoBuddy createTimeline");
            AssetDatabase.CreateAsset(timeline, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createTimeline", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddTimelineTrack(EditorAction a)
        {
            var timelinePath = (a.timelinePath ?? a.assetPath ?? "").Trim();
            var trackName = (a.trackName ?? "Track").Trim();
            var trackTypeName = (a.trackType ?? "Animation").Trim();
            if (string.IsNullOrEmpty(timelinePath)) return (Fail("addTimelineTrack requires timelinePath"), null);
            var timeline = AssetDatabase.LoadAssetAtPath<ScriptableObject>(timelinePath);
            if (timeline == null) return (Fail($"Timeline not found: {timelinePath}"), null);
            var timelineAssetType = Type.GetType("UnityEngine.Timeline.TimelineAsset, Unity.Timeline");
            if (timelineAssetType == null || !timelineAssetType.IsInstanceOfType(timeline))
                return (Fail("Timeline package not available"), null);
            Type trackType = null;
            if (trackTypeName.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                trackType = Type.GetType("UnityEngine.Timeline.AnimationTrack, Unity.Timeline");
            else if (trackTypeName.Equals("Activation", StringComparison.OrdinalIgnoreCase))
                trackType = Type.GetType("UnityEngine.Timeline.ActivationTrack, Unity.Timeline");
            else if (trackTypeName.Equals("Audio", StringComparison.OrdinalIgnoreCase))
                trackType = Type.GetType("UnityEngine.Timeline.AudioTrack, Unity.Timeline");
            else
                trackType = Type.GetType("UnityEngine.Timeline.AnimationTrack, Unity.Timeline");
            if (trackType == null) return (Fail("Track type not available"), null);
            var genericCreate = timeline.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateTrack" && m.IsGenericMethod && m.GetParameters().Length == 2);
            if (genericCreate == null) return (Fail("CreateTrack not found"), null);
            var typedCreate = genericCreate.MakeGenericMethod(trackType);
            var track = typedCreate.Invoke(timeline, new object[] { null, trackName });
            if (track == null) return (Fail("CreateTrack failed"), null);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return (Ok(timelinePath), null);
        }

        private static (ActionResult, ActionRevertRecord) AddTimelineClip(EditorAction a)
        {
            var timelinePath = (a.timelinePath ?? a.assetPath ?? "").Trim();
            var trackName = (a.trackName ?? "").Trim();
            var clipPath = (a.clipPath ?? "").Trim();
            var start = a.clipStart;
            var duration = a.clipDuration > 0 ? a.clipDuration : 1f;
            if (string.IsNullOrEmpty(timelinePath)) return (Fail("addTimelineClip requires timelinePath"), null);
            var timeline = AssetDatabase.LoadAssetAtPath<ScriptableObject>(timelinePath);
            if (timeline == null) return (Fail($"Timeline not found: {timelinePath}"), null);
            var getOutputTracks = timeline.GetType().GetMethod("GetOutputTracks");
            if (getOutputTracks == null) return (Fail("GetOutputTracks not found"), null);
            var tracks = getOutputTracks.Invoke(timeline, null) as System.Collections.IEnumerable;
            object targetTrack = null;
            if (tracks != null && !string.IsNullOrEmpty(trackName))
            {
                foreach (var t in tracks)
                {
                    if (t != null && (t.GetType().GetProperty("name")?.GetValue(t) as string ?? "").Equals(trackName, StringComparison.OrdinalIgnoreCase))
                    { targetTrack = t; break; }
                }
            }
            if (targetTrack == null && tracks != null) { var en = tracks.GetEnumerator(); if (en.MoveNext()) targetTrack = en.Current; }
            if (targetTrack == null) return (Fail("No track found"), null);
            object clip = null;
            if (!string.IsNullOrEmpty(clipPath))
            {
                var animClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (animClip != null)
                {
                    var createClipMethod = targetTrack.GetType().GetMethod("CreateClip", new[] { typeof(AnimationClip) });
                    if (createClipMethod != null) clip = createClipMethod.Invoke(targetTrack, new object[] { animClip });
                }
            }
            if (clip == null)
            {
                var createClipMethod = targetTrack.GetType().GetMethod("CreateClip", Type.EmptyTypes);
                if (createClipMethod != null) clip = createClipMethod.Invoke(targetTrack, null);
            }
            if (clip == null) return (Fail("CreateClip failed"), null);
            var clipStartProp = clip.GetType().GetProperty("start") ?? clip.GetType().GetProperty("Start");
            var clipDurationProp = clip.GetType().GetProperty("duration") ?? clip.GetType().GetProperty("Duration");
            if (clipStartProp != null) clipStartProp.SetValue(clip, (double)start);
            if (clipDurationProp != null) clipDurationProp.SetValue(clip, (double)duration);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return (Ok(timelinePath), null);
        }

        private static (ActionResult, ActionRevertRecord) AddPlayableDirector(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var timelinePath = (a.timelinePath ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("addPlayableDirector requires target"), null);
            if (string.IsNullOrEmpty(timelinePath)) return (Fail("addPlayableDirector requires timelinePath"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            var directorType = Type.GetType("UnityEngine.Playables.PlayableDirector, UnityEngine.CoreModule");
            if (directorType == null) return (Fail("PlayableDirector not found"), null);
            var timeline = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(timelinePath);
            if (timeline == null) return (Fail($"Timeline not found: {timelinePath}"), null);
            var existing = go.GetComponent(directorType);
            if (existing == null) existing = Undo.AddComponent(go, directorType);
            var playableAssetProp = directorType.GetProperty("playableAsset");
            if (playableAssetProp != null) { Undo.RecordObject(existing, "Set playableAsset"); playableAssetProp.SetValue(existing, timeline); }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addPlayableDirector", targetPath = GetPath(go), componentType = "PlayableDirector" });
        }

        private static (ActionResult, ActionRevertRecord) CreateAudioClip(EditorAction a)
        {
            var p = (a.assetPath ?? a.audioClipPath ?? "").Trim();
            var wavBase64 = (a.wavBase64 ?? a.content ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createAudioClip requires assetPath"), null);
            if (string.IsNullOrEmpty(wavBase64)) return (Fail("createAudioClip requires wavBase64 or content"), null);
            if (!p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) p += ".wav";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(wavBase64.Contains(",") ? wavBase64.Split(',')[1] : wavBase64); }
            catch { return (Fail("createAudioClip: invalid base64"), null); }
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(fullPath, bytes);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createAudioClip", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddAudioSource(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var clipPath = (a.audioClipPath ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("addAudioSource requires target"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            var comp = go.GetComponent<AudioSource>();
            if (comp == null) comp = Undo.AddComponent<AudioSource>(go);
            if (!string.IsNullOrEmpty(clipPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip != null) { Undo.RecordObject(comp, "Set clip"); comp.clip = clip; }
            }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addAudioSource", targetPath = GetPath(go), componentType = "AudioSource" });
        }

        private static (ActionResult, ActionRevertRecord) CreateAudioMixer(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createAudioMixer requires assetPath"), null);
            if (!p.EndsWith(".mixer", StringComparison.OrdinalIgnoreCase)) p += ".mixer";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            if (!EditorApplication.ExecuteMenuItem("Assets/Create/Audio Mixer"))
                return (Fail("Audio Mixer creation failed. Use Assets > Create > Audio Mixer manually."), null);
            var newMixers = AssetDatabase.FindAssets("t:AudioMixer");
            string createdPath = null;
            foreach (var g in newMixers)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (path.StartsWith(dir ?? "Assets", StringComparison.OrdinalIgnoreCase)) { createdPath = path; break; }
            }
            if (string.IsNullOrEmpty(createdPath) && newMixers.Length > 0)
                createdPath = AssetDatabase.GUIDToAssetPath(newMixers[newMixers.Length - 1]);
            if (!string.IsNullOrEmpty(createdPath) && createdPath != uniquePath)
            {
                var err = AssetDatabase.MoveAsset(createdPath, uniquePath);
                if (string.IsNullOrEmpty(err)) createdPath = uniquePath;
            }
            return (Ok(createdPath ?? uniquePath), new ActionRevertRecord { action = "createAudioMixer", assetPath = createdPath ?? uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateLight(EditorAction a)
        {
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "Light").Trim();
            var lightTypeStr = (a.lightType ?? "Directional").Trim();
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            LightType lightType;
            if (lightTypeStr.Equals("Point", StringComparison.OrdinalIgnoreCase)) lightType = LightType.Point;
            else if (lightTypeStr.Equals("Spot", StringComparison.OrdinalIgnoreCase)) lightType = LightType.Spot;
            else if (lightTypeStr.Equals("Area", StringComparison.OrdinalIgnoreCase)) lightType = LightType.Rectangle;
            else lightType = LightType.Directional;
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createLight");
            if (parent != null) go.transform.SetParent(parent, false);
            var light = Undo.AddComponent<Light>(go);
            light.type = lightType;
            if (a.intensity > 0) light.intensity = a.intensity;
            if (a.range > 0) light.range = a.range;
            if (a.spotAngle > 0) light.spotAngle = a.spotAngle;
            if (a.color != null && a.color.Length >= 3) light.color = new Color(a.color[0], a.color[1], a.color[2], a.color.Length > 3 ? a.color[3] : 1f);
            else if (a.colorR >= 0 || a.colorG >= 0 || a.colorB >= 0) light.color = new Color(a.colorR >= 0 ? a.colorR : 1f, a.colorG >= 0 ? a.colorG : 1f, a.colorB >= 0 ? a.colorB : 1f, a.colorA >= 0 ? a.colorA : 1f);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "createLight", path = GetPath(go) });
        }

        private static (ActionResult, ActionRevertRecord) UpdateLight(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("updateLight requires target"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            var light = go.GetComponent<Light>();
            if (light == null) return (Fail("Target has no Light component"), null);
            Undo.RecordObject(light, "Update Light");
            if (a.intensity > 0) light.intensity = a.intensity;
            if (a.range > 0) light.range = a.range;
            if (a.spotAngle > 0) light.spotAngle = a.spotAngle;
            if (a.color != null && a.color.Length >= 3) light.color = new Color(a.color[0], a.color[1], a.color[2], a.color.Length > 3 ? a.color[3] : 1f);
            else if (a.colorR >= 0 || a.colorG >= 0 || a.colorB >= 0) light.color = new Color(a.colorR >= 0 ? a.colorR : 1f, a.colorG >= 0 ? a.colorG : 1f, a.colorB >= 0 ? a.colorB : 1f, a.colorA >= 0 ? a.colorA : 1f);
            return (Ok(GetPath(go)), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateRenderTexture(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var w = a.sizeX > 0 ? (int)a.sizeX : 256;
            var h = a.sizeY > 0 ? (int)a.sizeY : 256;
            if (string.IsNullOrEmpty(p)) return (Fail("createRenderTexture requires assetPath"), null);
            if (!p.EndsWith(".renderTexture", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".rt", StringComparison.OrdinalIgnoreCase)) p += ".renderTexture";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var rt = new RenderTexture(w, h, 24);
            rt.Create();
            AssetDatabase.CreateAsset(rt, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createRenderTexture", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddParticleSystem(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "Particle System").Trim();
            Transform parent = null;
            if (!string.IsNullOrEmpty(targetPath))
            {
                var go = FindByPath(targetPath);
                if (go == null) return (FailTargetNotFound(targetPath), null);
                var existing = go.GetComponent<ParticleSystem>();
                if (existing != null) return (Ok(GetPath(go)), null);
                var comp = Undo.AddComponent<ParticleSystem>(go);
                return (Ok(GetPath(go)), new ActionRevertRecord { action = "addParticleSystem", targetPath = GetPath(go), componentType = "ParticleSystem" });
            }
            parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            var newGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(newGo, "CoBuddy addParticleSystem");
            if (parent != null) newGo.transform.SetParent(parent, false);
            Undo.AddComponent<ParticleSystem>(newGo);
            return (Ok(GetPath(newGo)), new ActionRevertRecord { action = "addParticleSystem", path = GetPath(newGo) });
        }

        private static (ActionResult, ActionRevertRecord) UpdateParticleSystem(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("updateParticleSystem requires target"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return (Fail("Target has no ParticleSystem"), null);
            var main = ps.main;
            Undo.RecordObject(ps, "Update ParticleSystem");
            if (a.duration > 0) main.duration = a.duration;
            if (a.startLifetime >= 0) main.startLifetime = a.startLifetime;
            if (a.startSpeed >= 0) main.startSpeed = a.startSpeed;
            if (a.startSize >= 0) main.startSize = a.startSize;
            return (Ok(GetPath(go)), null);
        }

        private static (ActionResult, ActionRevertRecord) AddCamera(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "Camera").Trim();
            if (!string.IsNullOrEmpty(targetPath))
            {
                var go = FindByPath(targetPath);
                if (go == null) return (FailTargetNotFound(targetPath), null);
                if (go.GetComponent<Camera>() != null) return (Ok(GetPath(go)), null);
                Undo.AddComponent<Camera>(go);
                return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCamera", targetPath = GetPath(go), componentType = "Camera" });
            }
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            var newGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(newGo, "CoBuddy addCamera");
            if (parent != null) newGo.transform.SetParent(parent, false);
            Undo.AddComponent<Camera>(newGo);
            return (Ok(GetPath(newGo)), new ActionRevertRecord { action = "addCamera", path = GetPath(newGo) });
        }

        private static (ActionResult, ActionRevertRecord) AddCinemachineVirtualCamera(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "CM vcam").Trim();
            var vcamType = Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine");
            if (vcamType == null) return (Fail("Cinemachine package not available. Add com.unity.cinemachine."), null);
            if (!string.IsNullOrEmpty(targetPath))
            {
                var go = FindByPath(targetPath);
                if (go == null) return (FailTargetNotFound(targetPath), null);
                if (go.GetComponent(vcamType) != null) return (Ok(GetPath(go)), null);
                Undo.AddComponent(go, vcamType);
                return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCinemachineVirtualCamera", targetPath = GetPath(go), componentType = "CinemachineVirtualCamera" });
            }
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            var newGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(newGo, "CoBuddy addCinemachineVirtualCamera");
            if (parent != null) newGo.transform.SetParent(parent, false);
            Undo.AddComponent(newGo, vcamType);
            return (Ok(GetPath(newGo)), new ActionRevertRecord { action = "addCinemachineVirtualCamera", path = GetPath(newGo) });
        }

        private static (ActionResult, ActionRevertRecord) SetBuildScenes(EditorAction a)
        {
            var paths = a.scenePaths;
            if (paths == null || paths.Length == 0) return (Fail("setBuildScenes requires scenePaths array"), null);
            var scenes = paths.Select((p, i) => new EditorBuildSettingsScene(p?.Trim() ?? "", a.enabled)).ToArray();
            Undo.RecordObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets"), "Set Build Scenes");
            EditorBuildSettings.scenes = scenes;
            return (Ok(string.Join(",", paths)), null);
        }

        private static (ActionResult, ActionRevertRecord) SetPlayerSettings(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.companyName) && string.IsNullOrWhiteSpace(a.productName) && string.IsNullOrWhiteSpace(a.bundleIdentifier))
                return (Fail("setPlayerSettings requires at least one of companyName, productName, bundleIdentifier"), null);
            if (!string.IsNullOrWhiteSpace(a.companyName)) PlayerSettings.companyName = a.companyName.Trim();
            if (!string.IsNullOrWhiteSpace(a.productName)) PlayerSettings.productName = a.productName.Trim();
            if (!string.IsNullOrWhiteSpace(a.bundleIdentifier)) PlayerSettings.applicationIdentifier = a.bundleIdentifier.Trim();
            return (Ok(), null);
        }

        private static (ActionResult, ActionRevertRecord) SwitchPlatform(EditorAction a)
        {
            if (CurrentCancelCheck != null && CurrentCancelCheck()) return (Fail("Operation cancelled"), null);
            var targetStr = (a.target ?? "").Trim();
            BuildTarget target;
            if (targetStr.Equals("StandaloneWindows64", StringComparison.OrdinalIgnoreCase) || targetStr.Equals("Win64", StringComparison.OrdinalIgnoreCase))
                target = BuildTarget.StandaloneWindows64;
            else if (targetStr.Equals("Android", StringComparison.OrdinalIgnoreCase)) target = BuildTarget.Android;
            else if (targetStr.Equals("iOS", StringComparison.OrdinalIgnoreCase)) target = BuildTarget.iOS;
            else if (targetStr.Equals("WebGL", StringComparison.OrdinalIgnoreCase)) target = BuildTarget.WebGL;
            else return (Fail($"Unknown platform: {targetStr}. Use StandaloneWindows64, Android, iOS, WebGL."), null);
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                return (Ok(target.ToString()), null);
            return (Fail($"Failed to switch to {target}"), null);
        }

        private static (ActionResult, ActionRevertRecord) ExecuteBuild(EditorAction a)
        {
            if (CurrentCancelCheck != null && CurrentCancelCheck()) return (Fail("Operation cancelled"), null);
            var outputPath = (a.assetPath ?? a.path ?? "").Trim();
            if (string.IsNullOrEmpty(outputPath)) return (Fail("executeBuild requires assetPath (output path)"), null);
            var options = BuildOptions.None;
            if (a.enabled) options |= BuildOptions.Development;
            var report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes, outputPath, EditorUserBuildSettings.activeBuildTarget, options);
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                return (Ok(outputPath), null);
            return (Fail(report.summary.ToString()), null);
        }

        private static (ActionResult, ActionRevertRecord) CreatePrefabVariant(EditorAction a)
        {
            var sourcePath = (a.prefabPath ?? a.path ?? "").Trim();
            var variantPath = (a.variantPath ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(sourcePath)) return (Fail("createPrefabVariant requires prefabPath (source)"), null);
            if (string.IsNullOrEmpty(variantPath)) return (Fail("createPrefabVariant requires variantPath"), null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (prefab == null) return (Fail($"Prefab not found: {sourcePath}"), null);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return (Fail("Failed to instantiate prefab"), null);
            Undo.RegisterCreatedObjectUndo(instance, "CoBuddy createPrefabVariant");
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(variantPath);
            if (!uniquePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) uniquePath += ".prefab";
            var success = PrefabUtility.SaveAsPrefabAsset(instance, uniquePath);
            Undo.DestroyObjectImmediate(instance);
            if (!success) return (Fail($"Failed to save prefab variant to {uniquePath}"), null);
            AssetDatabase.Refresh();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createPrefabVariant", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) UnpackPrefab(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("unpackPrefab requires target"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            if (!PrefabUtility.IsPartOfPrefabInstance(go)) return (Fail("Target is not a prefab instance"), null);
            Undo.RecordObject(go, "Unpack Prefab");
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            return (Ok(GetPath(go)), null);
        }

        private static (ActionResult, ActionRevertRecord) CreatePanelSettings(EditorAction a)
        {
            var p = (a.assetPath ?? a.panelSettingsPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) p = "Assets/UI/CoBuddyPanelSettings.asset";
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(uniquePath);
            if (existing != null) return (Ok(uniquePath), null);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var themeGuids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            if (themeGuids != null && themeGuids.Length > 0)
            {
                var themePath = AssetDatabase.GUIDToAssetPath(themeGuids[0]);
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (theme != null) panel.themeStyleSheet = theme;
            }
            AssetDatabase.CreateAsset(panel, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createPanelSettings", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateThemeStyleSheet(EditorAction a)
        {
            var p = (a.assetPath ?? a.themePath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createThemeStyleSheet requires assetPath"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var theme = ScriptableObject.CreateInstance<ThemeStyleSheet>();
            AssetDatabase.CreateAsset(theme, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createThemeStyleSheet", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddRigidbody2D(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addRigidbody2D requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null) rb = Undo.AddComponent<Rigidbody2D>(go);
            var bt = (a.bodyType ?? "Dynamic").Trim();
            if (bt.Equals("Static", StringComparison.OrdinalIgnoreCase)) rb.bodyType = RigidbodyType2D.Static;
            else if (bt.Equals("Kinematic", StringComparison.OrdinalIgnoreCase)) rb.bodyType = RigidbodyType2D.Kinematic;
            else rb.bodyType = RigidbodyType2D.Dynamic;
            if (a.mass > 0) rb.mass = a.mass;
            if (a.gravityScale >= 0) rb.gravityScale = a.gravityScale;
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addRigidbody2D", targetPath = GetPath(go), componentType = "Rigidbody2D" });
        }

        private static (ActionResult, ActionRevertRecord) AddCollider2D(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addCollider2D requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var t = (a.colliderType ?? "Box").Trim().ToLowerInvariant();
            Collider2D col = null;
            if (t == "box")
            {
                var c = go.GetComponent<BoxCollider2D>();
                if (c == null) c = Undo.AddComponent<BoxCollider2D>(go);
                if (a.sizeX > 0 || a.sizeY > 0) c.size = new Vector2(a.sizeX > 0 ? a.sizeX : 1, a.sizeY > 0 ? a.sizeY : 1);
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else if (t == "circle")
            {
                var c = go.GetComponent<CircleCollider2D>();
                if (c == null) c = Undo.AddComponent<CircleCollider2D>(go);
                if (a.radius > 0) c.radius = a.radius;
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else if (t == "polygon")
            {
                var c = go.GetComponent<PolygonCollider2D>();
                if (c == null) c = Undo.AddComponent<PolygonCollider2D>(go);
                c.isTrigger = a.isTrigger;
                col = c;
            }
            else return (Fail($"Unknown collider2D type: {a.colliderType}. Use Box, Circle, or Polygon."), null);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCollider2D", targetPath = GetPath(go), componentType = col.GetType().Name });
        }

        private static (ActionResult, ActionRevertRecord) AddSpriteRenderer(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addSpriteRenderer requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) sr = Undo.AddComponent<SpriteRenderer>(go);
            if (!string.IsNullOrWhiteSpace(a.spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(a.spritePath.Trim());
                if (sprite != null) { Undo.RecordObject(sr, "Set sprite"); sr.sprite = sprite; }
            }
            if (a.color != null && a.color.Length >= 3) { Undo.RecordObject(sr, "Set color"); sr.color = new Color(a.color[0], a.color[1], a.color[2], a.color.Length > 3 ? a.color[3] : 1f); }
            else if (a.colorR >= 0 || a.colorG >= 0 || a.colorB >= 0) { Undo.RecordObject(sr, "Set color"); sr.color = new Color(a.colorR >= 0 ? a.colorR : 1f, a.colorG >= 0 ? a.colorG : 1f, a.colorB >= 0 ? a.colorB : 1f, a.colorA >= 0 ? a.colorA : 1f); }
            if (a.sortingOrder != 0) { Undo.RecordObject(sr, "Set sorting"); sr.sortingOrder = a.sortingOrder; }
            if (!string.IsNullOrWhiteSpace(a.sortingLayerName)) { Undo.RecordObject(sr, "Set sorting layer"); sr.sortingLayerName = a.sortingLayerName; }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addSpriteRenderer", targetPath = GetPath(go), componentType = "SpriteRenderer" });
        }

        private static (ActionResult, ActionRevertRecord) SetSortingLayer(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("setSortingLayer requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return (Fail("Target has no Renderer component"), null);
            Undo.RecordObject(renderer, "Set sorting layer");
            if (!string.IsNullOrWhiteSpace(a.sortingLayerName)) renderer.sortingLayerName = a.sortingLayerName;
            if (a.sortingOrder != 0) renderer.sortingOrder = a.sortingOrder;
            return (Ok(GetPath(go)), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateSortingLayer(EditorAction a)
        {
            var name = (a.layerName ?? a.name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return (Fail("createSortingLayer requires layerName"), null);
            var tagManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            var so = new SerializedObject(tagManager);
            var layers = so.FindProperty("m_SortingLayers");
            if (layers == null) return (Fail("TagManager m_SortingLayers not found"), null);
            for (int i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == name)
                    return (Ok(name), null);
            layers.InsertArrayElementAtIndex(layers.arraySize);
            var elem = layers.GetArrayElementAtIndex(layers.arraySize - 1);
            elem.FindPropertyRelative("name").stringValue = name;
            elem.FindPropertyRelative("uniqueID").intValue = (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
            so.ApplyModifiedPropertiesWithoutUndo();
            return (Ok(name), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateSpriteAtlas(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createSpriteAtlas requires assetPath"), null);
            if (!p.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase)) p += ".spriteatlas";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var atlas = new UnityEngine.U2D.SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createSpriteAtlas", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateCanvas(EditorAction a)
        {
            var parentPath = (a.parent ?? "").Trim();
            var name = (a.name ?? "Canvas").Trim();
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createCanvas");
            if (parent != null) go.transform.SetParent(parent, false);
            var canvas = Undo.AddComponent<Canvas>(go);
            var modeStr = (a.renderMode ?? "ScreenSpaceOverlay").Trim().ToLowerInvariant();
            canvas.renderMode = modeStr.Contains("camera") ? RenderMode.ScreenSpaceCamera : (modeStr.Contains("world") ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay);
            Undo.AddComponent<UnityEngine.UI.CanvasScaler>(go);
            Undo.AddComponent<UnityEngine.UI.GraphicRaycaster>(go);
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>();
                var match = (a.canvasScalerMatch ?? "Both").Trim();
                if (match.Equals("Width", StringComparison.OrdinalIgnoreCase)) scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                else if (match.Equals("Height", StringComparison.OrdinalIgnoreCase)) scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.matchWidthOrHeight = match.Equals("Width", StringComparison.OrdinalIgnoreCase) ? 0f : (match.Equals("Height", StringComparison.OrdinalIgnoreCase) ? 1f : 0.5f);
            }
            var eventSystem = UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
            if (eventSystem == null)
            {
                var esGo = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(esGo, "CoBuddy createCanvas");
                Undo.AddComponent<UnityEngine.EventSystems.EventSystem>(esGo);
                Undo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>(esGo);
            }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "createCanvas", path = GetPath(go) });
        }

        private static (ActionResult, ActionRevertRecord) AddEventSystem(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            if (!string.IsNullOrEmpty(targetPath))
            {
                var go = FindByPath(targetPath);
                if (go == null) return (FailTargetNotFound(targetPath), null);
                if (go.GetComponent<UnityEngine.EventSystems.EventSystem>() != null) return (Ok(GetPath(go)), null);
                Undo.AddComponent<UnityEngine.EventSystems.EventSystem>(go);
                if (go.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>() == null)
                    Undo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>(go);
                return (Ok(GetPath(go)), new ActionRevertRecord { action = "addEventSystem", targetPath = GetPath(go), componentType = "EventSystem" });
            }
            var existing = UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
            if (existing != null) return (Ok(GetPath(existing.gameObject)), null);
            var esGo = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(esGo, "CoBuddy addEventSystem");
            Undo.AddComponent<UnityEngine.EventSystems.EventSystem>(esGo);
            Undo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>(esGo);
            return (Ok(GetPath(esGo)), new ActionRevertRecord { action = "addEventSystem", path = GetPath(esGo) });
        }

        private static (ActionResult, ActionRevertRecord) AddLayoutGroup(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addLayoutGroup requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return (Fail("Target must have RectTransform"), null);
            var lt = (a.layoutType ?? "Vertical").Trim().ToLowerInvariant();
            UnityEngine.UI.LayoutGroup lg = null;
            if (lt == "horizontal")
            {
                var c = go.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (c == null) c = Undo.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>(go);
                if (a.spacing >= 0) c.spacing = a.spacing;
                lg = c;
            }
            else if (lt == "grid")
            {
                var c = go.GetComponent<UnityEngine.UI.GridLayoutGroup>();
                if (c == null) c = Undo.AddComponent<UnityEngine.UI.GridLayoutGroup>(go);
                if (a.spacing >= 0) c.spacing = new Vector2(a.spacing, a.spacing);
                lg = c;
            }
            else
            {
                var c = go.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                if (c == null) c = Undo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>(go);
                if (a.spacing >= 0) c.spacing = a.spacing;
                lg = c;
            }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addLayoutGroup", targetPath = GetPath(go), componentType = lg.GetType().Name });
        }

        private static (ActionResult, ActionRevertRecord) AddScrollRect(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addScrollRect requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return (Fail("Target must have RectTransform"), null);
            var sr = go.GetComponent<UnityEngine.UI.ScrollRect>();
            if (sr == null) sr = Undo.AddComponent<UnityEngine.UI.ScrollRect>(go);
            RectTransform viewport = null, content = null;
            if (!string.IsNullOrWhiteSpace(a.viewportPath)) viewport = FindByPath(a.viewportPath)?.GetComponent<RectTransform>();
            if (!string.IsNullOrWhiteSpace(a.contentPath)) content = FindByPath(a.contentPath)?.GetComponent<RectTransform>();
            if (viewport != null) { Undo.RecordObject(sr, "Set viewport"); sr.viewport = viewport; }
            if (content != null) { Undo.RecordObject(sr, "Set content"); sr.content = content; }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addScrollRect", targetPath = GetPath(go), componentType = "ScrollRect" });
        }

        private static (ActionResult, ActionRevertRecord) CreateLayer(EditorAction a)
        {
            var name = (a.layerName ?? a.name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return (Fail("createLayer requires layerName"), null);
            var tagManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            var so = new SerializedObject(tagManager);
            var layers = so.FindProperty("layers");
            if (layers == null) return (Fail("TagManager layers not found"), null);
            for (int i = 8; i < 32; i++)
            {
                var elem = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(elem.stringValue))
                {
                    elem.stringValue = name;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return (Ok(name), null);
                }
                if (elem.stringValue == name) return (Ok(name), null);
            }
            return (Fail("No empty layer slot (8-31) available"), null);
        }

        private static (ActionResult, ActionRevertRecord) AddLineRenderer(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addLineRenderer requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var lr = go.GetComponent<LineRenderer>();
            if (lr == null) lr = Undo.AddComponent<LineRenderer>(go);
            if (a.positionCount > 0) lr.positionCount = a.positionCount;
            if (a.width > 0) { lr.startWidth = a.width; lr.endWidth = a.width * 0.5f; }
            if (a.color != null && a.color.Length >= 3) lr.startColor = lr.endColor = new Color(a.color[0], a.color[1], a.color[2], a.color.Length > 3 ? a.color[3] : 1f);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addLineRenderer", targetPath = GetPath(go), componentType = "LineRenderer" });
        }

        private static (ActionResult, ActionRevertRecord) AddTrailRenderer(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addTrailRenderer requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var tr = go.GetComponent<TrailRenderer>();
            if (tr == null) tr = Undo.AddComponent<TrailRenderer>(go);
            if (a.trailTime > 0) tr.time = a.trailTime;
            if (a.width > 0) { tr.startWidth = a.width; tr.endWidth = a.width * 0.5f; }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addTrailRenderer", targetPath = GetPath(go), componentType = "TrailRenderer" });
        }

        private static (ActionResult, ActionRevertRecord) AddReflectionProbe(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addReflectionProbe requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var rp = go.GetComponent<ReflectionProbe>();
            if (rp == null) rp = Undo.AddComponent<ReflectionProbe>(go);
            var pt = (a.probeType ?? "Baked").Trim();
            if (pt.Equals("Realtime", StringComparison.OrdinalIgnoreCase)) rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            else if (pt.Equals("Both", StringComparison.OrdinalIgnoreCase)) rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            else rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Baked;
            if (a.sizeX > 0 || a.sizeY > 0 || a.sizeZ > 0) rp.size = new Vector3(a.sizeX > 0 ? a.sizeX : 10, a.sizeY > 0 ? a.sizeY : 10, a.sizeZ > 0 ? a.sizeZ : 10);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addReflectionProbe", targetPath = GetPath(go), componentType = "ReflectionProbe" });
        }

        private static (ActionResult, ActionRevertRecord) AddLightProbeGroup(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addLightProbeGroup requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var lpg = go.GetComponent<LightProbeGroup>();
            if (lpg == null) lpg = Undo.AddComponent<LightProbeGroup>(go);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addLightProbeGroup", targetPath = GetPath(go), componentType = "LightProbeGroup" });
        }

        private static (ActionResult, ActionRevertRecord) BakeNavMesh(EditorAction a)
        {
            if (CurrentCancelCheck != null && CurrentCancelCheck()) return (Fail("Operation cancelled"), null);
            var builderType = Type.GetType("UnityEditor.AI.NavMeshBuilder, Unity.AI.Navigation.Editor");
            if (builderType == null) builderType = Type.GetType("UnityEditor.AI.NavMeshBuilder, UnityEngine.AIModule");
            if (builderType == null) return (Fail("NavMesh baking not available. Add AI Navigation package."), null);
            var buildMethod = builderType.GetMethod("BuildNavMesh", Type.EmptyTypes);
            if (buildMethod == null) buildMethod = builderType.GetMethod("BuildNavMesh", new[] { typeof(UnityEngine.AI.NavMeshData) });
            if (buildMethod != null)
            {
                try { buildMethod.Invoke(null, buildMethod.GetParameters().Length == 0 ? Array.Empty<object>() : new object[] { null }); }
                catch (Exception ex) { return (Fail(ex.InnerException?.Message ?? ex.Message), null); }
                return (Ok(), null);
            }
            return (Fail("NavMeshBuilder.BuildNavMesh not found"), null);
        }

        private static (ActionResult, ActionRevertRecord) AddNavMeshAgent(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addNavMeshAgent requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var nma = go.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (nma == null) nma = Undo.AddComponent<UnityEngine.AI.NavMeshAgent>(go);
            if (a.agentRadius >= 0) nma.radius = a.agentRadius;
            if (a.agentHeight >= 0) nma.height = a.agentHeight;
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addNavMeshAgent", targetPath = GetPath(go), componentType = "NavMeshAgent" });
        }

        private static (ActionResult, ActionRevertRecord) AddNavMeshObstacle(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addNavMeshObstacle requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var nmo = go.GetComponent<UnityEngine.AI.NavMeshObstacle>();
            if (nmo == null) nmo = Undo.AddComponent<UnityEngine.AI.NavMeshObstacle>(go);
            var shape = (a.obstacleShape ?? "Box").Trim();
            nmo.shape = shape.Equals("Capsule", StringComparison.OrdinalIgnoreCase) ? UnityEngine.AI.NavMeshObstacleShape.Capsule : UnityEngine.AI.NavMeshObstacleShape.Box;
            if (a.sizeX > 0 || a.sizeY > 0 || a.sizeZ > 0) nmo.size = new Vector3(a.sizeX > 0 ? a.sizeX : 1, a.sizeY > 0 ? a.sizeY : 1, a.sizeZ > 0 ? a.sizeZ : 1);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addNavMeshObstacle", targetPath = GetPath(go), componentType = "NavMeshObstacle" });
        }

        private static (ActionResult, ActionRevertRecord) CreateAnimatorOverrideController(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var basePath = (a.controllerPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createAnimatorOverrideController requires assetPath"), null);
            if (string.IsNullOrEmpty(basePath)) return (Fail("createAnimatorOverrideController requires controllerPath (base AnimatorController)"), null);
            var baseController = AssetDatabase.LoadAssetAtPath<AnimatorController>(basePath);
            if (baseController == null) return (Fail($"AnimatorController not found: {basePath}"), null);
            if (!p.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase)) p += ".overrideController";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var over = new AnimatorOverrideController(baseController);
            AssetDatabase.CreateAsset(over, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createAnimatorOverrideController", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateBlendTree(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createBlendTree requires assetPath"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var bt = new BlendTree();
            if (bt == null) return (Fail("Failed to create BlendTree"), null);
            AssetDatabase.CreateAsset(bt, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createBlendTree", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddAvatarMask(EditorAction a)
        {
            var targetPath = (a.target ?? "").Trim();
            var maskPath = (a.maskPath ?? "").Trim();
            if (string.IsNullOrEmpty(targetPath)) return (Fail("addAvatarMask requires target"), null);
            var go = FindByPath(targetPath);
            if (go == null) return (FailTargetNotFound(targetPath), null);
            var animator = go.GetComponent<Animator>();
            if (animator == null) return (Fail("Target has no Animator. addAvatarMask applies to Animator layers."), null);
            AvatarMask mask = null;
            if (!string.IsNullOrEmpty(maskPath))
            {
                mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath.Trim());
                if (mask == null) return (Fail($"AvatarMask not found: {maskPath}"), null);
            }
            else
            {
                var newMaskPath = (a.assetPath ?? "Assets/AvatarMask.asset").Trim();
                if (!newMaskPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) newMaskPath += ".asset";
                var uniquePath = AssetDatabase.GenerateUniqueAssetPath(newMaskPath);
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, uniquePath);
                AssetDatabase.SaveAssets();
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller != null)
            {
                Undo.RecordObject(controller, "Set AvatarMask");
                var layers = controller.layers;
                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    layer.avatarMask = mask;
                    layers[i] = layer;
                }
                controller.layers = layers;
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
            }
            return (Ok(GetPath(go)), string.IsNullOrEmpty(maskPath) ? new ActionRevertRecord { action = "addAvatarMask", assetPath = AssetDatabase.GetAssetPath(mask) } : null);
        }

        private static (ActionResult, ActionRevertRecord) AddJoint(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addJoint requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var jt = (a.jointType ?? "Fixed").Trim().ToLowerInvariant();
            Component joint = null;
            if (jt == "hinge")
            {
                var j = go.GetComponent<HingeJoint>();
                if (j == null) j = Undo.AddComponent<HingeJoint>(go);
                if (!string.IsNullOrWhiteSpace(a.connectedBodyPath)) { var cb = FindByPath(a.connectedBodyPath)?.GetComponent<Rigidbody>(); if (cb != null) j.connectedBody = cb; }
                joint = j;
            }
            else if (jt == "spring")
            {
                var j = go.GetComponent<SpringJoint>();
                if (j == null) j = Undo.AddComponent<SpringJoint>(go);
                if (!string.IsNullOrWhiteSpace(a.connectedBodyPath)) { var cb = FindByPath(a.connectedBodyPath)?.GetComponent<Rigidbody>(); if (cb != null) j.connectedBody = cb; }
                joint = j;
            }
            else if (jt == "configurable")
            {
                var j = go.GetComponent<ConfigurableJoint>();
                if (j == null) j = Undo.AddComponent<ConfigurableJoint>(go);
                if (!string.IsNullOrWhiteSpace(a.connectedBodyPath)) { var cb = FindByPath(a.connectedBodyPath)?.GetComponent<Rigidbody>(); if (cb != null) j.connectedBody = cb; }
                joint = j;
            }
            else
            {
                var j = go.GetComponent<FixedJoint>();
                if (j == null) j = Undo.AddComponent<FixedJoint>(go);
                if (!string.IsNullOrWhiteSpace(a.connectedBodyPath)) { var cb = FindByPath(a.connectedBodyPath)?.GetComponent<Rigidbody>(); if (cb != null) j.connectedBody = cb; }
                joint = j;
            }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addJoint", targetPath = GetPath(go), componentType = joint.GetType().Name });
        }

        private static (ActionResult, ActionRevertRecord) AddCharacterController(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addCharacterController requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var cc = go.GetComponent<CharacterController>();
            if (cc == null) cc = Undo.AddComponent<CharacterController>(go);
            if (a.height > 0) cc.height = a.height;
            if (a.radius > 0) cc.radius = a.radius;
            if (a.center != null && a.center.Length >= 3) cc.center = new Vector3(a.center[0], a.center[1], a.center[2]);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCharacterController", targetPath = GetPath(go), componentType = "CharacterController" });
        }

        private static (ActionResult, ActionRevertRecord) AddCloth(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addCloth requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var clothType = Type.GetType("UnityEngine.Cloth, UnityEngine.PhysicsModule");
            if (clothType == null) return (Fail("Cloth component not available (deprecated in Unity 6)"), null);
            var existing = go.GetComponent(clothType);
            if (existing != null) return (Ok(GetPath(go)), null);
            Undo.AddComponent(go, clothType);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addCloth", targetPath = GetPath(go), componentType = "Cloth" });
        }

        private static (ActionResult, ActionRevertRecord) CreateTerrain(EditorAction a)
        {
            var assetPath = (a.assetPath ?? "Assets/TerrainData.asset").Trim();
            var parentPath = (a.parent ?? "").Trim();
            var dataPath = (a.terrainDataPath ?? assetPath).Trim();
            TerrainData td = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                td = AssetDatabase.LoadAssetAtPath<TerrainData>(dataPath);
                if (td == null)
                {
                    var dir = Path.GetDirectoryName(dataPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                        AssetDatabase.Refresh();
                    }
                    td = new TerrainData();
                    td.size = new Vector3(1000, 600, 1000);
                    var uniquePath = AssetDatabase.GenerateUniqueAssetPath(dataPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ? dataPath : dataPath + ".asset");
                    AssetDatabase.CreateAsset(td, uniquePath);
                }
            }
            if (td == null) td = new TerrainData();
            var go = Terrain.CreateTerrainGameObject(td);
            if (go == null) return (Fail("Failed to create Terrain"), null);
            Undo.RegisterCreatedObjectUndo(go, "CoBuddy createTerrain");
            var parent = GetParentTransform(string.IsNullOrEmpty(parentPath) ? null : parentPath);
            if (parent != null) go.transform.SetParent(parent, false);
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "createTerrain", path = GetPath(go) });
        }

        private static (ActionResult, ActionRevertRecord) AddTerrainLayer(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("addTerrainLayer requires assetPath"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "addTerrainLayer", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddVideoPlayer(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addVideoPlayer requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var vp = go.GetComponent<UnityEngine.Video.VideoPlayer>();
            if (vp == null) vp = Undo.AddComponent<UnityEngine.Video.VideoPlayer>(go);
            if (!string.IsNullOrWhiteSpace(a.videoClipPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.Video.VideoClip>(a.videoClipPath.Trim());
                if (clip != null) { Undo.RecordObject(vp, "Set clip"); vp.clip = clip; }
            }
            vp.playOnAwake = a.enabled;
            if (a.loop) vp.isLooping = true;
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addVideoPlayer", targetPath = GetPath(go), componentType = "VideoPlayer" });
        }

        private static (ActionResult, ActionRevertRecord) AddTextMeshPro(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target)) return (Fail("addTextMeshPro requires target"), null);
            var go = FindByPath(a.target);
            if (go == null) return (FailTargetNotFound(a.target), null);
            var tmType = (a.textMeshType ?? "UGUI").Trim().ToLowerInvariant();
            var tmpType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            if (tmType.Contains("3d") || tmType.Contains("world")) tmpType = Type.GetType("TMPro.TextMeshPro, Unity.TextMeshPro");
            if (tmpType == null) return (Fail("TextMeshPro package not available. Add com.unity.textmeshpro."), null);
            var existing = go.GetComponent(tmpType);
            if (existing == null) Undo.AddComponent(go, tmpType);
            var comp = go.GetComponent(tmpType);
            if (comp != null && !string.IsNullOrWhiteSpace(a.text)) { Undo.RecordObject(comp, "Set text"); comp.GetType().GetProperty("text")?.SetValue(comp, a.text); }
            if (comp != null && a.fontSize > 0) { Undo.RecordObject(comp, "Set fontSize"); comp.GetType().GetProperty("fontSize")?.SetValue(comp, a.fontSize); }
            return (Ok(GetPath(go)), new ActionRevertRecord { action = "addTextMeshPro", targetPath = GetPath(go), componentType = "TextMeshProUGUI" });
        }

        private static (ActionResult, ActionRevertRecord) CreateTMPFontAsset(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var sourcePath = (a.sourceFontPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createTMPFontAsset requires assetPath"), null);
            if (string.IsNullOrEmpty(sourcePath)) return (Fail("createTMPFontAsset requires sourceFontPath (TTF)"), null);
            var tmpType = Type.GetType("TMPro.TMP_FontAsset, Unity.TextMeshPro");
            if (tmpType == null) return (Fail("TextMeshPro package not available"), null);
            var createMethod = tmpType.GetMethod("CreateFontAsset", new[] { typeof(UnityEngine.Font) });
            if (createMethod == null) return (Fail("TMP_FontAsset.CreateFontAsset not found"), null);
            var font = AssetDatabase.LoadAssetAtPath<UnityEngine.Font>(sourcePath);
            if (font == null) return (Fail($"Font not found: {sourcePath}"), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var fontAsset = createMethod.Invoke(null, new object[] { font });
            if (fontAsset == null) return (Fail("CreateFontAsset failed"), null);
            AssetDatabase.CreateAsset((UnityEngine.Object)fontAsset, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createTMPFontAsset", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) AddPackage(EditorAction a)
        {
            var id = (a.packageId ?? a.packageName ?? "").Trim();
            if (string.IsNullOrEmpty(id)) return (Fail("addPackage requires packageId"), null);
            var clientType = Type.GetType("UnityEditor.PackageManager.Client, Unity.PackageManager.UI");
            if (clientType == null) return (Fail("Package Manager API not available"), null);
            var addMethod = clientType.GetMethod("Add", new[] { typeof(string) });
            if (addMethod == null) addMethod = clientType.GetMethod("Add", new[] { typeof(string), typeof(string) });
            try
            {
                var req = addMethod.GetParameters().Length == 1 ? addMethod.Invoke(null, new object[] { id }) : addMethod.Invoke(null, new object[] { id, a.packageVersion ?? "" });
                if (req != null && req.GetType().GetProperty("Status")?.GetValue(req)?.ToString() == "Error")
                    return (Fail(req.GetType().GetProperty("Error")?.GetValue(req)?.ToString() ?? "Add failed"), null);
            }
            catch (Exception ex) { return (Fail(ex.InnerException?.Message ?? ex.Message), null); }
            return (Ok(id), null);
        }

        private static (ActionResult, ActionRevertRecord) RemovePackage(EditorAction a)
        {
            var name = (a.packageName ?? a.packageId ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return (Fail("removePackage requires packageName"), null);
            var manifestPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return (Fail("Packages/manifest.json not found"), null);
            var manifest = File.ReadAllText(manifestPath);
            var pattern = new System.Text.RegularExpressions.Regex($@"\s*""{System.Text.RegularExpressions.Regex.Escape(name)}""\s*:\s*""[^""]*""\s*,?\s*");
            var newManifest = pattern.Replace(manifest, "");
            if (newManifest == manifest) return (Ok(name), null);
            File.WriteAllText(manifestPath, newManifest);
            AssetDatabase.Refresh();
            return (Ok(name), null);
        }

        private static (ActionResult, ActionRevertRecord) LoadSceneAdditive(EditorAction a)
        {
            var p = (a.path ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("loadSceneAdditive requires path"), null);
            var scene = EditorSceneManager.OpenScene(p, OpenSceneMode.Additive);
            return (Ok(scene.path), new ActionRevertRecord { action = "loadSceneAdditive", scenePath = scene.path });
        }

        private static (ActionResult, ActionRevertRecord) UnloadScene(EditorAction a)
        {
            var p = (a.path ?? a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("unloadScene requires path"), null);
            var scene = SceneManager.GetSceneByPath(p);
            if (!scene.isLoaded) return (Ok(p), null);
            EditorSceneManager.CloseScene(scene, true);
            return (Ok(p), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateVFXGraph(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createVFXGraph requires assetPath"), null);
            if (!p.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)) p += ".vfx";
            var vfxType = Type.GetType("UnityEngine.VFX.VisualEffectAsset, Unity.VisualEffectGraph");
            if (vfxType == null) return (Fail("VFX Graph package not available. Add com.unity.visualeffectgraph."), null);
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var vfx = ScriptableObject.CreateInstance(vfxType);
            if (vfx == null) return (Fail("Failed to create VFX asset"), null);
            AssetDatabase.CreateAsset(vfx, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createVFXGraph", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateShaderGraph(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createShaderGraph requires assetPath"), null);
            if (!p.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) p += ".shadergraph";
            if (!EditorApplication.ExecuteMenuItem("Assets/Create/Shader Graph/Blank Shader Graph"))
                return (Fail("Shader Graph creation failed. Add com.unity.shadergraph."), null);
            var newShaders = AssetDatabase.FindAssets("t:Shader");
            string createdPath = null;
            foreach (var g in newShaders)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) { createdPath = path; break; }
            }
            if (!string.IsNullOrEmpty(createdPath))
            {
                var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
                var err = AssetDatabase.MoveAsset(createdPath, uniquePath);
                if (string.IsNullOrEmpty(err)) createdPath = uniquePath;
            }
            return (Ok(createdPath ?? p), new ActionRevertRecord { action = "createShaderGraph", assetPath = createdPath ?? p });
        }

        private static (ActionResult, ActionRevertRecord) CreateSpriteAnimation(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            var paths = a.spritePaths;
            if (string.IsNullOrEmpty(p)) return (Fail("createSpriteAnimation requires assetPath"), null);
            if (paths == null || paths.Length == 0) return (Fail("createSpriteAnimation requires spritePaths array"), null);
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var clip = new AnimationClip();
            clip.frameRate = a.frameRate > 0 ? a.frameRate : 12f;
            var objRef = new EditorCurveBinding { path = "", type = typeof(SpriteRenderer), propertyName = "m_Sprite" };
            var keyframes = new ObjectReferenceKeyframe[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(paths[i]?.Trim() ?? "");
                keyframes[i] = new ObjectReferenceKeyframe { time = i / clip.frameRate, value = sprite };
            }
            AnimationUtility.SetObjectReferenceCurve(clip, objRef, keyframes);
            if (!p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) p += ".anim";
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            AssetDatabase.CreateAsset(clip, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createSpriteAnimation", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateRuleTile(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createRuleTile requires assetPath"), null);
            var ruleTileType = Type.GetType("UnityEngine.Tilemaps.RuleTile, Unity.2D.Tilemap.Extras");
            if (ruleTileType == null) return (Fail("2D Tilemap Extras package not available. Add com.unity.2d.tilemap.extras."), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var tile = ScriptableObject.CreateInstance(ruleTileType);
            if (tile == null) return (Fail("Failed to create RuleTile"), null);
            AssetDatabase.CreateAsset(tile, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createRuleTile", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) MarkAddressable(EditorAction a)
        {
            var p = (a.assetPath ?? a.path ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("markAddressable requires assetPath"), null);
            var settingsType = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings, Unity.Addressables.Editor");
            if (settingsType == null) return (Fail("Addressables package not available. Add com.unity.addressables."), null);
            var defaultObj = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject, Unity.Addressables.Editor");
            if (defaultObj == null) return (Fail("AddressableAssetSettingsDefaultObject not found"), null);
            var settingsProp = defaultObj.GetProperty("Settings");
            var settings = settingsProp?.GetValue(null);
            if (settings == null) return (Fail("AddressableAssetSettings not initialized"), null);
            var createOrMove = settings.GetType().GetMethod("CreateOrMoveEntry", new[] { typeof(string), typeof(string) });
            if (createOrMove == null) createOrMove = settings.GetType().GetMethod("CreateOrMoveEntry", new[] { typeof(string), typeof(string), typeof(Type) });
            var address = (a.address ?? p).Trim();
            var groupName = (a.groupName ?? "").Trim();
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(p);
                if (string.IsNullOrEmpty(guid)) return (Fail($"Asset not found: {p}"), null);
                var entry = createOrMove.GetParameters().Length == 2
                    ? createOrMove.Invoke(settings, new object[] { guid, address })
                    : createOrMove.Invoke(settings, new object[] { guid, address, null });
                if (!string.IsNullOrEmpty(groupName) && entry != null)
                {
                    var groups = settings.GetType().GetMethod("GetGroups")?.Invoke(settings, null) as System.Collections.IEnumerable;
                    foreach (var g in groups ?? Array.Empty<object>())
                    {
                        if (g?.GetType().GetProperty("Name")?.GetValue(g)?.ToString() == groupName)
                        {
                            entry.GetType().GetProperty("parentGroup")?.SetValue(entry, g);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { return (Fail(ex.InnerException?.Message ?? ex.Message), null); }
            return (Ok(p), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateAddressablesGroup(EditorAction a)
        {
            var name = (a.groupName ?? a.name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return (Fail("createAddressablesGroup requires groupName"), null);
            var settingsType = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings, Unity.Addressables.Editor");
            if (settingsType == null) return (Fail("Addressables package not available."), null);
            var defaultObj = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject, Unity.Addressables.Editor");
            var settings = defaultObj?.GetProperty("Settings")?.GetValue(null);
            if (settings == null) return (Fail("AddressableAssetSettings not initialized"), null);
            var createGroup = settings.GetType().GetMethod("CreateGroup", new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(System.Collections.IEnumerable), typeof(Type[]) });
            var schemaType = Type.GetType("UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema, Unity.Addressables.Editor");
            try
            {
                var group = createGroup?.Invoke(settings, new object[] { name, false, false, true, null, schemaType != null ? new Type[] { schemaType } : Array.Empty<Type>() });
                if (group == null) return (Fail("CreateGroup failed"), null);
            }
            catch (Exception ex) { return (Fail(ex.InnerException?.Message ?? ex.Message), null); }
            return (Ok(name), null);
        }

        private static (ActionResult, ActionRevertRecord) CreateLocalizationTable(EditorAction a)
        {
            var p = (a.assetPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) return (Fail("createLocalizationTable requires assetPath"), null);
            var tableType = Type.GetType("UnityEngine.Localization.Tables.StringTable, Unity.Localization");
            if (tableType == null) return (Fail("Localization package not available. Add com.unity.localization."), null);
            if (!p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) p += ".asset";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar))))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir.Replace('/', Path.DirectorySeparatorChar)));
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(p);
            var table = ScriptableObject.CreateInstance(tableType);
            if (table == null) return (Fail("Failed to create LocalizationTable"), null);
            AssetDatabase.CreateAsset(table, uniquePath);
            AssetDatabase.SaveAssets();
            return (Ok(uniquePath), new ActionRevertRecord { action = "createLocalizationTable", assetPath = uniquePath });
        }

        private static (ActionResult, ActionRevertRecord) CreateCircleSprite(EditorAction a)
        {
            var path = !string.IsNullOrWhiteSpace(a.assetPath) ? a.assetPath.Trim() : "Assets/Textures/CoBuddyCircle.png";
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";
            var size = a.spriteSize > 0 ? a.spriteSize : 128;
            size = Mathf.Clamp(size, 16, 512);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            var uniquePath = AssetDatabase.GenerateUniqueAssetPath(path);
            var tex = new Texture2D(size, size);
            var center = size / 2f;
            var radius = center - 1;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);
                    tex.SetPixel(x, y, dist <= radius ? Color.white : Color.clear);
                }
            tex.Apply();
            var bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), uniquePath.Replace('/', Path.DirectorySeparatorChar)), bytes);
            AssetDatabase.Refresh();
            var importer = AssetImporter.GetAtPath(uniquePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 100;
                importer.SaveAndReimport();
            }
            return (Ok(uniquePath), new ActionRevertRecord { action = "createCircleSprite", assetPath = uniquePath });
        }

        private static string EnsureDefaultPanelSettingsExists()
        {
            var path = "Assets/UI/CoBuddyDefaultPanelSettings.asset";
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (existing != null) return path;
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var themeGuids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            if (themeGuids != null && themeGuids.Length > 0)
            {
                var themePath = AssetDatabase.GUIDToAssetPath(themeGuids[0]);
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (theme != null) panel.themeStyleSheet = theme;
            }
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();
            return path;
        }

        private static string GetPath(GameObject go)
        {
            if (go == null) return "";
            var parts = new System.Collections.Generic.List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Insert(0, t.gameObject.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        // -----------------------------------------------------------------------
        // setComponentProperty — set any public/private field or property on a
        // component using reflection. Supports: float, int, bool, string,
        // Vector2, Vector3, Color, enums, object references, LayerMask,
        // RigidbodyConstraints.
        // -----------------------------------------------------------------------
        private static (ActionResult, ActionRevertRecord) SetComponentProperty(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("setComponentProperty requires target"), null);
            if (string.IsNullOrWhiteSpace(a.componentType))
                return (Fail("setComponentProperty requires componentType"), null);
            if (string.IsNullOrWhiteSpace(a.property))
                return (Fail("setComponentProperty requires property"), null);
            if (a.value == null)
                return (Fail("setComponentProperty requires value"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            // Resolve component — try typed first, then name search.
            // "GameObject" / "UnityEngine.GameObject" is not a Component — the LLM sometimes
            // sends it when targeting GameObject-level properties like name, tag, layer.
            // Fall back to Transform (always present) and scan all components for the property.
            Component component = null;
            string requestedType = (a.componentType ?? "").Trim();
            bool isGameObjectType = requestedType.Equals("GameObject", StringComparison.OrdinalIgnoreCase)
                || requestedType.Equals("UnityEngine.GameObject", StringComparison.OrdinalIgnoreCase);

            if (isGameObjectType)
            {
                // Check if the property is a direct GameObject field (name, tag, layer, isStatic)
                var goProp = typeof(GameObject).GetProperty(a.property,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var goField = goProp == null ? typeof(GameObject).GetField(a.property,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) : null;
                if (goProp != null || goField != null)
                {
                    Undo.RecordObject(go, $"CoBuddy setComponentProperty {a.property}");
                    try
                    {
                        var goMemberType = goProp != null ? goProp.PropertyType : goField.FieldType;
                        object parsed = ParseComponentValue(a.value, goMemberType,
                            (a.valueType ?? "auto").ToLowerInvariant());
                        if (goProp != null) goProp.SetValue(go, parsed);
                        else goField.SetValue(go, parsed);
                        EditorUtility.SetDirty(go);
                    }
                    catch (Exception ex)
                    {
                        return (Fail($"Failed to set GameObject.{a.property}: {ex.Message}"), null);
                    }
                    return (new ActionResult { success = true, message = $"Set {a.property} on {go.name}", path = GetPath(go) }, null);
                }
                // Not a GameObject field — fall back to Transform
                component = go.transform;
            }
            else
            {
                var compType = ResolveComponentType(requestedType);
                if (compType != null)
                    component = go.GetComponent(compType);
                if (component == null)
                {
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (c.GetType().Name.Equals(requestedType, StringComparison.OrdinalIgnoreCase))
                        { component = c; break; }
                    }
                }
            }
            if (component == null)
                return (Fail($"Component '{a.componentType}' not found on '{a.target}'"), null);

            // Walk the inheritance chain to find the member
            var memberOwnerType = component.GetType();
            System.Reflection.FieldInfo field = null;
            System.Reflection.PropertyInfo prop = null;
            var searchType = memberOwnerType;
            while (searchType != null && field == null && prop == null)
            {
                field = searchType.GetField(a.property,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (field == null)
                    prop = searchType.GetProperty(a.property,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                searchType = searchType.BaseType;
            }
            if (field == null && prop == null)
                return (Fail($"Field/property '{a.property}' not found on {memberOwnerType.Name}"), null);

            var memberType = field != null ? field.FieldType : prop.PropertyType;
            var vt = (a.valueType ?? "auto").ToLowerInvariant();

            Undo.RecordObject(component, $"CoBuddy setComponentProperty {a.property}");
            try
            {
                object parsed = ParseComponentValue(a.value, memberType, vt);
                if (field != null) field.SetValue(component, parsed);
                else prop.SetValue(component, parsed);
                EditorUtility.SetDirty(component);
            }
            catch (Exception ex)
            {
                return (Fail($"Failed to set '{a.property}': {ex.Message}"), null);
            }

            return (Ok(GetPath(go)), null);
        }

        private static object ParseComponentValue(string raw, Type memberType, string vt)
        {
            // Object reference (scene path or asset path)
            if (vt == "objectref" || (memberType != null && typeof(UnityEngine.Object).IsAssignableFrom(memberType) && vt != "layermask"))
            {
                var refGo = FindByPath(raw);
                if (refGo != null)
                {
                    if (memberType == typeof(GameObject)) return refGo;
                    if (memberType == typeof(Transform)) return refGo.transform;
                    if (memberType != null && typeof(Component).IsAssignableFrom(memberType))
                        return refGo.GetComponent(memberType);
                    return refGo;
                }
                var loadType = memberType ?? typeof(UnityEngine.Object);
                var asset = AssetDatabase.LoadAssetAtPath(raw, loadType);
                if (asset != null) return asset;
                return null;
            }

            // LayerMask
            if (vt == "layermask" || memberType == typeof(LayerMask))
            {
                if (int.TryParse(raw, out int idx)) return (LayerMask)(1 << idx);
                int layer = LayerMask.NameToLayer(raw);
                return layer >= 0 ? (LayerMask)(1 << layer) : (LayerMask)0;
            }

            // RigidbodyConstraints (supports combined names)
            if (vt == "rigidbodyconstraints" || memberType == typeof(RigidbodyConstraints))
            {
                if (raw.Equals("FreezeAll", StringComparison.OrdinalIgnoreCase)) return RigidbodyConstraints.FreezeAll;
                if (raw.Equals("FreezeRotation", StringComparison.OrdinalIgnoreCase))
                    return RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;
                if (raw.Equals("FreezePosition", StringComparison.OrdinalIgnoreCase))
                    return RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezePositionZ;
                if (Enum.TryParse<RigidbodyConstraints>(raw, true, out var rc)) return rc;
                return RigidbodyConstraints.None;
            }

            // Any other enum
            if (memberType != null && memberType.IsEnum)
            {
                if (Enum.TryParse(memberType, raw, true, out var enumVal)) return enumVal;
                return Enum.GetValues(memberType).GetValue(0);
            }

            // Primitive types
            if (memberType == typeof(float) || vt == "float")
                return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (memberType == typeof(int) || vt == "int")
                return int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (memberType == typeof(bool) || vt == "bool")
                return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
            if (memberType == typeof(string) || vt == "string")
                return raw;

            // Vectors/Color — accept "[x,y,z]" or "x,y,z"
            var parts = raw.Trim('[', ']').Split(',');
            if (memberType == typeof(Vector3) || vt == "vector3")
            {
                if (parts.Length >= 3)
                    return new Vector3(
                        float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            if (memberType == typeof(Vector2) || vt == "vector2")
            {
                if (parts.Length >= 2)
                    return new Vector2(
                        float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            if (memberType == typeof(Color) || vt == "color")
            {
                if (parts.Length >= 4)
                    return new Color(
                        float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                if (parts.Length == 3)
                    return new Color(
                        float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }

            // Fallback: Convert
            return Convert.ChangeType(raw, memberType ?? typeof(string));
        }

        // -----------------------------------------------------------------------
        // assignMaterial — load a .mat asset and assign it to a Renderer slot
        // -----------------------------------------------------------------------
        private static (ActionResult, ActionRevertRecord) AssignMaterial(EditorAction a)
        {
            if (string.IsNullOrWhiteSpace(a.target))
                return (Fail("assignMaterial requires target"), null);
            if (string.IsNullOrWhiteSpace(a.materialPath))
                return (Fail("assignMaterial requires materialPath"), null);

            var go = FindByPath(a.target);
            if (go == null)
                return (FailTargetNotFound(a.target), null);

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return (Fail($"No Renderer on '{a.target}'"), null);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(a.materialPath);
            if (mat == null)
                return (Fail($"Material not found at: {a.materialPath}"), null);

            Undo.RecordObject(renderer, "CoBuddy assignMaterial");
            var mats = (Material[])renderer.sharedMaterials.Clone();
            int slot = a.slot;
            if (slot < 0 || slot >= mats.Length)
            {
                if (slot >= mats.Length)
                {
                    var expanded = new Material[slot + 1];
                    mats.CopyTo(expanded, 0);
                    mats = expanded;
                }
                else slot = 0;
            }
            mats[slot] = mat;
            renderer.sharedMaterials = mats;
            EditorUtility.SetDirty(renderer);
            return (Ok(GetPath(go)), null);
        }

        private static ActionResult Ok(string path = null, string data = null)
        {
            return new ActionResult { success = true, message = "OK", path = path ?? "", data = data };
        }

        private static ActionResult Fail(string message)
        {
            return new ActionResult { success = false, message = message ?? "Unknown error", path = "" };
        }

        /// <summary>
        /// Creates a "Target not found" failure with fuzzy match suggestions.
        /// The LLM can use the suggestions to self-correct without a full retry.
        /// </summary>
        private static ActionResult FailTargetNotFound(string targetPath)
        {
            var suggestions = FindSimilarPaths(targetPath ?? "", 3);
            if (suggestions.Length > 0)
                return Fail($"Target not found: {targetPath}. Did you mean: {string.Join(", ", suggestions)}?");
            return FailTargetNotFound(targetPath);
        }
    }
}
