#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

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

        // Main section foldouts
        private bool showBoneSettings = true;
        private bool showMeshSettings = true;
        private bool showBlendshapeSettings = true;
        private bool showAtlasSettings = true;
        private bool showStats = true;
        private bool showEstimates = true;

        // Atlas subsection foldouts
        private bool showAdaptiveCompression = false;
        private bool showComplexityWeights = false;
        private bool showPropertyModifiers = false;
        private bool showAdvancedAtlasSettings = false;
        private bool showValidationSettings = false;
        private bool showTextureCaching = false;
        private bool showPerPropertyControl = false;
        private bool showCompressionSettings = false;
        private bool showNamingSettings = false;
        private bool showMipmapSettings = false;

        // Cached estimation data
        private EstimationData cachedEstimates;
        private bool estimatesNeedUpdate = true;
        private Vector2 statsScrollPos;

        // Color scheme matching CVRMergeArmatureEditor
        private static readonly Color headerColor = new Color(0.8f, 0.9f, 1f, 0.3f);
        private static readonly Color sectionColor = new Color(0.9f, 0.95f, 1f, 0.2f);
        private static readonly Color infoColor = new Color(0.85f, 0.95f, 1f);
        private static readonly Color warningColor = new Color(1f, 0.92f, 0.8f);
        private static readonly Color successColor = new Color(0.7f, 1f, 0.7f);
        private static readonly Color estimateColor = new Color(0.9f, 1f, 0.9f);

        private class EstimationData
        {
            // Before optimization
            public long currentMemoryBytes;
            public int currentTextureCount;
            public int currentMaterialCount;
            public int currentMeshCount;
            public int currentVertexCount;
            public int currentTriangleCount;
            public int currentBoneCount;
            public int currentBlendshapeCount;

            // Estimations
            public int estimatedBonesRemovable;
            public int estimatedBlendshapesRemovable;
            public int estimatedVerticesMergeable;
            public int estimatedMeshesCombineable;
            public int estimatedAtlasesGenerable;
            public long estimatedTextureSavingsBytes;
            public float estimatedCompressionRatio; // 0-1, percentage saved

            // After optimization (populated post-build)
            public long optimizedMemoryBytes;
            public long actualSavingsBytes;
            public float actualCompressionRatio;
        }

        private void OnEnable()
        {
            boneSettingsProp = serializedObject.FindProperty("boneSettings");
            meshSettingsProp = serializedObject.FindProperty("meshSettings");
            blendshapeSettingsProp = serializedObject.FindProperty("blendshapeSettings");
            atlasSettingsProp = serializedObject.FindProperty("atlasSettings");
            statsProp = serializedObject.FindProperty("stats");

            estimatesNeedUpdate = true;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            // Header Banner
            DrawBanner();

            EditorGUILayout.Space(8);

            // Estimation/Statistics Display
            DrawEstimationsAndStats();

            EditorGUILayout.Space(8);

            // Main Settings Sections
            DrawBoneSettings();
            EditorGUILayout.Space(5);

            DrawMeshSettings();
            EditorGUILayout.Space(5);

            DrawBlendshapeSettings();
            EditorGUILayout.Space(5);

            DrawAtlasSettings();

            if (EditorGUI.EndChangeCheck())
            {
                estimatesNeedUpdate = true;
            }

            serializedObject.ApplyModifiedProperties();
        }

        #region Banner & Header

        private void DrawBanner()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = headerColor;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            GUILayout.Space(2);
            
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("⚡ Avatar Optimizer", titleStyle);
            
            GUILayout.Space(2);
            
            var subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Italic
            };
            EditorGUILayout.LabelField("NDMF-based avatar optimization system", subtitleStyle);
            
            GUILayout.Space(2);
            
            EditorGUILayout.EndVertical();
        }

        private void DrawSectionHeader(string title, Color? customColor = null)
        {
            GUILayout.Space(2);
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = customColor ?? headerColor;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;
            
            GUILayout.Label(title, EditorStyles.boldLabel);
            
            EditorGUILayout.EndVertical();
        }

        private void DrawSubsectionLabel(string label)
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11
            };
            EditorGUILayout.LabelField(label, style);
        }

        #endregion

        #region Estimations & Statistics

        private void DrawEstimationsAndStats()
        {
            var opt = (AvatarOptimizer)target;
            var avatarRoot = opt.GetComponent<Transform>();

            if (avatarRoot == null)
            {
                EditorGUILayout.HelpBox("Avatar root not found. Attach to avatar root GameObject.", MessageType.Warning);
                return;
            }

            var originalColor = GUI.backgroundColor;
            
            // Check if optimization has run
            bool hasRunOptimization = statsProp != null && 
                                     statsProp.FindPropertyRelative("optimizationTimeSeconds").floatValue > 0;

            if (hasRunOptimization)
            {
                // Show actual results after optimization
                DrawActualStatistics(opt);
            }
            else
            {
                // Show estimations before optimization
                DrawEstimations(avatarRoot, opt);
            }

            GUI.backgroundColor = originalColor;
        }

        private void DrawEstimations(Transform avatarRoot, AvatarOptimizer opt)
        {
            if (estimatesNeedUpdate || cachedEstimates == null)
            {
                cachedEstimates = GatherEstimations(avatarRoot, opt);
                estimatesNeedUpdate = false;
            }

            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = estimateColor;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            EditorGUILayout.BeginHorizontal();
            showEstimates = EditorGUILayout.Foldout(showEstimates, "📊 Pre-Optimization Analysis", true, EditorStyles.foldoutHeader);
            
            if (GUILayout.Button("🔄", GUILayout.Width(30), GUILayout.Height(18)))
            {
                estimatesNeedUpdate = true;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            if (showEstimates && cachedEstimates != null)
            {
                EditorGUILayout.Space(3);
                
                statsScrollPos = EditorGUILayout.BeginScrollView(statsScrollPos, GUILayout.MaxHeight(400));
                
                EditorGUI.indentLevel++;

                // Current State Overview
                DrawEstimateSection("🎯 Current Avatar State", new Color(0.95f, 0.95f, 1f), () =>
                {
                    DrawEstimateStat("Total Meshes", cachedEstimates.currentMeshCount);
                    DrawEstimateStat("Total Vertices", cachedEstimates.currentVertexCount.ToString("N0"));
                    DrawEstimateStat("Total Triangles", cachedEstimates.currentTriangleCount.ToString("N0"));
                    DrawEstimateStat("Total Bones", cachedEstimates.currentBoneCount);
                    DrawEstimateStat("Total Blendshapes", cachedEstimates.currentBlendshapeCount);
                    DrawEstimateStat("Total Materials", cachedEstimates.currentMaterialCount);
                    DrawEstimateStat("Total Textures", cachedEstimates.currentTextureCount);
                    DrawEstimateStat("Est. Memory Usage", FormatBytes(cachedEstimates.currentMemoryBytes));
                });

                EditorGUILayout.Space(5);

                // Estimated Reductions
                DrawEstimateSection("📉 Estimated Reductions", successColor, () =>
                {
                    if (opt.boneSettings.removeUnusedBoneReferences || opt.boneSettings.removeBonesWithoutWeights)
                    {
                        DrawEstimateReduction("Bones Removable", cachedEstimates.estimatedBonesRemovable, 
                                             cachedEstimates.currentBoneCount);
                    }

                    if (opt.blendshapeSettings.removeUnusedBlendshapes)
                    {
                        DrawEstimateReduction("Blendshapes Removable", cachedEstimates.estimatedBlendshapesRemovable,
                                             cachedEstimates.currentBlendshapeCount);
                    }

                    if (opt.meshSettings.mergeVerticesByDistance)
                    {
                        DrawEstimateReduction("Vertices Mergeable", cachedEstimates.estimatedVerticesMergeable,
                                             cachedEstimates.currentVertexCount);
                    }

                    if (opt.meshSettings.combineMeshes)
                    {
                        DrawEstimateReduction("Meshes Combineable", cachedEstimates.estimatedMeshesCombineable,
                                             cachedEstimates.currentMeshCount);
                    }

                    if (opt.atlasSettings.generateTextureAtlas && cachedEstimates.estimatedAtlasesGenerable > 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Potential Atlases", GUILayout.Width(180));
                        var style = new GUIStyle(EditorStyles.boldLabel);
                        style.normal.textColor = new Color(0.2f, 0.8f, 0.2f);
                        EditorGUILayout.LabelField(cachedEstimates.estimatedAtlasesGenerable.ToString(), style);
                        EditorGUILayout.EndHorizontal();

                        DrawEstimateStat("Est. Texture Savings", FormatBytes(cachedEstimates.estimatedTextureSavingsBytes));
                    }
                });

                EditorGUILayout.Space(5);

                // Compression Summary
                DrawEstimateSection("💾 Estimated Compression", new Color(1f, 0.95f, 0.9f), () =>
                {
                    float compressionPercent = cachedEstimates.estimatedCompressionRatio * 100f;
                    
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Estimated Savings", GUILayout.Width(180));
                    
                    var percentStyle = new GUIStyle(EditorStyles.boldLabel);
                    percentStyle.fontSize = 16;
                    if (compressionPercent > 50f)
                        percentStyle.normal.textColor = new Color(0.1f, 0.8f, 0.1f);
                    else if (compressionPercent > 25f)
                        percentStyle.normal.textColor = new Color(0.6f, 0.8f, 0.2f);
                    else
                        percentStyle.normal.textColor = new Color(0.8f, 0.6f, 0.2f);
                    
                    EditorGUILayout.LabelField($"{compressionPercent:F1}%", percentStyle);
                    EditorGUILayout.EndHorizontal();

                    long estimatedFinalSize = cachedEstimates.currentMemoryBytes - 
                                             (long)(cachedEstimates.currentMemoryBytes * cachedEstimates.estimatedCompressionRatio);
                    
                    DrawEstimateStat("Current Size", FormatBytes(cachedEstimates.currentMemoryBytes));
                    DrawEstimateStat("Estimated Final", FormatBytes(estimatedFinalSize));
                    DrawEstimateStat("Estimated Reduction", FormatBytes((long)(cachedEstimates.currentMemoryBytes * cachedEstimates.estimatedCompressionRatio)));
                });

                EditorGUI.indentLevel--;
                
                EditorGUILayout.EndScrollView();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActualStatistics(AvatarOptimizer opt)
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            EditorGUILayout.BeginHorizontal();
            showStats = EditorGUILayout.Foldout(showStats, "✅ Optimization Results", true, EditorStyles.foldoutHeader);
            EditorGUILayout.EndHorizontal();

            if (showStats)
            {
                EditorGUILayout.Space(3);
                
                statsScrollPos = EditorGUILayout.BeginScrollView(statsScrollPos, GUILayout.MaxHeight(400));
                
                EditorGUI.indentLevel++;

                // Actual Operations Performed
                DrawEstimateSection("🔧 Operations Performed", new Color(0.9f, 0.95f, 1f), () =>
                {
                    if (statsProp != null)
                    {
                        DrawActualStat("Bones Removed", statsProp.FindPropertyRelative("bonesRemoved"));
                        DrawActualStat("Bone Refs Removed", statsProp.FindPropertyRelative("boneReferencesRemoved"));
                        DrawActualStat("Blendshapes Removed", statsProp.FindPropertyRelative("blendshapesRemoved"));
                        DrawActualStat("Vertices Merged", statsProp.FindPropertyRelative("verticesMerged"));
                        DrawActualStat("Loose Verts Removed", statsProp.FindPropertyRelative("looseVerticesRemoved"));
                        DrawActualStat("Meshes Combined", statsProp.FindPropertyRelative("meshesCombined"));
                        DrawActualStat("Atlases Generated", statsProp.FindPropertyRelative("atlasesGenerated"));
                    }
                });

                EditorGUILayout.Space(5);

                // Memory Savings (if we have cached estimates)
                if (cachedEstimates != null)
                {
                    DrawEstimateSection("💾 Memory Impact", successColor, () =>
                    {
                        DrawEstimateStat("Before Optimization", FormatBytes(cachedEstimates.currentMemoryBytes));
                        
                        // Calculate actual savings based on texture memory saved
                        long actualSavings = 0;
                        if (statsProp != null)
                        {
                            var texMemProp = statsProp.FindPropertyRelative("textureMemorySavedMB");
                            if (texMemProp != null)
                                actualSavings = (long)texMemProp.intValue * 1024L * 1024L;
                        }

                        long optimizedSize = cachedEstimates.currentMemoryBytes - actualSavings;
                        float actualCompressionPercent = (actualSavings / (float)cachedEstimates.currentMemoryBytes) * 100f;

                        DrawEstimateStat("After Optimization", FormatBytes(optimizedSize));
                        DrawEstimateStat("Actual Savings", FormatBytes(actualSavings));

                        EditorGUILayout.Space(3);
                        
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Compression Achieved", GUILayout.Width(180));
                        
                        var percentStyle = new GUIStyle(EditorStyles.boldLabel);
                        percentStyle.fontSize = 16;
                        percentStyle.normal.textColor = new Color(0.1f, 0.8f, 0.1f);
                        
                        EditorGUILayout.LabelField($"{actualCompressionPercent:F1}%", percentStyle);
                        EditorGUILayout.EndHorizontal();

                        // Compare to estimate
                        if (cachedEstimates.estimatedCompressionRatio > 0)
                        {
                            float estimatedPercent = cachedEstimates.estimatedCompressionRatio * 100f;
                            float difference = actualCompressionPercent - estimatedPercent;
                            
                            EditorGUILayout.Space(2);
                            string comparisonText = difference > 0 
                                ? $"({difference:+F1}% better than estimated)" 
                                : $"({-difference:F1}% less than estimated)";
                            
                            var compStyle = new GUIStyle(EditorStyles.miniLabel);
                            compStyle.fontStyle = FontStyle.Italic;
                            compStyle.normal.textColor = difference > 0 
                                ? new Color(0.2f, 0.8f, 0.2f) 
                                : new Color(0.8f, 0.6f, 0.2f);
                            
                            EditorGUILayout.LabelField(comparisonText, compStyle);
                        }
                    });

                    EditorGUILayout.Space(5);
                }

                // Performance Info
                if (statsProp != null)
                {
                    var timeProp = statsProp.FindPropertyRelative("optimizationTimeSeconds");
                    if (timeProp != null && timeProp.floatValue > 0)
                    {
                        EditorGUILayout.Space(3);
                        var timeStyle = new GUIStyle(EditorStyles.boldLabel);
                        timeStyle.normal.textColor = new Color(0.2f, 0.6f, 0.8f);
                        EditorGUILayout.LabelField($"⏱️ Build Time: {timeProp.floatValue:F2}s", timeStyle);
                    }
                }

                EditorGUI.indentLevel--;
                
                EditorGUILayout.EndScrollView();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEstimateSection(string title, Color bgColor, System.Action content)
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            DrawSubsectionLabel(title);
            EditorGUI.indentLevel++;
            content?.Invoke();
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private void DrawEstimateStat(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEstimateStat(string label, int value)
        {
            DrawEstimateStat(label, value.ToString("N0"));
        }

        private void DrawEstimateReduction(string label, int removable, int total)
        {
            float percent = total > 0 ? (removable / (float)total) * 100f : 0f;
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            
            var style = new GUIStyle(EditorStyles.boldLabel);
            if (percent > 50f)
                style.normal.textColor = new Color(0.2f, 0.8f, 0.2f);
            else if (percent > 25f)
                style.normal.textColor = new Color(0.6f, 0.8f, 0.2f);
            else
                style.normal.textColor = Color.white;
            
            EditorGUILayout.LabelField($"{removable} / {total} ({percent:F1}%)", style);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawActualStat(string label, SerializedProperty prop)
        {
            if (prop == null) return;
            
            int value = prop.intValue;
            var color = value > 0 ? new Color(0.6f, 1f, 0.6f) : Color.white;
            var prevColor = GUI.contentColor;
            GUI.contentColor = color;
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            EditorGUILayout.LabelField(value.ToString("N0"), EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            
            GUI.contentColor = prevColor;
        }

        private EstimationData GatherEstimations(Transform avatarRoot, AvatarOptimizer opt)
        {
            var data = new EstimationData();

            // Gather current state
            var meshRenderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshFilters = avatarRoot.GetComponentsInChildren<MeshFilter>(true);
            
            HashSet<Material> uniqueMaterials = new HashSet<Material>();
            HashSet<Texture> uniqueTextures = new HashSet<Texture>();
            
            foreach (var smr in meshRenderers)
            {
                if (smr.sharedMesh != null)
                {
                    data.currentMeshCount++;
                    data.currentVertexCount += smr.sharedMesh.vertexCount;
                    data.currentTriangleCount += smr.sharedMesh.triangles.Length / 3;
                    data.currentBlendshapeCount += smr.sharedMesh.blendShapeCount;
                    
                    // Estimate removable bones
                    if (opt.boneSettings.removeUnusedBoneReferences && smr.bones != null)
                    {
                        var usedBones = GetUsedBoneCount(smr.sharedMesh);
                        data.estimatedBonesRemovable += smr.bones.Length - usedBones;
                    }
                }

                if (smr.sharedMaterials != null)
                {
                    foreach (var mat in smr.sharedMaterials)
                    {
                        if (mat != null)
                        {
                            uniqueMaterials.Add(mat);
                            CollectTextures(mat, uniqueTextures);
                        }
                    }
                }
            }

            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null)
                {
                    data.currentMeshCount++;
                    data.currentVertexCount += mf.sharedMesh.vertexCount;
                    data.currentTriangleCount += mf.sharedMesh.triangles.Length / 3;
                }
            }

            // Count bones
            data.currentBoneCount = avatarRoot.GetComponentsInChildren<Transform>(true).Length;

            // Estimate mergeable vertices
            if (opt.meshSettings.mergeVerticesByDistance)
            {
                data.estimatedVerticesMergeable = (int)(data.currentVertexCount * 0.15f); // ~15% typical
            }

            // Estimate combineable meshes
            if (opt.meshSettings.combineMeshes)
            {
                var materialGroups = meshRenderers.GroupBy(r => string.Join(",", r.sharedMaterials.Select(m => m?.name ?? "")));
                data.estimatedMeshesCombineable = meshRenderers.Length - materialGroups.Count();
            }

            // Estimate removable blendshapes (conservative estimate)
            if (opt.blendshapeSettings.removeUnusedBlendshapes)
            {
                data.estimatedBlendshapesRemovable = (int)(data.currentBlendshapeCount * 0.3f); // ~30% typical
            }

            // Count materials and textures
            data.currentMaterialCount = uniqueMaterials.Count;
            data.currentTextureCount = uniqueTextures.Count;

            // Estimate texture atlasing
            if (opt.atlasSettings.generateTextureAtlas)
            {
                var shaderGroups = uniqueMaterials.GroupBy(m => m.shader);
                foreach (var group in shaderGroups)
                {
                    if (group.Count() >= opt.atlasSettings.minimumMaterialsForAtlas)
                    {
                        data.estimatedAtlasesGenerable++;
                    }
                }
            }

            // Calculate memory usage
            foreach (var tex in uniqueTextures)
            {
                if (tex != null)
                {
                    data.currentMemoryBytes += EstimateTextureSize(tex as Texture2D);
                }
            }

            // Add mesh memory
            data.currentMemoryBytes += data.currentVertexCount * 48; // ~48 bytes per vertex average

            // Estimate texture savings from atlasing
            if (data.estimatedAtlasesGenerable > 0)
            {
                // Atlasing typically saves 40-60% of texture memory
                data.estimatedTextureSavingsBytes = (long)(data.currentMemoryBytes * 0.5f * (data.estimatedAtlasesGenerable / (float)uniqueTextures.Count));
            }

            // Calculate overall compression ratio
            long totalEstimatedSavings = data.estimatedTextureSavingsBytes;
            totalEstimatedSavings += data.estimatedVerticesMergeable * 48; // Vertex memory
            
            data.estimatedCompressionRatio = data.currentMemoryBytes > 0 
                ? totalEstimatedSavings / (float)data.currentMemoryBytes 
                : 0f;

            return data;
        }

        private int GetUsedBoneCount(Mesh mesh)
        {
            if (mesh.boneWeights == null || mesh.boneWeights.Length == 0)
                return 0;

            HashSet<int> usedBones = new HashSet<int>();
            foreach (var weight in mesh.boneWeights)
            {
                if (weight.weight0 > 0.0001f) usedBones.Add(weight.boneIndex0);
                if (weight.weight1 > 0.0001f) usedBones.Add(weight.boneIndex1);
                if (weight.weight2 > 0.0001f) usedBones.Add(weight.boneIndex2);
                if (weight.weight3 > 0.0001f) usedBones.Add(weight.boneIndex3);
            }
            return usedBones.Count;
        }

        private void CollectTextures(Material mat, HashSet<Texture> textures)
        {
            if (mat == null || mat.shader == null) return;

            for (int i = 0; i < ShaderUtil.GetPropertyCount(mat.shader); i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                    var tex = mat.GetTexture(propName);
                    if (tex != null)
                        textures.Add(tex);
                }
            }
        }

        private long EstimateTextureSize(Texture2D tex)
        {
            if (tex == null) return 0;
            
            // Rough estimation based on resolution and format
            int pixels = tex.width * tex.height;
            int bytesPerPixel = 4; // RGBA32 baseline
            
            // Adjust for common formats
            var format = tex.format;
            if (format == TextureFormat.DXT1 || format == TextureFormat.BC4)
                bytesPerPixel = 1;
            else if (format == TextureFormat.DXT5 || format == TextureFormat.BC7)
                bytesPerPixel = 1;
            else if (format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32)
                bytesPerPixel = 4;
            
            long size = pixels * bytesPerPixel;
            
            // Add mipmap overhead (~33%)
            if (tex.mipmapCount > 1)
                size = (long)(size * 1.33f);
            
            return size;
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            else if (bytes < 1024 * 1024)
                return $"{bytes / 1024f:F1} KB";
            else if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024f * 1024f):F1} MB";
            else
                return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
        }

        #endregion

        #region Bone Settings

        private void DrawBoneSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.95f, 0.95f);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            showBoneSettings = EditorGUILayout.Foldout(showBoneSettings, "🦴 Bone Optimization", true, EditorStyles.foldoutHeader);

            if (showBoneSettings)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                DrawSubsectionLabel("Bone Reference Cleanup");
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("removeUnusedBoneReferences"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("onlyRemoveZeroWeightBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("minimumBoneWeightThreshold"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Bone Removal");
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("removeBonesWithoutWeights"));
                
                if (boneSettingsProp.FindPropertyRelative("removeBonesWithoutWeights").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox("Physics component detection for safety", MessageType.Info);
                    EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForMagicaCloth"));
                    EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForDynamicBones"));
                    EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("checkForVRCPhysBones"));
                    EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("manualConfirmationPerBone"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Preservation Rules");
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveAnimatedBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveBoneNamePatterns"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveChildrenOfUsedBones"));
                EditorGUILayout.PropertyField(boneSettingsProp.FindPropertyRelative("preserveBonesWithConstraints"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Mesh Settings

        private void DrawMeshSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.95f, 1f, 0.95f);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            showMeshSettings = EditorGUILayout.Foldout(showMeshSettings, "🔺 Mesh Optimization", true, EditorStyles.foldoutHeader);

            if (showMeshSettings)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                DrawSubsectionLabel("Vertex Optimization");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeVerticesByDistance"));
                
                if (meshSettingsProp.FindPropertyRelative("mergeVerticesByDistance").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeDistance"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compareNormals"));
                    if (meshSettingsProp.FindPropertyRelative("compareNormals").boolValue)
                        EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("normalAngleThreshold"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compareUVs"));
                    if (meshSettingsProp.FindPropertyRelative("compareUVs").boolValue)
                        EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("uvDistanceThreshold"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("deleteLooseVertices"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Mesh Operations");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("combineMeshes"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("recalculateNormals"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("recalculateTangents"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Compression & Optimization");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("optimizeMeshForRendering"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("applyMeshCompression"));
                if (meshSettingsProp.FindPropertyRelative("applyMeshCompression").boolValue)
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("compressionLevel"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Filtering");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("meshNameFilter"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("meshNameExclude"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Additional Options");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripUnusedMeshData"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("deduplicateMaterials"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("optimizeIndexBuffer"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeIdenticalSubmeshes"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("intelligentAttributeStripping"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("verboseLogging"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Blendshape Settings

        private void DrawBlendshapeSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.95f, 0.95f, 1f);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            showBlendshapeSettings = EditorGUILayout.Foldout(showBlendshapeSettings, "😊 Blendshape Optimization", true, EditorStyles.foldoutHeader);

            if (showBlendshapeSettings)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("removeUnusedBlendshapes"));

                if (blendshapeSettingsProp.FindPropertyRelative("removeUnusedBlendshapes").boolValue)
                {
                    EditorGUILayout.Space(5);
                    DrawSubsectionLabel("Animation Scanning");
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("scanOverrideController"));
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("scanAdvancedAvatarSettings"));

                    EditorGUILayout.Space(5);
                    DrawSubsectionLabel("CVR Blendshape Preservation");
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveBlinkBlendshapes"));
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveVisemeBlendshapes"));
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveFaceTrackingBlendshapes"));
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveEyeLookBlendshapes"));

                    EditorGUILayout.Space(5);
                    DrawSubsectionLabel("Zero Delta Detection");
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("removeZeroDeltaBlendshapes"));
                    if (blendshapeSettingsProp.FindPropertyRelative("removeZeroDeltaBlendshapes").boolValue)
                        EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("zeroDeltaThreshold"));

                    EditorGUILayout.Space(5);
                    DrawSubsectionLabel("Pattern Filtering");
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("preserveBlendshapePatterns"));
                    EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("forceRemoveBlendshapePatterns"));
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("verboseLogging"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Atlas Settings

        private void DrawAtlasSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.98f, 0.9f);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            showAtlasSettings = EditorGUILayout.Foldout(showAtlasSettings, "🎨 Texture Atlas Generation", true, EditorStyles.foldoutHeader);

            if (showAtlasSettings)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateTextureAtlas"));

                if (atlasSettingsProp.FindPropertyRelative("generateTextureAtlas").boolValue)
                {
                    EditorGUILayout.Space(5);
                    
                    // Animation Safety
                    DrawAtlasSubsection("Animation Safety", new Color(0.95f, 0.9f, 1f), () =>
                    {
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeAnimatedMaterials"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanOverrideController"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanAdvancedAvatarSettings"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeMaterialPatterns"));
                    });

                    EditorGUILayout.Space(5);

                    // Atlas Generation Mode
                    DrawAtlasSubsection("Atlas Generation Mode", new Color(0.9f, 1f, 1f), () =>
                    {
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useEnhancedAtlasWorkflow"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mergeIdenticalTextures"));
                    });

                    EditorGUILayout.Space(5);

                    // Basic Atlas Settings
                    DrawAtlasSubsection("Basic Atlas Settings", infoColor, () =>
                    {
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxAtlasSize"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("atlasPadding"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumOutputAtlasSize"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumMaterialsForAtlas"));
                    });

                    EditorGUILayout.Space(5);

                    // Property Filtering
                    DrawAtlasSubsection("Property Filtering", new Color(1f, 1f, 0.9f), () =>
                    {
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedTextureProperties"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedTextureProperties"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumTextureSize"));
                    });

                    EditorGUILayout.Space(5);

                    // Shader Filtering
                    DrawAtlasSubsection("Shader Filtering", new Color(0.9f, 1f, 0.95f), () =>
                    {
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedShaderNames"));
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedShaderNames"));
                    });

                    // Advanced sections with foldouts
                    EditorGUILayout.Space(5);
                    DrawTextureCachingSettings();

                    EditorGUILayout.Space(5);
                    DrawPerPropertyControlSettings();

                    EditorGUILayout.Space(5);
                    DrawAdaptiveCompressionSettings();

                    EditorGUILayout.Space(5);
                    DrawAdvancedAtlasSettings();

                    EditorGUILayout.Space(5);
                    DrawValidationSettings();

                    EditorGUILayout.Space(5);
                    DrawCompressionSettings();

                    EditorGUILayout.Space(5);
                    DrawNamingSettings();

                    EditorGUILayout.Space(5);
                    DrawMipmapSettings();

                    EditorGUILayout.Space(5);
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("verboseLogging"));
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAtlasSubsection(string title, Color bgColor, System.Action content)
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            DrawSubsectionLabel(title);
            EditorGUI.indentLevel++;
            content?.Invoke();
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private void DrawTextureCachingSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showTextureCaching = EditorGUILayout.Foldout(showTextureCaching, "💾 Texture Caching & Deduplication", true, foldoutStyle);

            if (showTextureCaching)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("enableTextureCaching"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("deduplicateBeforeAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("persistTextureCache"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawPerPropertyControlSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showPerPropertyControl = EditorGUILayout.Foldout(showPerPropertyControl, "🎛️ Per-Property Control", true, foldoutStyle);

            if (showPerPropertyControl)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("enablePerPropertySizing"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("perPropertyAtlasSizes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("linkedAtlasProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("perPropertyCrunchQuality"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("uncompressedProperties"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawAdaptiveCompressionSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showAdaptiveCompression = EditorGUILayout.Foldout(showAdaptiveCompression, "⚙️ Adaptive Compression", true, foldoutStyle);

            if (showAdaptiveCompression)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useAdaptiveCompression"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("verboseDensityLogging"));

                EditorGUILayout.Space(3);

                var compressionTiersProp = atlasSettingsProp.FindPropertyRelative("compressionTiers");
                if (compressionTiersProp != null)
                {
                    EditorGUILayout.PropertyField(compressionTiersProp, new GUIContent("Compression Tiers"), true);
                }

                EditorGUILayout.Space(5);
                showComplexityWeights = EditorGUILayout.Foldout(showComplexityWeights, "Complexity Weights", true);
                if (showComplexityWeights)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("colorDiversityWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("colorVarianceWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("edgeDensityWeight"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("edgeDetectionThreshold"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(5);
                showPropertyModifiers = EditorGUILayout.Foldout(showPropertyModifiers, "Property Modifiers", true);
                if (showPropertyModifiers)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mainTextureComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("normalMapComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("detailTextureComplexityBoost"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maskTextureComplexityReduction"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("emissionTextureComplexityBoost"));
                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawAdvancedAtlasSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showAdvancedAtlasSettings = EditorGUILayout.Foldout(showAdvancedAtlasSettings, "🔧 Advanced Settings", true, foldoutStyle);

            if (showAdvancedAtlasSettings)
            {
                EditorGUI.indentLevel++;

                DrawSubsectionLabel("Mip & Robustness");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useMipAwarePadding"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("optimizeFragmentation"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("targetUtilization"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("padUVSeams"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("preserveNormalMaps"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("autoDetectColorSpace"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Filter Modes");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("optimizeFilterModes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("detailTextureFilter"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("simpleTextureFilter"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Quality");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("normalizeTextureSizes"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxTextureSizeRatio"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("sharpenDownscaledTextures"));
                if (atlasSettingsProp.FindPropertyRelative("sharpenDownscaledTextures").boolValue)
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("sharpeningStrength"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Packing");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useAdvancedPacking"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowTextureRotation"));

                EditorGUI.indentLevel--;
            }
        }

        private void DrawValidationSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showValidationSettings = EditorGUILayout.Foldout(showValidationSettings, "🛡️ Safety Validation", true, foldoutStyle);

            if (showValidationSettings)
            {
                EditorGUI.indentLevel++;

                DrawSubsectionLabel("UV Validation");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("validateUVBounds"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("warnOnInvalidUVs"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("autoFixInvalidUVs"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("skipInvalidUVMaterials"));

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Atlas Limits");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxMaterialsPerAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("limitAtlasPixelCount"));
                if (atlasSettingsProp.FindPropertyRelative("limitAtlasPixelCount").boolValue)
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxAtlasPixels"));

                EditorGUI.indentLevel--;
            }
        }

        private void DrawCompressionSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showCompressionSettings = EditorGUILayout.Foldout(showCompressionSettings, "🗜️ Atlas Compression", true, foldoutStyle);

            if (showCompressionSettings)
            {
                EditorGUI.indentLevel++;

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

                EditorGUI.indentLevel--;
            }
        }

        private void DrawNamingSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showNamingSettings = EditorGUILayout.Foldout(showNamingSettings, "📝 Atlas Naming", true, foldoutStyle);

            if (showNamingSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("atlasNamePrefix"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includeShaderInName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includePropertyInName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("addTimestampToName"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includeTierInName"));

                EditorGUI.indentLevel--;
            }
        }

        private void DrawMipmapSettings()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            showMipmapSettings = EditorGUILayout.Foldout(showMipmapSettings, "🔍 Mipmap Settings", true, foldoutStyle);

            if (showMipmapSettings)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateMipmaps"));
                if (atlasSettingsProp.FindPropertyRelative("generateMipmaps").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mipmapFilter"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("fadeOutMipmaps"));
                    if (atlasSettingsProp.FindPropertyRelative("fadeOutMipmaps").boolValue)
                        EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mipmapFadeStart"));
                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }
        }

        #endregion
    }
}

#endif
