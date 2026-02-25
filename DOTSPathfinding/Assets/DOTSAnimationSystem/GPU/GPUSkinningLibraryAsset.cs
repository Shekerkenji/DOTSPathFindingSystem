using UnityEngine;


namespace DOTSAnimation
{
    /// <summary>
    /// Saved alongside the animation texture. Assigned to GPUSkinningAuthoring
    /// so the ECS baker can read clip metadata without re-sampling.
    /// </summary>
    [CreateAssetMenu(menuName = "DOTS Animation/GPU Skinning Library")]
    public class GPUSkinningLibraryAsset : ScriptableObject
    {
        public int boneCount;
        public int totalFrames;
        public float sampleRate;
        public GPUClipInfoAsset[] clips;
    }

    [System.Serializable]
    public class GPUClipInfoAsset
    {
        public string clipName;
        public int textureRowOffset;
        public int frameCount;
        public float duration;
        public float frameRate;
        public bool isLooping;
    }
}