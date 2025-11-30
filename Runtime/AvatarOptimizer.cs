using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace MilchZocker.AvatarOptimizer
{
    [AddComponentMenu("MilchZocker/Avatar Optimizer")]
    public class AvatarOptimizer : MonoBehaviour
    {
        [Header("=== Bone Optimisation ===")]
        [Space(5)]
        public BoneOptimizationSettings boneSettings = new BoneOptimizationSettings();
        
        [Header("=== Mesh Optimisation ===")]
        [Space(5)]
        public MeshOptimizationSettings meshSettings = new MeshOptimizationSettings();
        
        [Header("=== Blendshape Optimisation ===")]
        [Space(5)]
        public BlendshapeOptimizationSettings blendshapeSettings = new BlendshapeOptimizationSettings();
        
        [Header("=== Texture Atlas Generation ===")]
        [Space(5)]
        public AtlasGenerationSettings atlasSettings = new AtlasGenerationSettings();
        
#if UNITY_EDITOR
        [Header("=== Debug & Statistics ===")]
        // Hidden from the default inspector so users can’t edit stats.
        // Custom editor shows this read-only.
        [SerializeField, HideInInspector]
        public OptimizationStats stats = new OptimizationStats();
        
        [Serializable]
        public class OptimizationStats
        {
            public int bonesRemoved;
            public int boneReferencesRemoved;
            public int blendshapesRemoved;
            public int verticesMerged;
            public int looseVerticesRemoved;
            public int meshesCombined;
            public int atlasesGenerated;
            public int textureMemorySavedMB;
            public float optimizationTimeSeconds;
        }
#endif
        
        [Serializable]
        public class BoneOptimizationSettings
        {
            [Tooltip("Remove unused bone references from Skinned Mesh Renderers")]
            public bool removeUnusedBoneReferences = true;
            
            [Tooltip("Only remove bone references if they have zero weight across all vertices")]
            public bool onlyRemoveZeroWeightBones = true;
            
            [Tooltip("Minimum weight threshold to consider a bone as 'used' (0.0001 - 0.01)")]
            [Range(0.0001f, 0.01f)]
            public float minimumBoneWeightThreshold = 0.0001f;
            
            [Space(10)]
            [Tooltip("Remove actual bones (GameObjects) that have no weights")]
            public bool removeBonesWithoutWeights = false;
            
            [Tooltip("Check for Magica Cloth components before removing bones")]
            public bool checkForMagicaCloth = true;
            
            [Tooltip("Check for Dynamic Bone components before removing bones")]
            public bool checkForDynamicBones = true;
            
            [Tooltip("Check for VRCPhysBone components before removing bones")]
            public bool checkForVRCPhysBones = true;
            
            [Tooltip("Manually ask before removing each bone (Editor only)")]
            public bool manualConfirmationPerBone = true;
            
            [Tooltip("Preserve bones that are animation targets")]
            public bool preserveAnimatedBones = true;
            
            [Tooltip("Preserve bones with specific name patterns (comma-separated)")]
            public string preserveBoneNamePatterns = "Hair,Skirt,Cloth,Breast,Tail";
            
            [Space(10)]
            [Tooltip("Keep bones that are children of used bones")]
            public bool preserveChildrenOfUsedBones = true;
            
            [Tooltip("Keep bones that have constraints attached")]
            public bool preserveBonesWithConstraints = true;
        }
        
        [Serializable]
        public class MeshOptimizationSettings
        {
            [Tooltip("Merge vertices within specified distance")]
            public bool mergeVerticesByDistance = true;
            
            [Tooltip("Distance threshold for merging vertices")]
            [Range(0.00001f, 0.01f)]
            public float mergeDistance = 0.0001f;
            
            [Tooltip("Also compare normals when merging (more accurate but slower)")]
            public bool compareNormals = true;
            
            [Tooltip("Normal angle threshold for merging (degrees)")]
            [Range(0f, 45f)]
            public float normalAngleThreshold = 5f;
            
            [Tooltip("Also compare UVs when merging")]
            public bool compareUVs = false;
            
            [Tooltip("UV distance threshold for merging")]
            [Range(0.001f, 0.1f)]
            public float uvDistanceThreshold = 0.01f;
            
            [Space(10)]
            [Tooltip("Delete loose vertices (not connected to any face)")]
            public bool deleteLooseVertices = true;
            
            [Space(10)]
            [Tooltip("Combine meshes with identical materials (excludes animated meshes)")]
            public bool combineMeshes = false;
            
            [Space(10)]
            [Tooltip("Recalculate normals after optimisation")]
            public bool recalculateNormals = false;
            
            [Tooltip("Recalculate tangents after optimisation")]
            public bool recalculateTangents = false;
            
            [Space(10)]
            [Tooltip("Optimise mesh for rendering (Unity's built-in optimisation)")]
            public bool optimizeMeshForRendering = true;
            
            [Tooltip("Apply mesh compression (can save memory but may reduce quality)")]
            public bool applyMeshCompression = false;
            
            public MeshCompressionLevel compressionLevel = MeshCompressionLevel.Medium;
            
            public enum MeshCompressionLevel
            {
                Low,
                Medium,
                High
            }
            
            [Space(10)]
            [Tooltip("Only process meshes with name patterns (comma-separated, leave empty for all)")]
            public string meshNameFilter = "";
            
            [Tooltip("Exclude meshes with name patterns (comma-separated)")]
            public string meshNameExclude = "";
        }
        
        [Serializable]
        public class BlendshapeOptimizationSettings
        {
            [Tooltip("Remove unused blendshapes (keeps CVR system blendshapes)")]
            public bool removeUnusedBlendshapes = true;
            
            [Tooltip("Scan override animator controller for used blendshapes")]
            public bool scanOverrideController = true;
            
            [Tooltip("Scan advanced avatar settings animator for used blendshapes")]
            public bool scanAdvancedAvatarSettings = true;
            
            [Tooltip("Preserve CVRAvatar blinking blendshapes")]
            public bool preserveBlinkBlendshapes = true;
            
            [Tooltip("Preserve CVRAvatar viseme (lip sync) blendshapes")]
            public bool preserveVisemeBlendshapes = true;
            
            [Tooltip("Preserve CVRFaceTracking blendshapes")]
            public bool preserveFaceTrackingBlendshapes = true;
            
            [Tooltip("Preserve eye look/movement blendshapes")]
            public bool preserveEyeLookBlendshapes = true;
            
            [Space(10)]
            [Tooltip("Remove blendshapes with zero delta (no actual deformation)")]
            public bool removeZeroDeltaBlendshapes = true;
            
            [Tooltip("Minimum vertex movement to consider blendshape as non-zero (in metres)")]
            [Range(0.00001f, 0.001f)]
            public float zeroDeltaThreshold = 0.00001f;
            
            [Space(10)]
            [Tooltip("Manually preserve blendshapes by name pattern (comma-separated)")]
            public string preserveBlendshapePatterns = "";
            
            [Tooltip("Explicitly remove blendshapes by name pattern (comma-separated, overrides preserves)")]
            public string forceRemoveBlendshapePatterns = "";
            
            [Space(10)]
            [Tooltip("Log all found vs removed blendshapes to console")]
            public bool verboseLogging = false;
        }
        
        [Serializable]
        public class AtlasGenerationSettings
        {
            [Tooltip("Generate texture atlases for materials using the same shader")]
            public bool generateTextureAtlas = false;
            
            [Space(10)]
            [Header("Animation Safety")]
            [Tooltip("Exclude materials that are animated in controllers from atlasing")]
            public bool excludeAnimatedMaterials = true;
            
            [Tooltip("Scan override animator controller for material animations")]
            public bool scanOverrideController = true;
            
            [Tooltip("Scan advanced avatar settings animator for material animations")]
            public bool scanAdvancedAvatarSettings = true;
            
            [Tooltip("Manually exclude materials by name pattern (comma-separated)")]
            public string excludeMaterialPatterns = "";
            
            [Space(10)]
            [Header("Atlas Generation Mode")]
            [Tooltip("Use enhanced atlas workflow with better grouping and optimization")]
            [FormerlySerializedAs("useXatlas")]
            public bool useEnhancedAtlasWorkflow = false;
            
            [Tooltip("Merge materials using same textures by adjusting UV padding (experimental)")]
            public bool mergeIdenticalTextures = false;
            
            [Space(10)]
            [Header("Basic Atlas Settings")]
            [Tooltip("Maximum atlas texture size")]
            public AtlasSize maxAtlasSize = AtlasSize._2048;
            
            public enum AtlasSize
            {
                _512 = 512,
                _1024 = 1024,
                _2048 = 2048,
                _4096 = 4096,
                _8192 = 8192
            }
            
            [Tooltip("Padding between textures in atlas (pixels)")]
            [Range(0, 16)]
            public int atlasPadding = 2;
            
            [Space(10)]
            [Header("Advanced Settings")]
            [Tooltip("Minimum materials count to create atlas (won't atlas if less materials)")]
            [Range(2, 10)]
            public int minimumMaterialsForAtlas = 2;
            
            [Tooltip("Only atlas texture properties with these names (comma-separated, use * for all)")]
            public string allowedTextureProperties = "*";
            
            [Tooltip("Exclude texture properties with these names (comma-separated, supports wildcards like *Shadow*)")]
            public string excludedTextureProperties = "";
            
            [Tooltip("Minimum texture size to include in atlas (smaller textures will be skipped)")]
            [Range(16, 512)]
            public int minimumTextureSize = 32;
            
            [Space(10)]
            [Header("Shader Filtering")]
            [Tooltip("Only atlas materials using shaders with these names (comma-separated, empty = all shaders)")]
            public string allowedShaderNames = "";
            
            [Tooltip("Exclude materials using shaders with these names (comma-separated)")]
            public string excludedShaderNames = "Hidden,UI,Unlit/Transparent";
            
            [Space(10)]
            [Tooltip("Compress generated atlases")]
            public bool compressAtlases = true;
            
            [Tooltip("Atlas texture compression format")]
            public AtlasCompressionFormat compressionFormat = AtlasCompressionFormat.Automatic;
            
            public enum AtlasCompressionFormat
            {
                Automatic,
                DXT1,
                DXT5,
                BC7,
                ASTC,
                Uncompressed
            }
            
            [Space(10)]
            [Tooltip("Log detailed information about atlas generation and exclusions")]
            public bool verboseLogging = false;
        }
    }
}
