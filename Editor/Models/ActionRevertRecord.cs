using System;

namespace CoBuddy.Editor.Models
{
    /// <summary>
    /// Records what to undo for each executed action.
    /// </summary>
    [Serializable]
    public class ActionRevertRecord
    {
        public string action;
        public string path;
        public string prefabPath;
        public string scenePath;
        public string targetPath;
        public string componentType;
        // createScriptableObject, createMaterial, createAnimationClip
        public string assetPath;
        // createAnimationClip - controller created alongside clip
        public string controllerPath;
        // reparentGameObject
        public string oldParentPath;
        // updateGameObject
        public float[] previousPosition;
        public float[] previousRotation;
        public float[] previousScale;
        public string previousTag;
        // setRectTransformLayout
        public float prevAnchorMinX, prevAnchorMinY, prevAnchorMaxX, prevAnchorMaxY;
        public float prevPosX, prevPosY, prevSizeDeltaX, prevSizeDeltaY, prevPivotX, prevPivotY;
        // updateMaterial
        public string previousMaterialState;
        // moveAsset: path=original source, assetPath=current location
        public string oldAssetPath;
    }
}
