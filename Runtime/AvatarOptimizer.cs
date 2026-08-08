using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace MilchZocker.AvatarOptimizer
{
    [AddComponentMenu("NDMF Avatar Optimizer/Avatar Optimizer")]
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

        [Header("=== Optimization Analysis ===")]
        [Space(5)]
        [Tooltip("When enabled, run analysis and report findings without applying destructive optimizations")]
        public bool reportOnly = false;

        [Space(5)]
        public AnimatorAnalysisSettings animatorAnalysisSettings = new AnimatorAnalysisSettings();

        [Space(5)]
        public MeshAnalysisSettings meshAnalysisSettings = new MeshAnalysisSettings();

        [Space(5)]
        public BlendshapeAnalysisSettings blendshapeAnalysisSettings = new BlendshapeAnalysisSettings();

        [Space(5)]
        public PhysicsAnalysisSettings physicsAnalysisSettings = new PhysicsAnalysisSettings();

        [Space(5)]
        public ReportingSettings reportingSettings = new ReportingSettings();
        
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
            public int meshesModified;
            public int atlasesGenerated;
            public int textureMemorySavedMB;
            public float optimizationTimeSeconds;
            public int analysisIssues;
            public int analysisWarnings;
            public int analysisInfos;
            public string lastAnalysisReport;
        }
#endif

        [Serializable]
        public class AnimatorAnalysisSettings
        {
            [Tooltip("Enable analysis of animator controllers and layer complexity")]
            public bool enableAnalysis = true;

            [Tooltip("Warn when many layers are detected in a single controller")]
            public bool warnOnExcessiveLayers = true;

            [Range(1, 32)]
            [Tooltip("Recommended maximum layer count before warning")]
            public int maxRecommendedLayers = 8;

            [Tooltip("Warn when a controller uses an excessive number of parameters")]
            public bool warnOnExcessiveParameters = true;

            [Range(1, 100)]
            [Tooltip("Recommended maximum parameter count before warning")]
            public int maxRecommendedParameters = 20;

            [Tooltip("Prefer lighter controller patterns for heavy toggles when possible")]
            public bool preferDirectBlendTreesForHeavyToggles = true;

            [Tooltip("Warn when a controller uses too many bool parameters for toggle-driven logic")]
            public bool warnOnHeavyToggleSetup = true;

            [Range(1, 32)]
            [Tooltip("Recommended maximum bool-parameter count before warning")]
            public int maxRecommendedBoolParameters = 6;

            [Tooltip("Warn when a controller uses self-transitions that can make state logic harder to reason about")]
            public bool warnOnSelfTransitions = true;

            [Tooltip("Preserve CVR facial systems when evaluating animator-related changes")]
            public bool preserveCVRFacialSafety = true;

            [Tooltip("Keep analysis report-only unless explicitly enabled for auto-application")]
            public bool reportOnly = true;
        }

        [Serializable]
        public class MeshAnalysisSettings
        {
            [Tooltip("Enable analysis of meshes, material slots, and draw-call pressure")]
            public bool enableAnalysis = true;

            [Tooltip("Allow safe mesh merging when compatibility checks pass")]
            public bool allowSafeMeshMerging = false;

            [Tooltip("Allow material deduplication when compatible")]
            public bool allowMaterialDeduplication = true;

            [Range(1, 32)]
            [Tooltip("Recommended maximum material slots before warning")]
            public int maxRecommendedMaterialSlots = 8;

            [Range(1, 64)]
            [Tooltip("Recommended maximum mesh count before warning")]
            public int maxRecommendedMeshCount = 12;

            [Tooltip("Favor draw-call reduction when evaluating mesh changes")]
            public bool preferDrawCallReduction = true;

            [Tooltip("Keep analysis report-only unless explicitly enabled for auto-application")]
            public bool reportOnly = true;
        }

        [Serializable]
        public class BlendshapeAnalysisSettings
        {
            [Tooltip("Enable blendshape analysis and cleanup suggestions")]
            public bool enableAnalysis = true;

            [Tooltip("Protect CVR facial systems while evaluating blendshape changes")]
            public bool preserveCVRFacialShapes = true;

            [Tooltip("Remove blendshapes with nearly zero delta when safe")]
            public bool removeZeroDeltaBlendshapes = true;

            [Range(0.000001f, 0.001f)]
            [Tooltip("Threshold below which a blendshape is considered effectively empty")]
            public float zeroDeltaThreshold = 0.00001f;

            [Tooltip("Preserve custom blendshapes that match known facial patterns")]
            public bool preserveCustomPatterns = true;

            [Tooltip("Comma-separated preserve patterns for facial or custom shapes")]
            public string preserveBlendshapePatterns = "Blink,Viseme,EyeLook,FaceTracking";

            [Tooltip("Keep analysis report-only unless explicitly enabled for auto-application")]
            public bool reportOnly = true;
        }

        [Serializable]
        public class PhysicsAnalysisSettings
        {
            [Tooltip("Enable analysis of cloth and physics-heavy components")]
            public bool enableAnalysis = true;

            [Tooltip("Warn when Magica Cloth self-collision is likely to be costly")]
            public bool warnOnMagicaSelfCollision = true;

            [Tooltip("Warn when Magica Cloth mutual collision is likely to be costly")]
            public bool warnOnMagicaMutualCollision = true;

            [Tooltip("Warn when cloth proxy meshes are unusually large")]
            public bool warnOnHighProxyVertexCount = true;

            [Range(100, 100000)]
            [Tooltip("Recommended maximum proxy vertex count before warning")]
            public int maxRecommendedProxyVertexCount = 4000;

            [Tooltip("Warn when simulation frequency is set aggressively high")]
            public bool warnOnHighSimulationFrequency = true;

            [Range(30, 240)]
            [Tooltip("Recommended maximum simulation frequency before warning")]
            public int maxRecommendedSimulationFrequency = 90;

            [Tooltip("Warn when Dynamic Bone setups use an excessive number of colliders")]
            public bool warnOnDynamicBoneComplexity = true;

            [Range(1, 64)]
            [Tooltip("Recommended maximum Dynamic Bone collider count before warning")]
            public int maxRecommendedDynamicBoneColliders = 8;

            [Tooltip("Keep analysis report-only unless explicitly enabled for auto-application")]
            public bool reportOnly = true;
        }

        [Serializable]
        public class ReportingSettings
        {
            [Tooltip("Show the optimization findings summary in the inspector")]
            public bool showOptimizationSummary = true;

            [Tooltip("Show severity labels for each analysis finding")]
            public bool showIssueSeverity = true;

            [Range(1, 25)]
            [Tooltip("Maximum number of findings to display per category")]
            public int maxIssuesPerCategory = 10;

            [Tooltip("Include recommended next actions in the report")]
            public bool includeSuggestions = true;

            [Tooltip("Label whether a finding can be auto-fixed")]
            public bool includeAutoFixAvailability = true;
        }
        
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
            [Tooltip("Combine meshes with identical materials. Disabled for skinned avatar meshes to preserve rigging, blendshape, and bind-pose safety.")]
            public bool combineMeshes = false;

            [Tooltip("Exclude the face mesh selected on the CVR Avatar component from mesh merging")]
            public bool excludeFaceMeshFromCombine = true;
            
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
            
            [Space(10)]
            [Tooltip("Deduplicate identical materials to reduce draw calls")]
            public bool deduplicateMaterials = true;

            [Space(10)]
            [Header("Advanced Mesh Optimizations")]
            [Tooltip("Optimize index buffer for GPU vertex cache and reorder vertices where supported")]
            public bool optimizeIndexBuffer = false;

            [Tooltip("Merge identical submeshes that use the same material to reduce draw calls")]
            public bool mergeIdenticalSubmeshes = true;

            [Space(10)]
            [Header("Attribute Stripping")]
            [Tooltip("Enable intelligent stripping of unused vertex attributes based on shader requirements")]
            public bool stripUnusedAttributes = true;

            [Tooltip("Strip tangent data if not used by shader")]
            public bool stripTangents = false;

            [Tooltip("Strip vertex color data if not used by shader")]
            public bool stripVertexColors = true;

            [Tooltip("Strip lightmap UV coordinates if not used")]
            public bool stripLightmapUVs = true;

            [Tooltip("Strip unused UV channels (UV2, UV3, UV4) if not used by shader")]
            public bool stripExtraUVChannels = true;

            [Tooltip("Log detailed information about mesh-level optimizations")]
            public bool verboseLogging = false;
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
            public bool useEnhancedAtlasWorkflow = false;
            
            [Tooltip("Merge materials using same textures by adjusting UV/padding (experimental)")]
            public bool mergeIdenticalTextures = true;
            
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
            [Tooltip("Minimum output size for atlas textures. Set to 0 to use texture-derived sizes only. Higher values produce better quality but larger file sizes.")]
            [Range(0, 2048)]
            public int minimumOutputAtlasSize = 256;
            
            [Space(10)]
            [Header("Advanced Settings")]
            [Tooltip("Minimum materials count to create atlas (won't atlas if less materials)")]
            [Range(2, 10)]
            public int minimumMaterialsForAtlas = 2;
            
            [Tooltip("Only atlas texture properties with these names (comma-separated, use * for all)")]
            public string allowedTextureProperties = "*";
            
            [Tooltip("Exclude texture properties with these names (comma-separated, supports wildcards like *Shadow*)")]
            public string excludedTextureProperties = "_ReflectionCubeTex";
            
            [Tooltip("Minimum texture size to include in atlas (smaller textures will be skipped)")]
            [Range(16, 512)]
            public int minimumTextureSize = 32;
            
            [Space(10)]
            [Header("Shader Filtering")]
            [Tooltip("Only atlas materials using shaders with these names (comma-separated, empty = all shaders)")]
            public string allowedShaderNames = "";
            
            [Tooltip("Exclude materials using shaders with these names (comma-separated)")]
            public string excludedShaderNames = "";
            
            [Space(10)]
            [Header("═══════ Adaptive Compression Configuration ═══════")]
            [Tooltip("Enable adaptive compression based on texture complexity analysis")]
            public bool useAdaptiveCompression = true;
            
            [Tooltip("Log detailed complexity analysis for each texture")]
            public bool verboseDensityLogging = false;
            
            [Space(5)]
            [Tooltip("Define custom complexity score ranges and their compression settings")]
            public List<CompressionTier> compressionTiers = new List<CompressionTier>
            {
                new CompressionTier { tierName = "Ultra Low (Solid Colors)", minComplexity = 0.00f, maxComplexity = 0.10f, maxTextureSize = 256, crunchQuality = 40, enableTier = true },
                new CompressionTier { tierName = "Very Low (Simple Masks)", minComplexity = 0.10f, maxComplexity = 0.20f, maxTextureSize = 512, crunchQuality = 50, enableTier = true },
                new CompressionTier { tierName = "Low (Basic Patterns)", minComplexity = 0.20f, maxComplexity = 0.35f, maxTextureSize = 1024, crunchQuality = 60, enableTier = true },
                new CompressionTier { tierName = "Medium (Moderate Detail)", minComplexity = 0.35f, maxComplexity = 0.55f, maxTextureSize = 2048, crunchQuality = 75, enableTier = true },
                new CompressionTier { tierName = "High (Detailed Textures)", minComplexity = 0.55f, maxComplexity = 0.75f, maxTextureSize = 4096, crunchQuality = 85, enableTier = true },
                new CompressionTier { tierName = "Very High (Complex/Text)", minComplexity = 0.75f, maxComplexity = 0.90f, maxTextureSize = 4096, crunchQuality = 92, enableTier = true },
                new CompressionTier { tierName = "Ultra High (Critical Detail)", minComplexity = 0.90f, maxComplexity = 1.00f, maxTextureSize = 8192, crunchQuality = 100, enableTier = true }
            };
            
            [Space(5)]
            [Header("Complexity Analysis Weights")]
            [Tooltip("Weight for color diversity in complexity calculation (0-1)")]
            [Range(0f, 1f)]
            public float colorDiversityWeight = 0.30f;
            
            [Tooltip("Weight for color variance in complexity calculation (0-1)")]
            [Range(0f, 1f)]
            public float colorVarianceWeight = 0.30f;
            
            [Tooltip("Weight for edge density in complexity calculation (0-1)")]
            [Range(0f, 1f)]
            public float edgeDensityWeight = 0.40f;
            
            [Tooltip("Edge detection threshold (0-1, lower = more sensitive)")]
            [Range(0.01f, 0.5f)]
            public float edgeDetectionThreshold = 0.1f;
            
            [Space(5)]
            [Header("Property-Specific Complexity Modifiers")]
            [Tooltip("Add complexity boost for main/albedo textures")]
            [Range(-0.5f, 0.5f)]
            public float mainTextureComplexityBoost = 0.15f;
            
            [Tooltip("Add complexity boost for normal maps")]
            [Range(-0.5f, 0.5f)]
            public float normalMapComplexityBoost = 0.10f;
            
            [Tooltip("Add complexity boost for detail textures")]
            [Range(-0.5f, 0.5f)]
            public float detailTextureComplexityBoost = 0.15f;
            
            [Tooltip("Reduce complexity for mask textures")]
            [Range(-0.5f, 0.5f)]
            public float maskTextureComplexityReduction = -0.10f;
            
            [Tooltip("Reduce complexity for emission textures")]
            [Range(-0.5f, 0.5f)]
            public float emissionTextureComplexityBoost = -0.05f;
            
            [Space(10)]
            [Header("═══════ Texture Cache & Deduplication ═══════")]
            [Tooltip("Use texture fingerprinting to avoid processing duplicates")]
            public bool enableTextureCaching = true;
            
            [Tooltip("Deduplicate identical textures across materials before atlasing")]
            public bool deduplicateBeforeAtlas = true;
            
            [Tooltip("Cache processed textures for faster re-builds")]
            public bool persistTextureCache = false;
            
            [Space(10)]
            [Header("═══════ Per-Property Atlas Control ═══════")]
            [Tooltip("Allow different atlas sizes per texture property (e.g., larger for albedo, smaller for masks)")]
            public bool enablePerPropertySizing = false;
            
            [Tooltip("Property-specific atlas size overrides (format: PropertyName:Size, e.g., _MainTex:4096,_Mask:1024)")]
            public string perPropertyAtlasSizes = "";
            
            [Tooltip("Force specific properties to always share the same atlas size")]
            public string linkedAtlasProperties = "_MainTex,_BumpMap,_MetallicGlossMap";
            
            [Space(5)]
            [Tooltip("Property-specific crunch quality overrides (format: PropertyName:Quality, e.g., _MainTex:95,_Mask:50)")]
            public string perPropertyCrunchQuality = "";
            
            [Tooltip("Force specific properties to use uncompressed format (comma-separated)")]
            public string uncompressedProperties = "";
            
            [Space(10)]
            [Header("═══════ Atlas Naming & Organization ═══════")]
            [Tooltip("Prefix for generated atlas textures")]
            public string atlasNamePrefix = "Atlas_";
            
            [Tooltip("Include shader name in atlas filename")]
            public bool includeShaderInName = true;
            
            [Tooltip("Include property name in atlas filename")]
            public bool includePropertyInName = true;
            
            [Tooltip("Add timestamp to atlas names (prevents asset conflicts)")]
            public bool addTimestampToName = false;
            
            [Tooltip("Add complexity tier to atlas filename")]
            public bool includeTierInName = true;
            
            [Tooltip("Include the complexity score in the atlas name (e.g., 'Atlas_0.85')")]
            public bool includeComplexityScore = true;
            
            [Space(10)]
            [Header("═══════ Safety & Validation ═══════")]
            [Tooltip("Validate UV coordinates are within bounds before atlasing")]
            public bool validateUVBounds = true;
            
            [Tooltip("Warn if UV coordinates extend outside 0-1 range")]
            public bool warnOnInvalidUVs = false;
            
            [Tooltip("Automatically fix out-of-bounds UVs by wrapping/clamping")]
            public bool autoFixInvalidUVs = true;
            
            [Tooltip("Skip materials with invalid UVs instead of fixing them")]
            public bool skipInvalidUVMaterials = false;
            
            [Space(5)]
            [Tooltip("Maximum number of materials to combine in a single atlas")]
            [Range(2, 100)]
            public int maxMaterialsPerAtlas = 5;
            
            [Tooltip("Skip atlasing if estimated atlas would exceed this pixel count")]
            public bool limitAtlasPixelCount = true;
            
            [Tooltip("Maximum total pixels in an atlas (width * height)")]
            public int maxAtlasPixels = 67108864;
            
            [Space(10)]
            [Header("═══════ Mip & Atlas Robustness ═══════")]
            [Tooltip("Use mip-aware padding to pick padding that is safe for mipmaps")]
            public bool useMipAwarePadding = false;
            
            [Tooltip("Attempt to reduce fragmentation by iteratively repacking atlases")]
            public bool optimizeFragmentation = true;
            
            [Tooltip("Target minimum utilization before attempting size reduction (0.0-1.0)")]
            [Range(0.5f, 0.95f)]
            public float targetUtilization = 0.9f;
            
            [Tooltip("Apply seam padding to atlases to avoid edge bleeding across UVs")]
            public bool padUVSeams = true;
            
            [Tooltip("Special handling for normal maps (ensures tangent space format)")]
            public bool preserveNormalMaps = true;
            
            [Tooltip("Detect and handle sRGB vs Linear color space automatically")]
            public bool autoDetectColorSpace = true;
            
            [Space(5)]
            [Tooltip("Apply texture filtering optimization based on content type")]
            public bool optimizeFilterModes = false;
            
            [Tooltip("Filter mode for high-detail textures (albedo, normal)")]
            public FilterMode detailTextureFilter = FilterMode.Trilinear;
            
            [Tooltip("Filter mode for low-detail textures (masks, simple patterns)")]
            public FilterMode simpleTextureFilter = FilterMode.Bilinear;
            
            [Space(5)]
            [Tooltip("Generate mipmaps for atlas textures")]
            public bool generateMipmaps = true;
            
            [Tooltip("Mipmap filter for better quality at distance")]
            public MipmapFilterMode mipmapFilter = MipmapFilterMode.Box;
            
            [Tooltip("Fade out mips to gray (helps with texture shimmer)")]
            public bool fadeOutMipmaps = false;
            
            [Range(0, 10)]
            [Tooltip("Mip level to start fade (0 = start immediately)")]
            public int mipmapFadeStart = 0;
            
            [Space(10)]
            [Header("═══════ Atlas Compression ═══════")]
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
                ASTC_4x4,
                ASTC_6x6,
                ASTC_8x8,
                ETC2_RGB,
                ETC2_RGBA,
                Uncompressed
            }
            
            [Space(5)]
            [Tooltip("Apply platform-specific compression overrides")]
            public bool usePlatformSpecificCompression = false;
            
#if UNITY_EDITOR
            [Tooltip("Standalone platform compression format")]
            public UnityEditor.TextureImporterFormat standaloneFormat = UnityEditor.TextureImporterFormat.DXT5;
            
            [Tooltip("Android platform compression format")]
            public UnityEditor.TextureImporterFormat androidFormat = UnityEditor.TextureImporterFormat.ASTC_6x6;
            
            [Tooltip("iOS platform compression format")]
            public UnityEditor.TextureImporterFormat iosFormat = UnityEditor.TextureImporterFormat.ASTC_6x6;
#endif
            
            [Space(10)]
            [Header("═══════ Advanced Quality Settings ═══════")]
            [Tooltip("Downscale textures that are significantly larger than others in the same atlas")]
            public bool normalizeTextureSizes = true;
            
            [Tooltip("Maximum size ratio between largest and smallest texture (2 = 2x difference allowed)")]
            [Range(1f, 8f)]
            public float maxTextureSizeRatio = 2f;
            
            [Space(5)]
            [Tooltip("Apply sharpening filter to downscaled textures")]
            public bool sharpenDownscaledTextures = true;
            
            [Tooltip("Sharpening strength (0-1)")]
            [Range(0f, 1f)]
            public float sharpeningStrength = 0.3f;
            
            [Space(5)]
            [Tooltip("Attempt to pack smaller textures more tightly")]
            public bool useAdvancedPacking = false;
            
            [Tooltip("Allow texture rotation for better packing (experimental, may affect UVs)")]
            public bool allowTextureRotation = false;
            
            [Space(10)]
            [Tooltip("Log detailed information about atlas generation and exclusions")]
            public bool verboseLogging = false;
        }

        [Serializable]
        public class CompressionTier
        {
            [Tooltip("Name/description for this compression tier")]
            public string tierName = "Unnamed Tier";
            
            [Tooltip("Minimum complexity score for this tier (0.0-1.0)")]
            [Range(0f, 1f)]
            public float minComplexity = 0f;
            
            [Tooltip("Maximum complexity score for this tier (0.0-1.0)")]
            [Range(0f, 1f)]
            public float maxComplexity = 1f;
            
            [Tooltip("Maximum texture size for this tier")]
            public int maxTextureSize = 2048;
            
            [Tooltip("Crunch compression quality (0-100, higher = better quality)")]
            [Range(0, 100)]
            public int crunchQuality = 75;
            
            [Tooltip("Enable this compression tier")]
            public bool enableTier = true;
            
            [Space(5)]
            [Header("Advanced Tier Settings")]
            [Tooltip("Force specific compression format (overrides automatic)")]
            public bool useCustomFormat = false;
            
#if UNITY_EDITOR
            [Tooltip("Custom compression format")]
            public UnityEditor.TextureImporterFormat customFormat = UnityEditor.TextureImporterFormat.Automatic;
#endif
            
            [Tooltip("Anisotropic filtering level (0 = disabled, 16 = max)")]
            [Range(0, 16)]
            public int anisoLevel = 1;
            
            [Tooltip("Override filter mode for this tier")]
            public bool overrideFilterMode = false;
            
            [Tooltip("Custom filter mode")]
            public FilterMode filterMode = FilterMode.Bilinear;
            
            [Tooltip("Force generate mipmaps regardless of global setting")]
            public bool forceGenerateMipmaps = false;
            
            [Tooltip("Disable mipmaps for this tier")]
            public bool disableMipmaps = false;
            
            [Space(5)]
            [Tooltip("Property name filters (comma-separated, only apply tier to matching properties, empty = all)")]
            public string propertyNameFilter = "";
            
            [Tooltip("Exclude properties matching these patterns (comma-separated)")]
            public string propertyNameExclude = "";
            
            [HideInInspector]
            public string _notes = "";
        }

        public enum MipmapFilterMode
        {
            Box,
            Kaiser
        }
    }
}
