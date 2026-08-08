#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System;
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
        private SerializedProperty animatorSettingsProp;
        private SerializedProperty physicsSettingsProp;
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

        private bool showMaterialsByShader = false;
        private Dictionary<string, bool> shaderFoldoutStates = new Dictionary<string, bool>();

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

            // Material atlasing analysis
            public class MaterialAtlasInfo
            {
                public Material material;
                public string materialName;
                public string shaderName;
                public bool canAtlas;
                public List<string> reasons = new List<string>();
                public List<string> compatibleProperties = new List<string>();
                public List<string> incompatibleProperties = new List<string>();
                public int groupId = -1;
            }

            public List<MaterialAtlasInfo> atlasingMaterials = new List<MaterialAtlasInfo>();
            public int totalAtlasableMaterials = 0;
            public int totalExcludedMaterials = 0;
            public Dictionary<string, List<MaterialAtlasInfo>> materialsByShader = new Dictionary<string, List<MaterialAtlasInfo>>();
            public int estimatedAtlasGroups = 0;
        }

        private void OnEnable()
        {
            boneSettingsProp = serializedObject.FindProperty("boneSettings");
            meshSettingsProp = serializedObject.FindProperty("meshSettings");
            blendshapeSettingsProp = serializedObject.FindProperty("blendshapeSettings");
            atlasSettingsProp = serializedObject.FindProperty("atlasSettings");
            animatorSettingsProp = serializedObject.FindProperty("animatorAnalysisSettings");
            physicsSettingsProp = serializedObject.FindProperty("physicsAnalysisSettings");
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

            DrawOptimizationFindings();

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("reportOnly"), new GUIContent("Report-only mode"));
            if (serializedObject.FindProperty("reportOnly").boolValue)
            {
                EditorGUILayout.HelpBox("Analysis is running in report-only mode. No destructive optimization steps will be applied until this is disabled.", MessageType.Info);
            }

            // Main Settings Sections
            DrawBoneSettings();
            EditorGUILayout.Space(5);

            DrawMeshSettings();
            EditorGUILayout.Space(5);

            DrawBlendshapeSettings();
            EditorGUILayout.Space(5);

            DrawAnimatorSettings();
            EditorGUILayout.Space(5);

            DrawPhysicsSettings();
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

        private void DrawOptimizationFindings()
        {
            var opt = (AvatarOptimizer)target;
            if (opt == null || !opt.reportingSettings.showOptimizationSummary)
            {
                return;
            }

            var issueCount = statsProp != null ? statsProp.FindPropertyRelative("analysisIssues").intValue : 0;
            var warningCount = statsProp != null ? statsProp.FindPropertyRelative("analysisWarnings").intValue : 0;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("🧠 Optimization Findings", EditorStyles.boldLabel);

            if (issueCount == 0)
            {
                EditorGUILayout.LabelField("No issues were detected in the latest analysis pass.");
            }
            else
            {
                EditorGUILayout.LabelField($"Found {issueCount} finding(s); {warningCount} warning(s).", EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox("Analysis is currently report-only by default. Review the findings and enable safe auto-apply modes when ready.", MessageType.Info);
            }

            var report = statsProp != null ? statsProp.FindPropertyRelative("lastAnalysisReport").stringValue : string.Empty;
            if (!string.IsNullOrWhiteSpace(report))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Analysis Report", EditorStyles.miniBoldLabel);
                EditorGUILayout.TextArea(report, GUILayout.MinHeight(120));
            }

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

                // ========== MATERIAL ATLASING ANALYSIS ==========
                if (opt.atlasSettings.generateTextureAtlas && cachedEstimates.atlasingMaterials.Count > 0)
                {
                    EditorGUILayout.Space(5);
                    DrawMaterialAtlasingAnalysis(cachedEstimates, opt);
                }

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

            // IMPROVED ATLAS ESTIMATION (align with actual atlasing logic)
            if (opt.atlasSettings.generateTextureAtlas)
            {
                data.atlasingMaterials = AnalyzeMaterialsForAtlasing(avatarRoot, opt, uniqueMaterials, out data.materialsByShader);
                data.totalAtlasableMaterials = data.atlasingMaterials.Count(m => m.canAtlas);
                data.totalExcludedMaterials = data.atlasingMaterials.Count(m => !m.canAtlas);

                int groupId = 0;
                long totalOriginalTextureMemory = 0;
                long totalEstimatedAtlasMemory = 0;
                data.estimatedAtlasesGenerable = 0;

                foreach (var kvp in data.materialsByShader)
                {
                    var matsForShader = kvp.Value.Where(m => m.canAtlas).ToList();

                    if (matsForShader.Count < opt.atlasSettings.minimumMaterialsForAtlas)
                        continue;

                    var groupTextures = new HashSet<Texture2D>();

                    foreach (var matInfo in matsForShader)
                    {
                        foreach (var propInfo in matInfo.compatibleProperties)
                        {
                            var propName = propInfo.Split('(')[0].Trim();
                            if (matInfo.material == null || !matInfo.material.HasProperty(propName))
                                continue;

                            var tex = matInfo.material.GetTexture(propName) as Texture2D;
                            if (tex != null)
                                groupTextures.Add(tex);
                        }
                    }

                    if (groupTextures.Count == 0)
                        continue;

                    long groupOriginalMemory = 0;
                    foreach (var tex in groupTextures)
                    {
                        groupOriginalMemory += EstimateTextureSize(tex);
                    }
                    totalOriginalTextureMemory += groupOriginalMemory;

                    int maxAtlasSize = (int)opt.atlasSettings.maxAtlasSize;
                    int padding = opt.atlasSettings.atlasPadding;

                    long totalPixels = 0;
                    foreach (var tex in groupTextures)
                    {
                        int paddedWidth = tex.width + (padding * 2);
                        int paddedHeight = tex.height + (padding * 2);
                        totalPixels += (long)paddedWidth * paddedHeight;
                    }

                    long estimatedAtlasPixels = (long)(totalPixels / 0.75f); // assume ~75% packing efficiency
                    int atlasSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(estimatedAtlasPixels)));
                    atlasSize = Mathf.Clamp(atlasSize, opt.atlasSettings.minimumOutputAtlasSize, maxAtlasSize);

                    long estimatedAtlasMemory = (long)(atlasSize * atlasSize * 4f * 1.33f); // RGBA32 with mipmaps
                    totalEstimatedAtlasMemory += estimatedAtlasMemory;

                    int numProperties = Mathf.Max(1, matsForShader[0].compatibleProperties.Count);
                    if (opt.atlasSettings.useEnhancedAtlasWorkflow)
                    {
                        numProperties = Mathf.Max(1, Mathf.RoundToInt(numProperties * 0.7f));
                    }

                    data.estimatedAtlasesGenerable += numProperties;
                    groupId++;
                }

                data.estimatedTextureSavingsBytes = totalOriginalTextureMemory - totalEstimatedAtlasMemory;
                data.estimatedAtlasGroups = groupId;
            }

            // Calculate overall compression ratio
            long totalEstimatedSavings = data.estimatedTextureSavingsBytes;
            totalEstimatedSavings += data.estimatedVerticesMergeable * 48; // Vertex memory

            data.estimatedCompressionRatio = data.currentMemoryBytes > 0
                ? totalEstimatedSavings / (float)data.currentMemoryBytes
                : 0f;

            // Clamp to avoid impossible savings percentages
            data.estimatedCompressionRatio = Mathf.Clamp(data.estimatedCompressionRatio, -0.5f, 0.95f);

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

        /// <summary>
        /// Analyze materials for atlasing compatibility (editor-time analysis)
        /// </summary>
        private List<EstimationData.MaterialAtlasInfo> AnalyzeMaterialsForAtlasing(
            Transform avatarRoot, 
            AvatarOptimizer opt, 
            HashSet<Material> allMaterials,
            out Dictionary<string, List<EstimationData.MaterialAtlasInfo>> materialsByShader)
        {
            var results = new List<EstimationData.MaterialAtlasInfo>();
            materialsByShader = new Dictionary<string, List<EstimationData.MaterialAtlasInfo>>();

            foreach (var mat in allMaterials)
            {
                if (mat == null || mat.shader == null)
                    continue;

                var info = new EstimationData.MaterialAtlasInfo
                {
                    material = mat,
                    materialName = mat.name,
                    shaderName = mat.shader.name,
                    canAtlas = true
                };

                // Check shader filtering
                if (!ShouldIncludeShader(mat.shader, opt))
                {
                    info.canAtlas = false;
                    info.reasons.Add($"Shader '{mat.shader.name}' is in exclusion list");
                }

                // Check material name patterns
                if (info.canAtlas && ShouldExcludeMaterialByPattern(mat, opt))
                {
                    info.canAtlas = false;
                    info.reasons.Add("Material name matches exclusion pattern");
                }

                // Analyze texture properties
                int propertyCount = ShaderUtil.GetPropertyCount(mat.shader);
                for (int i = 0; i < propertyCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                        continue;

                    string propName = ShaderUtil.GetPropertyName(mat.shader, i);

                    if (!ShouldIncludeTextureProperty(propName, opt))
                        continue;

                    if (!mat.HasProperty(propName))
                    {
                        info.incompatibleProperties.Add($"{propName} (not present)");
                        continue;
                    }

                    var tex = mat.GetTexture(propName);
                    if (tex == null)
                    {
                        info.incompatibleProperties.Add($"{propName} (null texture)");
                        continue;
                    }

                    if (tex.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
                    {
                        info.incompatibleProperties.Add($"{propName} ({tex.dimension} texture)");
                        if (info.canAtlas)
                        {
                            info.canAtlas = false;
                            info.reasons.Add($"Property '{propName}' has non-2D texture");
                        }
                        continue;
                    }

                    var tex2D = tex as Texture2D;
                    if (tex2D != null)
                    {
                        int texWidth = tex2D.width;
                        int texHeight = tex2D.height;

                        if (texWidth < opt.atlasSettings.minimumTextureSize || 
                            texHeight < opt.atlasSettings.minimumTextureSize)
                        {
                            info.incompatibleProperties.Add($"{propName} (too small: {texWidth}x{texHeight})");
                            continue;
                        }

                        info.compatibleProperties.Add($"{propName} ({texWidth}x{texHeight})");
                    }
                }

                if (info.canAtlas && info.compatibleProperties.Count == 0)
                {
                    info.canAtlas = false;
                    info.reasons.Add("No compatible 2D texture properties found");
                }

                if (info.canAtlas)
                {
                    info.reasons.Add("✅ Can be atlased");
                }

                results.Add(info);

                // Group by shader
                if (!materialsByShader.ContainsKey(info.shaderName))
                    materialsByShader[info.shaderName] = new List<EstimationData.MaterialAtlasInfo>();

                materialsByShader[info.shaderName].Add(info);
            }

            return results;
        }

        private bool ShouldIncludeShader(Shader shader, AvatarOptimizer opt)
        {
            string shaderName = shader.name;
            var settings = opt.atlasSettings;

            if (!string.IsNullOrEmpty(settings.excludedShaderNames))
            {
                var excludePatterns = settings.excludedShaderNames.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in excludePatterns)
                {
                    if (shaderName.Contains(pattern, System.StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            if (!string.IsNullOrEmpty(settings.allowedShaderNames))
            {
                var allowPatterns = settings.allowedShaderNames.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (allowPatterns.Contains("*"))
                    return true;

                bool matches = allowPatterns.Any(pattern => 
                    shaderName.Contains(pattern, System.StringComparison.OrdinalIgnoreCase));
                return matches;
            }

            return true;
        }

        private bool ShouldExcludeMaterialByPattern(Material mat, AvatarOptimizer opt)
        {
            if (string.IsNullOrEmpty(opt.atlasSettings.excludeMaterialPatterns))
                return false;

            var patterns = opt.atlasSettings.excludeMaterialPatterns.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p));

            foreach (var pattern in patterns)
            {
                if (mat.name.Contains(pattern, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool ShouldIncludeTextureProperty(string propName, AvatarOptimizer opt)
        {
            var settings = opt.atlasSettings;

            if (!string.IsNullOrEmpty(settings.excludedTextureProperties))
            {
                var excludePatterns = settings.excludedTextureProperties.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in excludePatterns)
                {
                    if (MatchesWildcard(propName, pattern))
                        return false;
                }
            }

            if (!string.IsNullOrEmpty(settings.allowedTextureProperties))
            {
                var allowPatterns = settings.allowedTextureProperties.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (allowPatterns.Contains("*"))
                    return true;

                bool matches = allowPatterns.Any(pattern => MatchesWildcard(propName, pattern));
                return matches;
            }

            return true;
        }

        private bool MatchesWildcard(string text, string pattern)
        {
            if (pattern == "*")
                return true;

            if (pattern.Contains("*"))
            {
                var regexPattern = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*");
                return System.Text.RegularExpressions.Regex.IsMatch(text, "^" + regexPattern + "$", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return text.Contains(pattern, System.StringComparison.OrdinalIgnoreCase);
        }

        private bool GetShaderFoldoutState(string key)
        {
            if (!shaderFoldoutStates.ContainsKey(key))
                shaderFoldoutStates[key] = false;
            return shaderFoldoutStates[key];
        }

        private void SetShaderFoldoutState(string key, bool state)
        {
            shaderFoldoutStates[key] = state;
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

        /// <summary>
        /// Draw material atlasing analysis in the Pre-Optimization panel
        /// </summary>
        private void DrawMaterialAtlasingAnalysis(EstimationData data, AvatarOptimizer opt)
        {
            DrawEstimateSection("Material Atlasing Analysis", new Color(1f, 0.95f, 0.85f), () =>
            {
                // Summary stats with clearer labeling
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Total Materials Found:", GUILayout.Width(180));
                EditorGUILayout.LabelField(data.atlasingMaterials.Count.ToString(), EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  ✓ Atlaseable:", GUILayout.Width(180));
                var atlasableStyle = new GUIStyle(EditorStyles.boldLabel);
                atlasableStyle.normal.textColor = new Color(0.2f, 0.8f, 0.2f);
                EditorGUILayout.LabelField($"{data.totalAtlasableMaterials} materials", atlasableStyle);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  ✗ Excluded:", GUILayout.Width(180));
                var excludedStyle = new GUIStyle(EditorStyles.boldLabel);
                excludedStyle.normal.textColor = new Color(0.8f, 0.3f, 0.2f);
                EditorGUILayout.LabelField($"{data.totalExcludedMaterials} materials", excludedStyle);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Shader Groups (atlaseable):", GUILayout.Width(180));
                EditorGUILayout.LabelField(data.estimatedAtlasGroups.ToString(), EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Estimated Atlases:", GUILayout.Width(180));
                var atlasCountStyle = new GUIStyle(EditorStyles.boldLabel);
                atlasCountStyle.normal.textColor = data.estimatedAtlasesGenerable > 0 ?
                    new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.6f, 0.2f);
                EditorGUILayout.LabelField(data.estimatedAtlasesGenerable.ToString(), atlasCountStyle);
                EditorGUILayout.EndHorizontal();

                // Show texture memory impact
                if (data.estimatedTextureSavingsBytes != 0)
                {
                    EditorGUILayout.Space(3);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Texture Memory Impact:", GUILayout.Width(180));
                    var memStyle = new GUIStyle(EditorStyles.boldLabel);
                    bool willSave = data.estimatedTextureSavingsBytes > 0;
                    memStyle.normal.textColor = willSave ?
                        new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.3f, 0.2f);
                    string sign = willSave ? "-" : "+";
                    EditorGUILayout.LabelField(
                        $"{sign}{FormatBytes(Math.Abs(data.estimatedTextureSavingsBytes))}",
                        memStyle);
                    EditorGUILayout.EndHorizontal();

                    if (!willSave)
                    {
                        EditorGUILayout.HelpBox(
                            "⚠ Atlasing will INCREASE memory due to padding and packing overhead. " +
                            "Consider increasing minimum texture size or using fewer materials per atlas.",
                            MessageType.Warning);
                    }
                }

                // Shader breakdown
                if (data.materialsByShader.Count > 0)
                {
                    EditorGUILayout.Space(5);
                    var shaderFoldoutStyle = new GUIStyle(EditorStyles.foldout);
                    shaderFoldoutStyle.fontStyle = FontStyle.Bold;

                    showMaterialsByShader = EditorGUILayout.Foldout(showMaterialsByShader, 
                        $"Shader Breakdown ({data.materialsByShader.Count} shaders)", true, shaderFoldoutStyle);

                    if (showMaterialsByShader)
                    {
                        EditorGUI.indentLevel++;

                        foreach (var kvp in data.materialsByShader.OrderBy(k => k.Key))
                        {
                            var shaderName = kvp.Key;
                            var materials = kvp.Value;
                            var atlasable = materials.Count(m => m.canAtlas);
                            var excluded = materials.Count(m => !m.canAtlas);

                            EditorGUILayout.Space(3);

                            // Shader header with color coding
                            var bgColor = atlasable >= opt.atlasSettings.minimumMaterialsForAtlas 
                                ? new Color(0.7f, 1f, 0.7f, 0.3f) 
                                : new Color(1f, 0.9f, 0.7f, 0.3f);

                            var prevBg = GUI.backgroundColor;
                            GUI.backgroundColor = bgColor;
                            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                            GUI.backgroundColor = prevBg;

                            // Shader name and stats
                            EditorGUILayout.LabelField($"🎨 {shaderName}", EditorStyles.boldLabel);

                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField($"Materials: {materials.Count} (✅ {atlasable} | ❌ {excluded})");

                            if (atlasable < opt.atlasSettings.minimumMaterialsForAtlas)
                            {
                                EditorGUILayout.HelpBox(
                                    $"⚠️ Only {atlasable} atlaseable material(s). Minimum required: {opt.atlasSettings.minimumMaterialsForAtlas}", 
                                    MessageType.Warning);
                            }

                            // Material details foldout
                            string foldoutKey = $"shader_{shaderName}";
                            bool showMaterials = EditorGUILayout.Foldout(
                                GetShaderFoldoutState(foldoutKey), 
                                $"Material Details ({materials.Count})", 
                                true);
                            SetShaderFoldoutState(foldoutKey, showMaterials);

                            if (showMaterials)
                            {
                                EditorGUI.indentLevel++;

                                // Show atlaseable materials first
                                var atlaseableMats = materials.Where(m => m.canAtlas).ToList();
                                var excludedMats = materials.Where(m => !m.canAtlas).ToList();

                                if (atlaseableMats.Count > 0)
                                {
                                    EditorGUILayout.LabelField($"✅ Atlaseable Materials ({atlaseableMats.Count}):", 
                                        EditorStyles.boldLabel);
                                    EditorGUI.indentLevel++;
                                    foreach (var mat in atlaseableMats.Take(5))
                                    {
                                        DrawMaterialInfo(mat, true);
                                    }
                                    if (atlaseableMats.Count > 5)
                                    {
                                        EditorGUILayout.LabelField($"... and {atlaseableMats.Count - 5} more");
                                    }
                                    EditorGUI.indentLevel--;
                                }

                                if (excludedMats.Count > 0)
                                {
                                    EditorGUILayout.Space(2);
                                    EditorGUILayout.LabelField($"❌ Excluded Materials ({excludedMats.Count}):", 
                                        EditorStyles.boldLabel);
                                    EditorGUI.indentLevel++;
                                    foreach (var mat in excludedMats.Take(5))
                                    {
                                        DrawMaterialInfo(mat, false);
                                    }
                                    if (excludedMats.Count > 5)
                                    {
                                        EditorGUILayout.LabelField($"... and {excludedMats.Count - 5} more");
                                    }
                                    EditorGUI.indentLevel--;
                                }

                                EditorGUI.indentLevel--;
                            }

                            EditorGUI.indentLevel--;
                            EditorGUILayout.EndVertical();
                        }

                        EditorGUI.indentLevel--;
                    }
                }
            });
        }

        private void DrawMaterialInfo(EstimationData.MaterialAtlasInfo mat, bool canAtlas)
        {
            var icon = canAtlas ? "✅" : "❌";
            EditorGUILayout.LabelField($"{icon} {mat.materialName}");

            EditorGUI.indentLevel++;

            // Reasons
            if (mat.reasons.Count > 0)
            {
                foreach (var reason in mat.reasons)
                {
                    var style = new GUIStyle(EditorStyles.miniLabel);
                    style.normal.textColor = reason.Contains("✅") 
                        ? new Color(0.2f, 0.7f, 0.2f) 
                        : new Color(0.7f, 0.3f, 0.2f);
                    EditorGUILayout.LabelField($"  • {reason}", style);
                }
            }

            // Compatible properties
            if (mat.compatibleProperties.Count > 0)
            {
                var propText = string.Join(", ", mat.compatibleProperties.Take(3));
                if (mat.compatibleProperties.Count > 3)
                    propText += $" (+{mat.compatibleProperties.Count - 3} more)";

                var style = new GUIStyle(EditorStyles.miniLabel);
                style.normal.textColor = new Color(0.3f, 0.6f, 0.9f);
                EditorGUILayout.LabelField($"  Properties: {propText}", style);
            }

            // Incompatible properties
            if (mat.incompatibleProperties.Count > 0)
            {
                var propText = string.Join(", ", mat.incompatibleProperties.Take(3));
                if (mat.incompatibleProperties.Count > 3)
                    propText += $" (+{mat.incompatibleProperties.Count - 3} more)";

                var style = new GUIStyle(EditorStyles.miniLabel);
                style.normal.textColor = new Color(0.6f, 0.5f, 0.4f);
                EditorGUILayout.LabelField($"  Excluded: {propText}", style);
            }

            EditorGUI.indentLevel--;
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
                if (meshSettingsProp.FindPropertyRelative("combineMeshes").boolValue)
                {
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("excludeFaceMeshFromCombine"));
                }
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
                DrawSubsectionLabel("Attribute Stripping");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripUnusedAttributes"), 
                    new GUIContent("Strip Unused Attributes", "Enable intelligent stripping of unused vertex attributes"));
                
                if (meshSettingsProp.FindPropertyRelative("stripUnusedAttributes").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripTangents"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripVertexColors"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripLightmapUVs"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("stripExtraUVChannels"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Additional Options");
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("deduplicateMaterials"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("optimizeIndexBuffer"));
                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeIdenticalSubmeshes"));
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

        private void DrawAnimatorSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.95f, 0.95f, 1f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            var showAnimator = EditorGUILayout.Foldout(true, "🎬 Animator Analysis", true, EditorStyles.foldoutHeader);
            if (showAnimator)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("enableAnalysis"));
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("warnOnExcessiveLayers"));
                if (animatorSettingsProp.FindPropertyRelative("warnOnExcessiveLayers").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("maxRecommendedLayers"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("warnOnExcessiveParameters"));
                if (animatorSettingsProp.FindPropertyRelative("warnOnExcessiveParameters").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("maxRecommendedParameters"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("warnOnHeavyToggleSetup"));
                if (animatorSettingsProp.FindPropertyRelative("warnOnHeavyToggleSetup").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("maxRecommendedBoolParameters"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("warnOnSelfTransitions"));
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("preserveCVRFacialSafety"));
                EditorGUILayout.PropertyField(animatorSettingsProp.FindPropertyRelative("reportOnly"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPhysicsSettings()
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.95f, 0.95f, 1f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            var showPhysics = EditorGUILayout.Foldout(true, "🧲 Physics Analysis", true, EditorStyles.foldoutHeader);
            if (showPhysics)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("enableAnalysis"));
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("warnOnMagicaSelfCollision"));
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("warnOnMagicaMutualCollision"));
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("warnOnHighProxyVertexCount"));
                if (physicsSettingsProp.FindPropertyRelative("warnOnHighProxyVertexCount").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("maxRecommendedProxyVertexCount"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("warnOnHighSimulationFrequency"));
                if (physicsSettingsProp.FindPropertyRelative("warnOnHighSimulationFrequency").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("maxRecommendedSimulationFrequency"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("warnOnDynamicBoneComplexity"));
                if (physicsSettingsProp.FindPropertyRelative("warnOnDynamicBoneComplexity").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("maxRecommendedDynamicBoneColliders"));
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.PropertyField(physicsSettingsProp.FindPropertyRelative("reportOnly"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

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
                {
                    DrawMaxAtlasResolutionDropdown();
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawMaxAtlasResolutionDropdown()
        {
            // Common square atlas resolutions (pixels = size * size)
            int[] sizes = { 1024, 2048, 4096, 8192 };
            int[] pixelCounts = sizes.Select(s => s * s).ToArray();
            string[] labels = sizes.Select(s => $"{s} x {s} ({(s * s) / 1_000_000f:F1} MP)").Concat(new[] { "Custom" }).ToArray();

            var maxPixelsProp = atlasSettingsProp.FindPropertyRelative("maxAtlasPixels");
            int currentPixels = maxPixelsProp.intValue;

            int selected = Array.IndexOf(pixelCounts, currentPixels);
            if (selected < 0)
            {
                selected = labels.Length - 1; // Custom
            }

            int newSelected = EditorGUILayout.Popup(new GUIContent("Max Atlas Resolution", "Caps atlas size using a resolution preset"), selected, labels);

            if (newSelected >= 0 && newSelected < pixelCounts.Length)
            {
                // Preset selected
                int pixels = pixelCounts[newSelected];
                if (pixels != maxPixelsProp.intValue)
                {
                    maxPixelsProp.intValue = pixels;
                }
            }
            else
            {
                // Custom entry
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(maxPixelsProp, new GUIContent("Custom Max Pixels"));
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
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("includeComplexityScore"));

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
