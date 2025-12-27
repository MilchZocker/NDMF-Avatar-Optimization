#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System.Linq;

namespace MilchZocker.AvatarOptimizer
{
    [CustomEditor(typeof(AvatarOptimizer))]
    public class AvatarOptimizerEditor : Editor
    {
        private SerializedProperty boneSettingsProp;
        private SerializedProperty meshSettingsProp;
        private SerializedProperty blendshapeSettingsProp;
        private SerializedProperty atlasSettingsProp;
        private SerializedProperty statsProp;

        private bool showBoneSettings = true;
        private bool showMeshSettings = true;
        private bool showBlendshapeSettings = true;
        private bool showAtlasSettings = true;
        private bool showStats = false;
        
        // New foldouts for atlas subsections
        private bool showAdaptiveCompression = false;
        private bool showComplexityWeights = false;
        private bool showPropertyModifiers = false;
        private bool showAdvancedAtlasSettings = false;
        private bool showValidationSettings = false;

        private void OnEnable()
        {
            boneSettingsProp = serializedObject.FindProperty("boneSettings");
            meshSettingsProp = serializedObject.FindProperty("meshSettings");
            blendshapeSettingsProp = serializedObject.FindProperty("blendshapeSettings");
            atlasSettingsProp = serializedObject.FindProperty("atlasSettings");
            statsProp = serializedObject.FindProperty("stats");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Avatar Optimizer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This component optimizes your avatar during NDMF build. Configure settings below.", MessageType.Info);
            EditorGUILayout.Space(5);

            // Bone Settings
            DrawBoneSettings();
            EditorGUILayout.Space(5);

            // Mesh Settings
            DrawMeshSettings();
            EditorGUILayout.Space(5);

            // Blendshape Settings
            DrawBlendshapeSettings();
            EditorGUILayout.Space(5);

            // Atlas Settings (UPDATED)
            DrawAtlasSettings();
            EditorGUILayout.Space(5);

            // Stats
            DrawStats();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawBoneSettings()
        {
            showBoneSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showBoneSettings, "Bone Optimization");
            if (showBoneSettings && boneSettingsProp != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("removeUnusedBoneReferences"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("onlyRemoveZeroWeightBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("minimumBoneWeightThreshold"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("removeBonesWithoutWeights"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForMagicaCloth"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForDynamicBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForVRCPhysBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("manualConfirmationPerBone"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveAnimatedBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveBoneNamePatterns"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveChildrenOfUsedBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveBonesWithConstraints"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawMeshSettings()
        {
            showMeshSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showMeshSettings, "Mesh Optimization");
            if (showMeshSettings && meshSettingsProp != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeVerticesByDistance"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeDistance"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compareNormals"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("normalAngleThreshold"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compareUVs"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("uvDistanceThreshold"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("deleteLooseVertices"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("combineMeshes"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("recalculateNormals"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("recalculateTangents"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("optimizeMeshForRendering"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("applyMeshCompression"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compressionLevel"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("meshNameFilter"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("meshNameExclude"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripUnusedMeshData"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("deduplicateMaterials"));
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Advanced Mesh Options", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("optimizeIndexBuffer"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeIdenticalSubmeshes"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("intelligentAttributeStripping"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("verboseLogging"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawBlendshapeSettings()
        {
            showBlendshapeSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showBlendshapeSettings, "Blendshape Optimization");
            if (showBlendshapeSettings && blendshapeSettingsProp != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("removeUnusedBlendshapes"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("scanOverrideController"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("scanAdvancedAvatarSettings"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveBlinkBlendshapes"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveVisemeBlendshapes"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveFaceTrackingBlendshapes"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveEyeLookBlendshapes"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("removeZeroDeltaBlendshapes"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("zeroDeltaThreshold"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveBlendshapePatterns"));
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("forceRemoveBlendshapePatterns"));
                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("verboseLogging"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawAtlasSettings()
        {
            // Use regular Foldout instead of BeginFoldoutHeaderGroup to allow nesting
            EditorGUILayout.Space(2);
            var style = new GUIStyle(EditorStyles.foldoutHeader);
            showAtlasSettings = EditorGUILayout.Foldout(showAtlasSettings, "Texture Atlas Generation", true, style);
            
            if (showAtlasSettings && atlasSettingsProp != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateTextureAtlas"));
                EditorGUILayout.Space(5);

                // Animation Safety
                EditorGUILayout.LabelField("Animation Safety", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeAnimatedMaterials"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanOverrideController"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanAdvancedAvatarSettings"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeMaterialPatterns"));
                EditorGUILayout.Space(5);

                // Atlas Generation Mode
                EditorGUILayout.LabelField("Atlas Generation Mode", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useEnhancedAtlasWorkflow"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mergeIdenticalTextures"));
                EditorGUILayout.Space(5);

                // Basic Atlas Settings
                EditorGUILayout.LabelField("Basic Atlas Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxAtlasSize"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("atlasPadding"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumOutputAtlasSize"));
                EditorGUILayout.Space(5);

                EditorGUILayout.LabelField("Advanced Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumMaterialsForAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedTextureProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedTextureProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumTextureSize"));
                EditorGUILayout.Space(5);

                // Shader Filtering
                EditorGUILayout.LabelField("Shader Filtering", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedShaderNames"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedShaderNames"));
                EditorGUILayout.Space(5);

                // ===== ADAPTIVE COMPRESSION SECTION =====
                DrawAdaptiveCompressionSettings();

                // ===== ADVANCED ATLAS SETTINGS =====
                DrawAdvancedAtlasSettings();

                // ===== VALIDATION SETTINGS =====
                DrawValidationSettings();

                // Compression & Naming
                EditorGUILayout.LabelField("Atlas Compression", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("compressAtlases"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("compressionFormat"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("usePlatformSpecificCompression"));
                
                var usePlatformSpecific = atlasSettingsProp.FindPropertyRelative("usePlatformSpecificCompression");
                if (usePlatformSpecific != null && usePlatformSpecific.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("standaloneFormat"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("androidFormat"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("iosFormat"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Space(5);

                EditorGUILayout.LabelField("Atlas Naming", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("atlasNamePrefix"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includeShaderInName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includePropertyInName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("addTimestampToName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includeTierInName"));
                EditorGUILayout.Space(5);

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("verboseLogging"));
                EditorGUI.indentLevel--;
            }
            // No EndFoldoutHeaderGroup() call since we're using regular Foldout
        }

        private void DrawAdaptiveCompressionSettings()
        {
            // Draw a separator line
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            
            // Use bold foldout style
            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showAdaptiveCompression = EditorGUILayout.Foldout(showAdaptiveCompression, "⚙ Adaptive Compression Configuration", true, foldoutStyle);
            
            if (showAdaptiveCompression)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useAdaptiveCompression"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("verboseDensityLogging"));
                EditorGUILayout.Space(3);

                // Compression Tiers List
                var compressionTiersProp = atlasSettingsProp.FindPropertyRelative("compressionTiers");
                if (compressionTiersProp != null)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField("Compression Tiers (Complexity Score → Import Settings)", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(
                        "Each tier maps a complexity score range (0.0-1.0) to specific import settings. " +
                        "Lower scores = simpler textures = higher compression. Higher scores = detailed textures = lower compression.",
                        MessageType.Info);
                    
                    EditorGUILayout.PropertyField(compressionTiersProp, new GUIContent("Tiers"), true);
                }

                // Complexity Analysis Weights
                EditorGUILayout.Space(5);
                showComplexityWeights = EditorGUILayout.Foldout(showComplexityWeights, "Complexity Analysis Weights", true);
                if (showComplexityWeights)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox("Adjust how complexity is calculated from texture features.", MessageType.None);
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("colorDiversityWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("colorVarianceWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("edgeDensityWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("edgeDetectionThreshold"));
                    EditorGUI.indentLevel--;
                }

                // Property-Specific Modifiers
                EditorGUILayout.Space(5);
                showPropertyModifiers = EditorGUILayout.Foldout(showPropertyModifiers, "Property-Specific Complexity Modifiers", true);
                if (showPropertyModifiers)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox("Boost or reduce complexity scores for specific texture types.", MessageType.None);
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mainTextureComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("normalMapComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("detailTextureComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maskTextureComplexityReduction"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("emissionTextureComplexityBoost"));
                    EditorGUI.indentLevel--;
                }

                // Texture Caching
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Texture Cache & Deduplication", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("enableTextureCaching"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("deduplicateBeforeAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("persistTextureCache"));

                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
        }

        private void DrawAdvancedAtlasSettings()
        {
            // Draw a separator line
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            
            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showAdvancedAtlasSettings = EditorGUILayout.Foldout(showAdvancedAtlasSettings, "⚙ Advanced Atlas Settings", true, foldoutStyle);
            
            if (showAdvancedAtlasSettings)
            {
                EditorGUI.indentLevel++;

                // Per-Property Control
                EditorGUILayout.LabelField("Per-Property Atlas Control", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("enablePerPropertySizing"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("perPropertyAtlasSizes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("linkedAtlasProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("perPropertyCrunchQuality"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("uncompressedProperties"));
                EditorGUILayout.Space(5);

                // Mip & Robustness
                EditorGUILayout.LabelField("Mip & Atlas Robustness", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useMipAwarePadding"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("optimizeFragmentation"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("targetUtilization"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("padUVSeams"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("preserveNormalMaps"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("autoDetectColorSpace"));
                EditorGUILayout.Space(5);

                // Filter Modes
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("optimizeFilterModes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("detailTextureFilter"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("simpleTextureFilter"));
                EditorGUILayout.Space(5);

                // Mipmaps
                EditorGUILayout.LabelField("Mipmap Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateMipmaps"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mipmapFilter"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("fadeOutMipmaps"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mipmapFadeStart"));
                EditorGUILayout.Space(5);

                // Quality Settings
                EditorGUILayout.LabelField("Advanced Quality Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("normalizeTextureSizes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxTextureSizeRatio"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("sharpenDownscaledTextures"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("sharpeningStrength"));
                EditorGUILayout.Space(5);

                // Packing
                EditorGUILayout.LabelField("Packing Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useAdvancedPacking"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowTextureRotation"));
                
                var allowRotation = atlasSettingsProp.FindPropertyRelative("allowTextureRotation");
                if (allowRotation != null && allowRotation.boolValue)
                {
                    EditorGUILayout.HelpBox("WARNING: Texture rotation is experimental and may break UVs!", MessageType.Warning);
                }

                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
        }

        private void DrawValidationSettings()
        {
            // Draw a separator line
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            
            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showValidationSettings = EditorGUILayout.Foldout(showValidationSettings, "⚙ Safety & Validation", true, foldoutStyle);
            
            if (showValidationSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("UV Validation", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("validateUVBounds"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("warnOnInvalidUVs"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("autoFixInvalidUVs"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("skipInvalidUVMaterials"));
                EditorGUILayout.Space(5);

                EditorGUILayout.LabelField("Atlas Limits", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxMaterialsPerAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("limitAtlasPixelCount"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxAtlasPixels"));

                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
        }

        private void DrawStats()
        {
            showStats = EditorGUILayout.BeginFoldoutHeaderGroup(showStats, "Debug & Statistics");
            if (showStats)
            {
                EditorGUI.indentLevel++;
                
                if (statsProp != null)
                {
                    DrawStatInt("Bones Removed", statsProp.FindPropertyRelative("bonesRemoved"));
                    DrawStatInt("Bone References Removed", statsProp.FindPropertyRelative("boneReferencesRemoved"));
                    DrawStatInt("Blendshapes Removed", statsProp.FindPropertyRelative("blendshapesRemoved"));
                    DrawStatInt("Vertices Merged", statsProp.FindPropertyRelative("verticesMerged"));
                    DrawStatInt("Loose Vertices Removed", statsProp.FindPropertyRelative("looseVerticesRemoved"));
                    DrawStatInt("Meshes Combined", statsProp.FindPropertyRelative("meshesCombined"));
                    DrawStatInt("Atlases Generated", statsProp.FindPropertyRelative("atlasesGenerated"));
                    
                    var timeProp = statsProp.FindPropertyRelative("optimizationTimeSeconds");
                    if (timeProp != null)
                        EditorGUILayout.LabelField("Optimization Time (s)", timeProp.floatValue.ToString("F2"));
                }
                else
                {
                    // Fallback
                    var opt = (AvatarOptimizer)target;
                    var s = opt.stats;
                    EditorGUILayout.LabelField("Bones Removed", s.bonesRemoved.ToString());
                    EditorGUILayout.LabelField("Bone References Removed", s.boneReferencesRemoved.ToString());
                    EditorGUILayout.LabelField("Blendshapes Removed", s.blendshapesRemoved.ToString());
                    EditorGUILayout.LabelField("Vertices Merged", s.verticesMerged.ToString());
                    EditorGUILayout.LabelField("Loose Vertices Removed", s.looseVerticesRemoved.ToString());
                    EditorGUILayout.LabelField("Meshes Combined", s.meshesCombined.ToString());
                    EditorGUILayout.LabelField("Atlases Generated", s.atlasesGenerated.ToString());
                    EditorGUILayout.LabelField("Optimization Time (s)", s.optimizationTimeSeconds.ToString("F2"));
                }
                
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawStatInt(string label, SerializedProperty prop)
        {
            if (prop == null) return;
            EditorGUILayout.LabelField(label, prop.intValue.ToString());
        }
    }
}

#endif
