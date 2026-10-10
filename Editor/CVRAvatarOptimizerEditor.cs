#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;

namespace MilchZocker.AvatarOptimizer
{
    [CustomEditor(typeof(AvatarOptimizer))]
    public class CVRAvatarOptimizerEditor : Editor
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
        private bool showSimplificationSettings = false;
        private bool showMergeLimits = false;
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
            public class ProcessInfo
            {
                public string name;
                public bool enabled;
                public string estimate;
                public string reason;
            }

            public List<ProcessInfo> processes = new List<ProcessInfo>();

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
                public string animatedSignature = string.Empty; // Which properties (if any) are animated together
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

            DrawSimplificationSettings();
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

                DrawProcessPlan(cachedEstimates.processes, "⚙ Process Plan");

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

                DrawProcessPlan(BuildResultProcessPlan(opt), "⚙ Process Results");
                EditorGUILayout.Space(5);

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

        private void DrawProcessPlan(IEnumerable<EstimationData.ProcessInfo> processes, string title)
        {
            DrawEstimateSection(title, new Color(0.88f, 0.94f, 1f), () =>
            {
                foreach (var process in processes)
                {
                    var color = process.enabled ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.55f, 0.55f, 0.55f);
                    var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } };
                    EditorGUILayout.LabelField($"{(process.enabled ? "ON" : "OFF")}  {process.name}", style);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField(process.estimate, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(process.reason, EditorStyles.wordWrappedMiniLabel);
                    EditorGUI.indentLevel--;
                }
            });
        }

        private List<EstimationData.ProcessInfo> BuildResultProcessPlan(AvatarOptimizer opt)
        {
            var processes = BuildProcessPlan(opt, null);
            if (statsProp != null)
            {
                var stats = new Dictionary<string, int>
                {
                    ["Mesh combine"] = statsProp.FindPropertyRelative("meshesCombined")?.intValue ?? 0,
                    ["Blendshape cleanup"] = statsProp.FindPropertyRelative("blendshapesRemoved")?.intValue ?? 0,
                    ["Vertex cleanup"] = statsProp.FindPropertyRelative("verticesMerged")?.intValue ?? 0,
                    ["Atlas generation"] = statsProp.FindPropertyRelative("atlasesGenerated")?.intValue ?? 0
                };

                foreach (var process in processes)
                {
                    if (stats.TryGetValue(process.name, out var value))
                        process.estimate = $"Completed: {value:N0}";
                }
            }
            return processes;
        }

        private List<EstimationData.ProcessInfo> BuildProcessPlan(AvatarOptimizer opt, EstimationData data)
        {
            var processes = new List<EstimationData.ProcessInfo>();
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Bone reference cleanup",
                enabled = opt.boneSettings.removeUnusedBoneReferences || opt.boneSettings.removeBonesWithoutWeights,
                estimate = data == null ? "Removes unused references while preserving required rig links" : $"Estimated removable: {data.estimatedBonesRemovable:N0}",
                reason = "Reduces skinning data without deleting required bone objects."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Blendshape cleanup",
                enabled = opt.blendshapeSettings.removeUnusedBlendshapes,
                estimate = data == null ? "Preserves animated and CVR facial channels" : $"Estimated removable: {data.estimatedBlendshapesRemovable:N0}",
                reason = "Animation curves, CVR lip sync, blink, eye look, and face tracking are treated as preservation inputs."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Vertex cleanup",
                enabled = opt.meshSettings.mergeVerticesByDistance || opt.meshSettings.deleteLooseVertices,
                estimate = data == null ? "Distance/normal/UV guarded cleanup" : $"Estimated mergeable: {data.estimatedVerticesMergeable:N0}",
                reason = "Only vertices passing the configured geometric safety checks are merged."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Mesh combine",
                enabled = opt.meshSettings.combineMeshes,
                estimate = data == null ? "Uses compatible renderer groups" : $"Estimated renderer reduction: {data.estimatedMeshesCombineable:N0}",
                reason = "Legacy mode requires strict compatibility; toggle mode uses clone-only relaxed grouping after successful preparation."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Toggle-preserving merge",
                enabled = opt.meshSettings.mergeToggleObjects,
                estimate = opt.meshSettings.toggleMergeBackend == AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.Automatic
                    ? "Automatic -> Infinimation Blendshape"
                    : opt.meshSettings.toggleMergeBackend.ToString(),
                reason = opt.meshSettings.toggleMergeBackend == AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.Automatic
                    ? "Selected because it adds no bones, is shader-independent, and supports clone-only blendshape animation repathing."
                    : "Explicit backend selection; unsupported experimental backends are reported during build."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Atlas generation",
                enabled = opt.atlasSettings.generateTextureAtlas,
                estimate = data == null ? "Groups by shader, textures, and animation signature" : $"Estimated atlases: {data.estimatedAtlasesGenerable:N0}",
                reason = "Animated materials are grouped by matching animated property signatures to avoid changing toggle behavior."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Standalone texture compression",
                enabled = opt.atlasSettings.useAdaptiveCompression,
                estimate = opt.atlasSettings.onlyReduceFileSize
                    ? "Clone-only adaptive compression; larger estimates are skipped"
                    : "Clone-only adaptive compression for non-atlased textures",
                reason = "Runs standalone when atlasing is disabled; original texture assets and importer settings are never modified."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Attribute stripping",
                enabled = opt.meshSettings.stripUnusedAttributes,
                estimate = "Removes unused tangents, colors, lightmap UVs, and extra channels",
                reason = "Reduces vertex-buffer memory only when the selected attributes are not required."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Material deduplication",
                enabled = opt.meshSettings.deduplicateMaterials,
                estimate = "Reuses identical compatible material assignments",
                reason = "Reduces duplicate material slots without modifying source material assets."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Mesh compression and GPU optimization",
                enabled = opt.meshSettings.optimizeMeshForRendering || opt.meshSettings.applyMeshCompression,
                estimate = "Applies selected Unity mesh optimization and compression settings",
                reason = "Can reduce runtime vertex/index cost or memory; quality is controlled by compression level."
            });
            processes.Add(new EstimationData.ProcessInfo
            {
                name = "Physics rebuild",
                enabled = true,
                estimate = "Rebuilds supported physics data after mesh and bone changes",
                reason = "Unsupported physics integrations are skipped safely."
            });
            return processes;
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

            foreach (var material in MaterialAnimationUtility.GetReferencedMaterials(avatarRoot))
            {
                uniqueMaterials.Add(material);
                CollectTextures(material, uniqueTextures);
            }

            // Count bones
            data.currentBoneCount = avatarRoot.GetComponentsInChildren<Transform>(true).Length;

            // Estimate mergeable vertices from the configured geometry guards rather than
            // using a fixed percentage. This remains conservative but responds to mesh data.
            if (opt.meshSettings.mergeVerticesByDistance)
            {
                data.estimatedVerticesMergeable = EstimateMergeableVertices(meshRenderers, opt);
            }

            // Estimate combineable meshes using strict material/root/bone compatibility.
            // Toggle-preserving mode gets a separate relaxed estimate because it can merge
            // across materials after successful clone-only toggle preparation.
            if (opt.meshSettings.combineMeshes || opt.meshSettings.mergeToggleObjects)
                data.estimatedMeshesCombineable = EstimateCombineableMeshes(meshRenderers, opt);

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

                // Partition each shader group by animated-property signature: materials animated
                // the same way (or both static) may atlas together, but a mismatch (different
                // animated properties, or animated vs. not animated) keeps them apart. This must
                // match the grouping used during actual atlas generation, or the stats shown here
                // will overstate how many materials can really be merged.
                foreach (var kvp in data.materialsByShader)
                {
                    foreach (var sigGroup in kvp.Value.Where(m => m.canAtlas).GroupBy(m => m.animatedSignature))
                    {
                        var sigMats = sigGroup.ToList();
                        if (sigMats.Count < opt.atlasSettings.minimumMaterialsForAtlas)
                        {
                            foreach (var info in sigMats)
                            {
                                info.canAtlas = false;
                                info.reasons.Add("Not enough materials sharing the same animation state");
                            }
                        }
                    }
                }

                data.totalAtlasableMaterials = data.atlasingMaterials.Count(m => m.canAtlas);
                data.totalExcludedMaterials = data.atlasingMaterials.Count(m => !m.canAtlas);

                int groupId = 0;
                long totalOriginalTextureMemory = 0;
                long totalEstimatedAtlasMemory = 0;
                data.estimatedAtlasesGenerable = 0;

                foreach (var kvp in data.materialsByShader)
                {
                    foreach (var sigGroup in kvp.Value.Where(m => m.canAtlas).GroupBy(m => m.animatedSignature))
                    {
                        var matsForShader = sigGroup.ToList();

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

            data.processes = BuildProcessPlan(opt, data);

            return data;
        }

        private int EstimateMergeableVertices(SkinnedMeshRenderer[] renderers, AvatarOptimizer opt)
        {
            var estimate = 0;
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || mesh.vertexCount < 2)
                    continue;

                var vertices = mesh.vertices;
                var threshold = opt.meshSettings.mergeDistance * opt.meshSettings.mergeDistance;
                for (var index = 1; index < vertices.Length; index++)
                {
                    if ((vertices[index] - vertices[index - 1]).sqrMagnitude <= threshold)
                        estimate++;
                }
            }
            return estimate;
        }

        private int EstimateCombineableMeshes(SkinnedMeshRenderer[] renderers, AvatarOptimizer opt)
        {
            var groups = new List<List<SkinnedMeshRenderer>>();
            foreach (var renderer in renderers.Where(item => item != null && item.sharedMesh != null))
            {
                if (opt.meshSettings.excludeFaceMeshFromCombine && IsFaceMeshForEstimate(renderer, opt))
                    continue;

                var group = groups.FirstOrDefault(candidate =>
                    opt.meshSettings.mergeToggleObjects ||
                    (candidate[0].rootBone == renderer.rootBone &&
                     candidate[0].sharedMaterials.SequenceEqual(renderer.sharedMaterials)));
                if (group == null)
                {
                    group = new List<SkinnedMeshRenderer>();
                    groups.Add(group);
                }
                group.Add(renderer);
            }
            return groups.Sum(group => Mathf.Max(0, group.Count - 1));
        }

        private bool IsFaceMeshForEstimate(SkinnedMeshRenderer renderer, AvatarOptimizer opt)
        {
            var avatar = opt.GetComponent("CVRAvatar");
            return avatar != null && renderer == GetObjectMember<SkinnedMeshRenderer>(avatar, "bodyMesh");
        }

        private T GetObjectMember<T>(UnityEngine.Object target, string memberName) where T : UnityEngine.Object
        {
            var field = target.GetType().GetField(memberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field != null ? field.GetValue(target) as T : null;
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

        // Sentinel signature for materials that aren't animated at all, distinct from
        // the empty string used when animation-awareness is disabled entirely.
        private const string NotAnimatedSignature = "\u0002__static__";

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

            // Scan for materials animated together (e.g. a Hue Shift applied via animator curves)
            // so materials sharing the same animated properties can still be grouped for atlasing.
            Dictionary<Material, List<string>> animatedMaterialProperties = null;
            {
                var avatarComponent = MaterialAnimationUtility.FindCvrAvatarComponent(avatarRoot);
                animatedMaterialProperties = MaterialAnimationUtility.CollectAnimatedMaterialProperties(avatarRoot, avatarComponent);
            }

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

                // Record the animated-property signature (used for grouping below). Materials
                // animated with the same properties can still atlas together; only a mismatch
                // (different properties, or animated vs. not) excludes materials from merging.
                if (animatedMaterialProperties != null)
                {
                    if (animatedMaterialProperties.TryGetValue(mat, out var animatedProps) && animatedProps.Count > 0)
                    {
                        info.animatedSignature = string.Join(",", animatedProps.OrderBy(p => p, System.StringComparer.Ordinal));
                    }
                    else
                    {
                        info.animatedSignature = NotAnimatedSignature;
                    }

                    if (animatedMaterialProperties.TryGetValue(mat, out var unsafeProperties) &&
                        unsafeProperties.Contains(MaterialAnimationUtility.MaterialReferenceProperty))
                    {
                        info.canAtlas = false;
                        info.reasons.Add("Material reference is animated");
                    }
                }

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
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("preserveBlendshapesWhenCombining"));
                }

                EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("mergeToggleObjects"));
                if (meshSettingsProp.FindPropertyRelative("mergeToggleObjects").boolValue)
                {
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("excludeFaceMeshFromToggleMerge"));
                    EditorGUILayout.PropertyField(meshSettingsProp.FindPropertyRelative("toggleMergeBackend"));
                    var backend = (AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend)
                        meshSettingsProp.FindPropertyRelative("toggleMergeBackend").enumValueIndex;
                    if (backend != AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.Automatic &&
                        backend != AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.InfinimationBlendshape)
                    {
                        EditorGUILayout.HelpBox(
                            "This toggle-preserving backend is not implemented for this package yet. " +
                            "Use Infinimation Blendshape for the supported path.",
                            MessageType.Warning);
                    }
                    else if (backend == AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.Automatic)
                    {
                        EditorGUILayout.HelpBox(
                            "Automatic currently selects Infinimation Blendshape: it preserves toggles without adding bones or requiring a shader-specific discard path.",
                            MessageType.Info);
                    }
                }
                showMergeLimits = EditorGUILayout.Foldout(showMergeLimits, "Merge Limits & Preview", true);
                if (showMergeLimits)
                {
                    EditorGUI.indentLevel++;
                    DrawSettingsContents(serializedObject.FindProperty("mergeBudgetSettings"));
                    if (GUILayout.Button("Preview merge plan"))
                    {
                        // Include edits from this inspector event in the preview.
                        serializedObject.ApplyModifiedProperties();
                        EditorUtility.DisplayDialog("Merge plan (estimates, not measured GPU timings)",
                            GuardedAvatarPasses.DescribeMergePlan((AvatarOptimizer)target), "Close");
                    }
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Space(5);
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

        #region Simplification Settings

        private void DrawSettingsContents(SerializedProperty settings)
        {
            var property = settings.Copy();
            var end = property.GetEndProperty();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end))
            {
                if (settings.name == "simplificationSettings" && property.name == "backend") DrawBackendSelector(property);
                else EditorGUILayout.PropertyField(property, true);
                enterChildren = false;
            }
        }

        private void DrawBackendSelector(SerializedProperty property)
        {
            var rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent("Backend"));
            if (EditorGUI.DropdownButton(rect, new GUIContent(property.stringValue), FocusType.Keyboard))
            {
                var menu = new GenericMenu();
                var path = property.propertyPath;
                foreach (var backend in new[] { "UnityMeshSimplifier", "Meshia" })
                {
                    var label = new GUIContent(backend);
                    if (!GuardedAvatarPasses.BackendAvailable(backend, out var reason))
                        menu.AddDisabledItem(new GUIContent(backend + " (unavailable)", reason), property.stringValue == backend);
                    else menu.AddItem(label, property.stringValue == backend, () =>
                    {
                        serializedObject.Update();
                        serializedObject.FindProperty(path).stringValue = backend;
                        serializedObject.ApplyModifiedProperties();
                        estimatesNeedUpdate = true;
                    });
                }
                menu.DropDown(rect);
            }
            if (!GuardedAvatarPasses.BackendAvailable(property.stringValue, out var selectedReason))
                EditorGUILayout.HelpBox(selectedReason + " This selection will be skipped without falling back.", MessageType.Warning);
            if (property.stringValue != "Meshia" && !GuardedAvatarPasses.BackendAvailable("Meshia", out var meshiaReason))
                EditorGUILayout.HelpBox(meshiaReason, MessageType.Info);
        }

        private void DrawSimplificationSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            showSimplificationSettings = EditorGUILayout.Foldout(showSimplificationSettings, "Simplification", true, EditorStyles.foldoutHeader);
            if (showSimplificationSettings)
            {
                EditorGUI.indentLevel++;
                DrawSubsectionLabel("Mesh Simplification");
                DrawSettingsContents(serializedObject.FindProperty("simplificationSettings"));
                EditorGUILayout.Space(5);
                DrawSubsectionLabel("Rig Reduction");
                DrawSettingsContents(serializedObject.FindProperty("rigReductionSettings"));
                EditorGUI.indentLevel--;
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
                EditorGUILayout.PropertyField(blendshapeSettingsProp.FindPropertyRelative("bakeConstantBlendshapes"));

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

            showAtlasSettings = EditorGUILayout.Foldout(showAtlasSettings, "🎨 Texture Optimization", true, EditorStyles.foldoutHeader);

            if (showAtlasSettings)
            {
                EditorGUILayout.Space(3);
                EditorGUI.indentLevel++;

                DrawSubsectionLabel("Atlas Generation");
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateTextureAtlas"));

                EditorGUILayout.Space(5);
                DrawAtlasSubsection("UV Packing & Material Swaps", infoColor, () =>
                {
                    DrawSettingsContents(serializedObject.FindProperty("surfaceSettings"));
                });

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

                EditorGUILayout.Space(5);
                DrawAdaptiveCompressionSettings();

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
            DrawAtlasSubsection("⚙️ Adaptive Texture Compression", new Color(0.9f, 0.96f, 1f), () =>
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("useAdaptiveCompression"));
                if (atlasSettingsProp.FindPropertyRelative("useAdaptiveCompression").boolValue)
                {
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("onlyReduceFileSize"));
                    EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("automaticFilterModes"));
                }
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
            });
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

namespace MilchZocker.AvatarOptimizer
{
    /// <summary>Explicit, repeatable Editor checks; never runs during import or avatar builds.</summary>
    public static class CVRAvatarOptimizerValidation
    {
        [MenuItem("Tools/Avatar Optimizer/Run optimizer regression checks")]
        public static void RunRegressionChecks()
        {
            var results = new List<string>();
            var owned = new List<UnityEngine.Object>();
            var root = new GameObject("Optimizer regression fixture");
            root.SetActive(false);
            owned.Add(root);
            try
            {
                var opt = root.AddComponent<AvatarOptimizer>();
                var child = new GameObject("Accessory"); child.transform.SetParent(root.transform, false);
                var renderer = child.AddComponent<SkinnedMeshRenderer>();
                var bone = new GameObject("Bone"); bone.transform.SetParent(root.transform, false);
                var mesh = new Mesh { name = "Regression triangle", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                    triangles = new[] { 0, 1, 2 }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up },
                    normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                    bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity } };
                owned.Add(mesh); renderer.sharedMesh = mesh; renderer.bones = new[] { bone.transform, bone.transform };
                var material = new Material(Shader.Find("Standard")); owned.Add(material); renderer.sharedMaterial = material;
                var unlit = new Material(Shader.Find("Unlit/Texture")); owned.Add(unlit);
                renderer.sharedMaterial = unlit;
                Check(GuardedAvatarPasses.GeometryExclusion(opt, renderer) == null, "geometry eligibility does not depend on Standard shader", results);
                Check(GuardedAvatarPasses.RendererMergeRejection(opt, renderer) == null, "opaque custom shader is not rejected by shader identity", results);
                var constraint = bone.AddComponent<UnityEngine.Animations.ParentConstraint>();
                constraint.AddSource(new UnityEngine.Animations.ConstraintSource { sourceTransform = bone.transform, weight = 1 });
                Check(GuardedAvatarPasses.GeometryExclusion(opt, renderer) == null, "constraints referencing renderer bones do not block geometry", results);
                UnityEngine.Object.DestroyImmediate(constraint);
                var clothObject = new GameObject("Other cloth"); clothObject.transform.SetParent(root.transform, false);
                var clothRenderer = clothObject.AddComponent<SkinnedMeshRenderer>(); clothRenderer.sharedMesh = mesh;
                var cloth = clothObject.AddComponent<Cloth>();
                Check(GuardedAvatarPasses.MeshClothDependency(opt, clothRenderer) != null, "Unity Cloth protects its target", results);
                Check(GuardedAvatarPasses.GeometryExclusion(opt, renderer) == null, "unrelated cloth sharing a mesh asset does not block renderer", results);
                UnityEngine.Object.DestroyImmediate(clothObject);
                var magicaType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MagicaCloth2.MagicaCloth")).FirstOrDefault(t => t != null);
                if (magicaType != null)
                {
                    var clothHost = new GameObject("Magica fixture"); clothHost.transform.SetParent(root.transform, false);
                    var magica = clothHost.AddComponent(magicaType);
                    var serializedCloth = new SerializedObject(magica);
                    var kind = serializedCloth.FindProperty("serializeData.clothType");
                    var sources = serializedCloth.FindProperty("serializeData.sourceRenderers");
                    sources.arraySize = 1; sources.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
                    kind.enumValueIndex = Array.IndexOf(kind.enumNames, "MeshCloth"); serializedCloth.ApplyModifiedPropertiesWithoutUndo();
                    Check(GuardedAvatarPasses.MeshClothDependency(opt, renderer) != null, "Magica MeshCloth protects referenced renderer", results);
                    sources.GetArrayElementAtIndex(0).objectReferenceValue = null; serializedCloth.ApplyModifiedPropertiesWithoutUndo();
                    Check(GuardedAvatarPasses.GeometryExclusion(opt, renderer) == null, "unrelated Magica MeshCloth does not block geometry", results);
                    sources.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
                    kind.enumValueIndex = Array.IndexOf(kind.enumNames, "BoneCloth"); serializedCloth.ApplyModifiedPropertiesWithoutUndo();
                    Check(GuardedAvatarPasses.GeometryExclusion(opt, renderer) == null, "Magica BoneCloth does not block geometry", results);
                    UnityEngine.Object.DestroyImmediate(clothHost);
                }
                Check(!GuardedAvatarPasses.BackendAvailable("Unknown", out _), "unknown backend is unavailable", results);
                if (GuardedAvatarPasses.BackendAvailable("Meshia", out var missingMeshia))
                {
                    var grid = new Mesh { name = "Backend grid" }; owned.Add(grid);
                    var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
                    for (int y = 0; y <= 8; y++) for (int x = 0; x <= 8; x++)
                    { vertices.Add(new Vector3(x / 8f, y / 8f, 0)); uv.Add(new Vector2(x / 8f, y / 8f)); }
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    { int i = y * 9 + x; triangles.AddRange(new[] { i, i + 1, i + 9, i + 1, i + 10, i + 9 }); }
                    grid.SetVertices(vertices); grid.SetUVs(0, uv); grid.SetTriangles(triangles, 0); grid.RecalculateNormals();
                    var gridDelta = Enumerable.Repeat(Vector3.forward * 0.01f, vertices.Count).ToArray();
                    grid.AddBlendShapeFrame("Expression", 50, gridDelta, null, null);
                    grid.AddBlendShapeFrame("Expression", 100, gridDelta.Select(v => v * 2).ToArray(), null, null);
                    var candidate = GuardedAvatarPasses.CreateSimplificationCandidate(grid, "Meshia", 0.75f); owned.Add(candidate);
                    Check(candidate.triangles.Length < grid.triangles.Length && grid.vertexCount == 81 && grid.triangles.Length == 384,
                        "Meshia reduces grid without changing original", results);
                    Check(GuardedAvatarPasses.StreamsCompatible(grid, candidate), "Meshia preserves multi-frame shape and stream layout", results);
                    renderer.sharedMesh = grid;
                    Check(GuardedAvatarPasses.ValidatePoses(opt, renderer, candidate, 0.001f, false, out var poseReason),
                        "Meshia grid passes expression pose validation: " + poseReason, results);
                    renderer.sharedMesh = mesh;
                }
                else
                {
                    bool rejected = false;
                    try { var unexpected = GuardedAvatarPasses.CreateSimplificationCandidate(mesh, "Meshia", 0.75f); owned.Add(unexpected); }
                    catch (InvalidOperationException) { rejected = true; }
                    Check(rejected, "unavailable Meshia refuses generation without fallback", results);
                    results.Add("SKIP: Meshia generation: " + missingMeshia);
                }
                opt.surfaceSettings.shaderProfiles = new[] { new AvatarOptimizer.SurfaceShaderProfile {
                    shader = unlit.shader, uv0Textures = "_MainTex" } };
                GuardedAvatarPasses.SurfaceTextureUsage(opt, unlit, true, out var customUv, out var independentUv, out var unusedUv);
                Check(customUv.Contains("_MainTex") && independentUv.Count == 0 && unusedUv.Count == 0, "custom shader UV contract accepted without feature-name guesses", results);
                opt.surfaceSettings.shaderProfiles[0].independentTextures = "_MainTex";
                bool rejectedConflict = false;
                try { GuardedAvatarPasses.SurfaceTextureUsage(opt, unlit, true, out _, out _, out _); }
                catch (InvalidOperationException) { rejectedConflict = true; }
                Check(rejectedConflict, "conflicting UV mapping rejected", results);
                opt.surfaceSettings.shaderProfiles = Array.Empty<AvatarOptimizer.SurfaceShaderProfile>();
                var lilShader = Shader.Find("lilToon");
                if (lilShader != null)
                {
                    var lil = new Material(lilShader); owned.Add(lil);
                    GuardedAvatarPasses.SurfaceTextureUsage(opt, lil, true, out var lilUv, out var lilIndependent, out var lilUnused);
                    Check(lilUv.Contains("_MainTex") && lilUnused.Contains("_BumpMap"), "lilToon main UV mapping and disabled normal feature recognized", results);
                    lil.SetFloat("_ShadowColorType", 1);
                    GuardedAvatarPasses.SurfaceTextureUsage(opt, lil, true, out lilUv, out lilIndependent, out _);
                    Check(!lilUv.Contains("_ShadowColorTex") && lilIndependent.Contains("_ShadowColorTex"), "lilToon shadow lookup texture stays outside UV atlas", results);
                    lil.SetVector("_MainTex_ScrollRotate", new Vector4(1, 0, 0, 0));
                    bool rejectedAnimatedUv = false;
                    try { GuardedAvatarPasses.SurfaceTextureUsage(opt, lil, true, out _, out _, out _); }
                    catch (InvalidOperationException) { rejectedAnimatedUv = true; }
                    Check(rejectedAnimatedUv, "lilToon UV animation requires dedicated handling", results);
                }
                else results.Add("SKIP: lilToon integration checks (shader not installed in this project)");
                renderer.sharedMaterial = material;
                var weights = Enumerable.Range(0, 3).Select(_ => new List<BoneWeight1> {
                    new BoneWeight1 { boneIndex = 0, weight = 0.6f }, new BoneWeight1 { boneIndex = 1, weight = 0.4f } }).ToList();
                GuardedAvatarPasses.WriteWeights(mesh, weights);
                var zero = new Vector3[3]; var delta = Enumerable.Repeat(Vector3.forward, 3).ToArray();
                mesh.AddBlendShapeFrame("AccessoryOffset", 100, delta, zero, zero);
                renderer.SetBlendShapeWeight(0, 50);
                Check(GuardedAvatarPasses.CanBakeShape(opt, renderer, 0), "unreferenced single-frame constant eligible", results);
                var processor = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AvatarOptimizationProcessor));
                var copyMethod = typeof(AvatarOptimizationProcessor).GetMethod("CopyMeshWithoutBlendshapes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (float weight in new[] { 50f, -25f, 150f })
                {
                    renderer.SetBlendShapeWeight(0, weight);
                    var baked = (Mesh)copyMethod.Invoke(processor, new object[] { renderer, mesh, new List<int> { 0 } }); owned.Add(baked);
                    Check(Mathf.Abs(baked.vertices[0].z - weight / 100f) < 0.00001f && baked.blendShapeCount == 0,
                        "constant baking weight " + weight, results);
                    Check(mesh.vertices[0] == Vector3.zero && mesh.blendShapeCount == 1, "source mesh unchanged", results);
                }
                mesh.AddBlendShapeFrame("MultipleFrames", 50, delta, zero, zero);
                mesh.AddBlendShapeFrame("MultipleFrames", 100, delta, zero, zero);
                renderer.SetBlendShapeWeight(1, 25);
                Check(!GuardedAvatarPasses.CanBakeShape(opt, renderer, 1), "nonzero multi-frame shape excluded", results);
                opt.blendshapeSettings.preserveBlendshapePatterns = "AccessoryOffset";
                Check(!GuardedAvatarPasses.CanBakeShape(opt, renderer, 0), "explicit preserve rule wins", results);
                opt.blendshapeSettings.preserveBlendshapePatterns = "";
                opt.rigReductionSettings.mergeEquivalentBoneReferences = true;
                GuardedAvatarPasses.ProcessRigAndSimplification(opt, _ => { });
                Check(renderer.sharedMesh != mesh && renderer.bones.Length == 1 && bone != null, "equivalent references merged without deleting bone", results);
                owned.Add(renderer.sharedMesh);
                Check(GuardedAvatarPasses.ReadWeights(renderer.sharedMesh).All(w => w.Count == 1 && Mathf.Abs(w[0].weight - 1) < 0.00001f), "merged weights normalized", results);
                Check(mesh.bindposes.Length == 2, "source bind poses unchanged", results);
                var many = UnityEngine.Object.Instantiate(mesh); owned.Add(many);
                many.bindposes = Enumerable.Repeat(Matrix4x4.identity, 6).ToArray();
                GuardedAvatarPasses.WriteWeights(many, Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 6)
                    .Select(i => new BoneWeight1 { boneIndex = i, weight = 1f / 6 }).ToList()).ToList());
                Check(GuardedAvatarPasses.HasManyWeights(many) && GuardedAvatarPasses.ReadWeights(many).All(w => w.Count == 6), "six-weight stream retained", results);
                var previousMesh = renderer.sharedMesh; var previousBones = renderer.bones;
                renderer.sharedMesh = many;
                renderer.bones = Enumerable.Range(0, 6).Select(i =>
                {
                    var obj = new GameObject("ValidationBone" + i); obj.transform.SetParent(root.transform, false); return obj.transform;
                }).ToArray();
                var reducedWeights = UnityEngine.Object.Instantiate(many); owned.Add(reducedWeights);
                GuardedAvatarPasses.WriteWeights(reducedWeights, Enumerable.Range(0, 3).Select(_ => new List<BoneWeight1> {
                    new BoneWeight1 { boneIndex = 0, weight = 1 } }).ToList());
                var previousQuality = QualitySettings.skinWeights;
                try
                {
                    QualitySettings.skinWeights = SkinWeights.OneBone;
                    Check(GuardedAvatarPasses.ValidatePoses(opt, renderer, many, .001f, true, out _), "identical variable-weight mesh passes pose samples", results);
                    Check(!GuardedAvatarPasses.ValidatePoses(opt, renderer, reducedWeights, .001f, true, out _), "lossy weights rejected across generic bends", results);
                    Check(QualitySettings.skinWeights == SkinWeights.OneBone, "pose validation restores editor skinning quality", results);
                }
                finally { QualitySettings.skinWeights = previousQuality; renderer.sharedMesh = previousMesh; renderer.bones = previousBones; }
                Check(GuardedAvatarPasses.SurfaceWithin(mesh, mesh, 0.001f, 5), "identical surface accepted", results);
                var displaced = UnityEngine.Object.Instantiate(mesh); owned.Add(displaced);
                displaced.vertices = mesh.vertices.Select(v => v + Vector3.forward).ToArray();
                Check(!GuardedAvatarPasses.SurfaceWithin(mesh, displaced, 0.001f, 5), "displaced surface rejected", results);
                var distorted = UnityEngine.Object.Instantiate(mesh); owned.Add(distorted);
                distorted.uv = mesh.uv.Select(v => v + Vector2.one).ToArray();
                Check(!GuardedAvatarPasses.AttributesWithin(mesh, distorted, opt.simplificationSettings), "UV distortion rejected", results);
                Check(GuardedAvatarPasses.RendererMergeRejection(opt, renderer) == null, "authored blendshapes eligible for stream-preserving merge", results);
                opt.reportOnly = true;
                var unchanged = renderer.sharedMesh;
                GuardedAvatarPasses.ProcessRigAndSimplification(opt, _ => { });
                GuardedAvatarPasses.ProcessSurfaces(opt, _ => { }, _ => { throw new Exception("report-only rewrote clips"); });
                Check(renderer.sharedMesh == unchanged, "report-only leaves renderer unchanged", results);
                opt.reportOnly = false;
                var texture = new Texture2D(16, 16); owned.Add(texture);
                material.SetTexture("_BumpMap", texture); material.DisableKeyword("_NORMALMAP");
                var alternate = new Material(material) { name = "Alternate" }; owned.Add(alternate);
                var baseClip = new AnimationClip { name = "Base" }; owned.Add(baseClip);
                var swapClip = new AnimationClip { name = "Selected override" }; owned.Add(swapClip);
                var slotBinding = EditorCurveBinding.PPtrCurve("Accessory", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
                AnimationUtility.SetObjectReferenceCurve(swapClip, slotBinding, new[] {
                    new ObjectReferenceKeyframe { time = 0, value = material }, new ObjectReferenceKeyframe { time = 1, value = alternate } });
                var controller = new UnityEditor.Animations.AnimatorController(); owned.Add(controller); controller.AddLayer("Base");
                owned.Add(controller.layers[0].stateMachine);
                var state = controller.layers[0].stateMachine.AddState("Swap"); owned.Add(state); state.motion = baseClip;
                var overrides = new AnimatorOverrideController(controller); owned.Add(overrides); overrides[baseClip] = swapClip;
                var animator = root.AddComponent<Animator>(); animator.runtimeAnimatorController = overrides;
                Check(GuardedAvatarPasses.Clips(opt).Any(u => u.clip == swapClip), "selected override clip inventoried", results);
                opt.surfaceSettings.removeUnusedTextures = true;
                AnimationClip rewritten = null;
                GuardedAvatarPasses.ProcessSurfaces(opt, message => results.Add(message), transform => rewritten = transform(swapClip));
                Check(rewritten != null && renderer.sharedMaterial != material, "material variants and swap clip copied", results);
                owned.Add(rewritten); owned.Add(renderer.sharedMesh); owned.Add(renderer.sharedMaterial);
                var newKeys = AnimationUtility.GetObjectReferenceCurve(rewritten, slotBinding);
                foreach (var key in newKeys) if (key.value != null) owned.Add(key.value);
                Check(newKeys.All(k => k.value is Material m && m.GetTexture("_BumpMap") == null), "unused feature texture removed from every variant", results);
                Check(material.GetTexture("_BumpMap") == texture && AnimationUtility.GetObjectReferenceCurve(swapClip, slotBinding)[1].value == alternate,
                    "source material and swap keys unchanged", results);
                var graphCopies = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
                var cloneGraph = typeof(AvatarOptimizationProcessor).GetMethod("CloneAnimationGraph", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var copiedOverride = (AnimatorOverrideController)cloneGraph.Invoke(processor, new object[] {
                    overrides, new Dictionary<AnimationClip, AnimationClip> { { swapClip, rewritten } }, graphCopies, "_Regression" });
                owned.AddRange(graphCopies.Values);
                var copiedBase = (UnityEditor.Animations.AnimatorController)copiedOverride.runtimeAnimatorController;
                Check(copiedOverride != overrides && copiedBase != controller && copiedBase.layers[0].stateMachine != controller.layers[0].stateMachine,
                    "override and base animator graphs independently copied", results);
                Check(copiedOverride[baseClip] == rewritten && overrides[baseClip] == swapClip,
                    "copied override uses updated clip while original remains unchanged", results);
                Check(copiedBase.layers[0].stateMachine.states[0].state != state && state.motion == baseClip,
                    "animator states detached from original graph", results);
                // Layout families use one set of island rectangles for every variant/map.
                var islandMethod = typeof(GuardedAvatarPasses).GetMethod("BuildIslands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var layoutMethod = typeof(GuardedAvatarPasses).GetMethod("LayoutIslands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var islands = islandMethod.Invoke(null, new object[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] {
                    new Vector2(.1f,.1f), new Vector2(.15f,.1f), new Vector2(.1f,.15f),
                    new Vector2(.8f,.8f), new Vector2(.85f,.8f), new Vector2(.8f,.85f) }, 512, 512 });
                int atlasSize = (int)layoutMethod.Invoke(null, new[] { islands, (object)512, 8 });
                Check(((System.Collections.IList)islands).Count == 2 && atlasSize > 0 && atlasSize < 512, "disconnected sampled islands pack below source size", results);
                opt.surfaceSettings.atlasMaterialSwaps = true;
                var tiled = new Material(material); owned.Add(tiled);
                tiled.SetTexture("_MainTex", texture); tiled.SetTextureScale("_MainTex", Vector2.one * 2);
                texture.wrapMode = TextureWrapMode.Clamp;
                renderer.sharedMaterial = tiled;
                animator.runtimeAnimatorController = null;
                opt.surfaceSettings.packUvIslands = true;
                unchanged = renderer.sharedMesh;
                GuardedAvatarPasses.ProcessSurfaces(opt, _ => { }, _ => { });
                Check(renderer.sharedMesh == unchanged && renderer.sharedMaterial == tiled, "unsupported texture tiling leaves original assignment", results);
                RunMergeRegressionChecks(results, owned);
                RunCompressionClipRegressionChecks(results, owned);
                results.Add("PASS: " + results.Count(r => r.StartsWith("PASS:", StringComparison.Ordinal)) + " assertions; Unity " + Application.unityVersion);
            }
            catch (Exception ex) { results.Add("FAIL: " + ex); throw; }
            finally
            {
                foreach (var obj in owned) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
                WriteReport("guarded-pass-regressions.txt", results);
            }
        }

        private static void RunCompressionClipRegressionChecks(List<string> results, List<UnityEngine.Object> owned)
        {
            var ctor = typeof(nadena.dev.ndmf.BuildContext).GetConstructor(new[] { typeof(GameObject), typeof(string), typeof(bool) });
            if (ctor == null) { results.Add("SKIP: compression clip registration requires real BuildContext"); return; }
            var root = new GameObject("Compression clip regression"); root.SetActive(false); owned.Add(root);
            var opt = root.AddComponent<AvatarOptimizer>();
            // A shader without texture properties isolates material-key rewriting from compression quality.
            var material = new Material(Shader.Find("Unlit/Color")); owned.Add(material);
            var replacementMaterial = new Material(material); owned.Add(replacementMaterial);
            var baseClip = new AnimationClip { name = "Compression base" }; owned.Add(baseClip);
            var swapClip = new AnimationClip { name = "Compression override" }; owned.Add(swapClip);
            var unchanged = new AnimationClip { name = "Unchanged" }; owned.Add(unchanged);
            var bindings = new[] { 0, 1 }.Select(slot => EditorCurveBinding.PPtrCurve("Mesh", typeof(SkinnedMeshRenderer),
                "m_Materials.Array.data[" + slot + "]")).ToArray();
            foreach (var clip in new[] { baseClip, swapClip })
                foreach (var binding in bindings)
                    AnimationUtility.SetObjectReferenceCurve(clip, binding,
                        new[] { new ObjectReferenceKeyframe { time = 0, value = material } });
            var controller = new UnityEditor.Animations.AnimatorController { name = "Compression controller" }; owned.Add(controller);
            controller.AddLayer("Base"); var machine = controller.layers[0].stateMachine; owned.Add(machine);
            var state = machine.AddState("Changed"); owned.Add(state); state.motion = baseClip;
            var unchangedState = machine.AddState("Unchanged"); owned.Add(unchangedState); unchangedState.motion = unchanged;
            var overrides = new AnimatorOverrideController(controller); owned.Add(overrides); overrides[baseClip] = swapClip;
            var animator = root.AddComponent<Animator>(); animator.runtimeAnimatorController = overrides;
            var context = (nadena.dev.ndmf.BuildContext)ctor.Invoke(new object[] { root, null, true });
            var container = typeof(nadena.dev.ndmf.BuildContext).GetProperty("AssetContainer")?.GetValue(context) as UnityEngine.Object;
            if (container != null) owned.Add(container);
            var registry = typeof(nadena.dev.ndmf.BuildContext).GetProperty("ObjectRegistry").GetValue(context);
            var scopeType = typeof(nadena.dev.ndmf.BuildContext).Assembly.GetType("nadena.dev.ndmf.ObjectRegistryScope");
            using var registryScope = (IDisposable)Activator.CreateInstance(scopeType, registry);
            var processor = new AvatarOptimizationProcessor(context, opt);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var copies = (Dictionary<Material, Material>)typeof(AvatarOptimizationProcessor).GetField("materialCopies", flags).GetValue(processor);
            copies.Add(material, replacementMaterial);
            typeof(AvatarOptimizationProcessor).GetMethod("ApplyStandaloneAdaptiveCompression", flags).Invoke(processor, null);
            var output = (AnimatorOverrideController)animator.runtimeAnimatorController; owned.Add(output);
            var outputBase = (UnityEditor.Animations.AnimatorController)output.runtimeAnimatorController; owned.Add(outputBase);
            var outputMachine = outputBase.layers[0].stateMachine; owned.Add(outputMachine);
            foreach (var child in outputMachine.states) owned.Add(child.state);
            var outputBaseClip = (AnimationClip)outputMachine.states.First(s => s.state.name.StartsWith("Changed", StringComparison.Ordinal)).state.motion;
            var outputClip = output[outputBaseClip]; owned.Add(outputBaseClip); owned.Add(outputClip);
            Check(output != overrides && outputBase != controller && outputBaseClip != baseClip && outputClip != swapClip,
                "adaptive compression copies and reattaches override, base controller and affected clips", results);
            foreach (var pair in new[] { (source: baseClip, copy: outputBaseClip), (source: swapClip, copy: outputClip) })
            {
                Check(bindings.All(b => AnimationUtility.GetObjectReferenceCurve(pair.copy, b)[0].value == replacementMaterial),
                    "adaptive compression rewrites every material slot: " + pair.source.name, results);
                Check(ReferenceEquals(nadena.dev.ndmf.ObjectRegistry.GetReference(pair.source), nadena.dev.ndmf.ObjectRegistry.GetReference(pair.copy)),
                    "compression clip retains NDMF replacement identity: " + pair.source.name, results);
                Check(bindings.All(b => AnimationUtility.GetObjectReferenceCurve(pair.source, b)[0].value == material),
                    "compression leaves source material keys intact: " + pair.source.name, results);
            }
            Check(outputMachine.states.First(s => s.state.name.StartsWith("Unchanged", StringComparison.Ordinal)).state.motion == unchanged &&
                overrides.runtimeAnimatorController == controller && overrides[baseClip] == swapClip && state.motion == baseClip,
                "compression preserves unchanged clips and source controller graph", results);
        }

        private static void RunMergeRegressionChecks(List<string> results, List<UnityEngine.Object> owned)
        {
            var root = new GameObject("Merge regression"); root.SetActive(false); owned.Add(root);
            var opt = root.AddComponent<AvatarOptimizer>(); opt.meshSettings.mergeToggleObjects = true;
            opt.meshSettings.preserveBlendshapesWhenCombining = true;
            var material = new Material(Shader.Find("lilToon") ?? Shader.Find("Unlit/Color")); owned.Add(material);
            material.SetOverrideTag("RenderType", "TransparentCutout"); material.renderQueue = 2450;
            var property = material.HasProperty("_MainTexHSVG") ? "_MainTexHSVG" : "_Color";
            var propertyBinding = "material." + property + (property == "_Color" ? ".r" : ".x");
            var alternate = new Material(material); owned.Add(alternate);
            var bones = Enumerable.Range(0, 5).Select(i => { var b = new GameObject("bone" + i); b.transform.SetParent(root.transform, false); return b.transform; }).ToArray();
            var renderers = new List<SkinnedMeshRenderer>();
            foreach (var name in new[] { "A", "B" })
            {
                var go = new GameObject(name); go.transform.SetParent(root.transform, false);
                var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderers.Add(renderer);
                var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one },
                    normals = Enumerable.Repeat(Vector3.forward, 4).ToArray(), tangents = Enumerable.Repeat(new Vector4(1,0,0,-1),4).ToArray(),
                    colors = Enumerable.Repeat(Color.red,4).ToArray(), uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one },
                    bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray(), subMeshCount = 2 };
                owned.Add(mesh); mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {1,3,2},1);
                mesh.SetUVs(1, Enumerable.Repeat(new Vector4(.1f,.2f,.3f,.4f),4).ToList());
                GuardedAvatarPasses.WriteWeights(mesh, Enumerable.Range(0,4).Select(_ => Enumerable.Range(0,5)
                    .Select(i => new BoneWeight1 { boneIndex = i, weight = .2f }).ToList()).ToList());
                var dp = Enumerable.Repeat(Vector3.forward * .01f,4).ToArray();
                mesh.AddBlendShapeFrame("Expression",50,dp,dp,dp); mesh.AddBlendShapeFrame("Expression",100,dp.Select(v=>v*2).ToArray(),dp,dp);
                renderer.sharedMesh = mesh; renderer.bones = bones; renderer.sharedMaterials = new[] { material, material };
                renderer.SetBlendShapeWeight(0, name == "A" ? 25 : 75);
            }
            var baseClip = new AnimationClip { name = "Base slots" }; var swapClip = new AnimationClip { name = "Override slots" };
            owned.Add(baseClip); owned.Add(swapClip);
            foreach (var clip in new[] { baseClip, swapClip })
            {
                foreach (var path in new[] { "A", "B" })
                {
                    var slot = path == "A" ? 0 : 1;
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(path, typeof(SkinnedMeshRenderer), "m_Materials.Array.data[" + slot + "]"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = material }, new ObjectReferenceKeyframe { time = 1, value = alternate } });
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), propertyBinding), AnimationCurve.Linear(0,0,1,1));
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"),
                        AnimationCurve.Constant(0,1,path == "A" ? 1 : 0));
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Expression"),
                        AnimationCurve.Constant(0,1,path == "A" ? 25 : 75));
                }
            }
            var controller = new UnityEditor.Animations.AnimatorController { name = "Merge base" }; owned.Add(controller);
            controller.AddLayer("Base"); var machine = controller.layers[0].stateMachine; owned.Add(machine);
            var state = machine.AddState("State"); owned.Add(state); state.motion = baseClip;
            var mask = new AvatarMask { name = "Merge mask", transformCount = 3 }; owned.Add(mask);
            mask.SetTransformPath(0, ""); mask.SetTransformActive(0, true);
            mask.SetTransformPath(1, "A"); mask.SetTransformActive(1, true);
            mask.SetTransformPath(2, "B"); mask.SetTransformActive(2, true);
            var layers = controller.layers; layers[0].avatarMask = mask; controller.layers = layers;
            var overrides = new AnimatorOverrideController(controller); owned.Add(overrides); overrides[baseClip] = swapClip;
            var avatarType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ABI.CCK.Components.CVRAvatar")).FirstOrDefault(t => t != null);
            Component avatar = null;
            if (avatarType != null) { avatar = root.AddComponent(avatarType); avatarType.GetField("overrides").SetValue(avatar, overrides); }
            else root.AddComponent<Animator>().runtimeAnimatorController = overrides;
            var settingsField = avatar != null ? avatarType.GetField("avatarSettings") : null;
            var authoringSettings = settingsField != null ? Activator.CreateInstance(settingsField.FieldType) : null;
            if (settingsField != null) settingsField.SetValue(avatar, authoringSettings);
            var authoringBase = authoringSettings?.GetType().GetField("baseController");
            if (authoringBase != null) authoringBase.SetValue(authoringSettings, controller);
            var authoringOverride = authoringSettings?.GetType().GetField("baseOverrideController");
            if (authoringOverride != null) authoringOverride.SetValue(authoringSettings, overrides);
            Check(GuardedAvatarPasses.RendererMergeRejection(opt, renderers[0]) == null, "cutout, animated slots, multiple submeshes and five influences are merge eligible", results);
            var overlayObject = new GameObject("Overlay"); overlayObject.transform.SetParent(renderers[1].transform, false);
            var overlay = overlayObject.AddComponent<SkinnedMeshRenderer>(); overlay.sharedMesh = renderers[1].sharedMesh; overlay.bones = bones;
            var transparent = new Material(material); owned.Add(transparent); transparent.SetOverrideTag("RenderType", "Transparent"); transparent.renderQueue = 3000;
            overlay.sharedMaterials = new[] { transparent, transparent };
            Check(GuardedAvatarPasses.CanPrepareToggle(opt, renderers[1].transform), "transparent child does not block eligible parent toggle", results);
            Check(GuardedAvatarPasses.MaterialAnimationMergeRejection(opt, renderers[0], renderers[1]) == null, "matching shader-property curves are merge compatible", results);
            var differing = EditorCurveBinding.FloatCurve("B", typeof(SkinnedMeshRenderer), propertyBinding);
            AnimationUtility.SetEditorCurve(swapClip, differing, AnimationCurve.Linear(0,0,1,.5f));
            Check(GuardedAvatarPasses.MaterialAnimationMergeRejection(opt, renderers[0], renderers[1]) != null, "different shader-property curves cannot overwrite one another", results);
            AnimationUtility.SetEditorCurve(swapClip, differing, null);
            Check(GuardedAvatarPasses.MaterialAnimationMergeRejection(opt, renderers[0], renderers[1]) != null, "missing shader-property curve remains independent", results);
            AnimationUtility.SetEditorCurve(swapClip, differing, AnimationCurve.Linear(0,0,1,1));
            Check(GuardedAvatarPasses.CanPrepareToggle(opt, renderers[0].transform), "Merge Toggle Objects alone authorizes conversion with zero optional limits", results);
            var built = GuardedAvatarPasses.BuildMergedMesh(opt, renderers, out var mergedBones, out var mergedMaterials, out var slots, out var shapes); owned.Add(built);
            Check(built.vertexCount == 8 && built.triangles.Length == 12 && built.subMeshCount == 3,
                "shared submesh vertices copied once; only static identical slots collapse", results);
            Check(slots[renderers[0]][0] != slots[renderers[1]][1] && mergedMaterials.Length == 3,
                "independent animated slots retain separate destinations", results);
            Check(GuardedAvatarPasses.ReadWeights(built).All(w => w.Count == 5 && Mathf.Abs(w.Sum(v=>v.weight)-1) < .0001f) && mergedBones.Length == 10,
                "variable-length weights and all bind poses preserved", results);
            var shape = built.GetBlendShapeIndex(shapes[renderers[1]]["Expression"]);
            var positions = new Vector3[8]; var normals = new Vector3[8]; var tangents = new Vector3[8];
            built.GetBlendShapeFrameVertices(shape,1,positions,normals,tangents);
            Check(built.blendShapeCount == 2 && built.GetBlendShapeFrameCount(shape) == 2 && positions[0] == Vector3.zero && positions[4] == Vector3.forward * .02f && normals[4] == Vector3.forward * .01f && tangents[4] == normals[4],
                "same-name shapes remain independent with every frame, normal and tangent delta", results);
            Check(built.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord1) == 4 && built.colors.Length == 8 && built.tangents[7].w == -1,
                "UV dimensions, colors and tangent handedness preserved", results);
            alternate.SetOverrideTag("RenderType", "Transparent"); alternate.renderQueue = 3000;
            Check(GuardedAvatarPasses.RendererMergeRejection(opt, renderers[0]) != null, "transparent material reached only by swap is rejected", results);
            alternate.SetOverrideTag("RenderType", "TransparentCutout"); alternate.renderQueue = 2450;
            var ctor = typeof(nadena.dev.ndmf.BuildContext).GetConstructor(new[] { typeof(GameObject), typeof(string), typeof(bool) });
            if (ctor == null) { results.Add("SKIP: full NDMF merge execution requires real BuildContext"); return; }
            var context = (nadena.dev.ndmf.BuildContext)ctor.Invoke(new object[] { root, null, true });
            var container = typeof(nadena.dev.ndmf.BuildContext).GetProperty("AssetContainer")?.GetValue(context) as UnityEngine.Object;
            if (container != null) owned.Add(container);
            var registry = typeof(nadena.dev.ndmf.BuildContext).GetProperty("ObjectRegistry").GetValue(context);
            var scopeType = typeof(nadena.dev.ndmf.BuildContext).Assembly.GetType("nadena.dev.ndmf.ObjectRegistryScope");
            using var registryScope = (IDisposable)Activator.CreateInstance(scopeType, registry);
            var oldMeshA = renderers[0].sharedMesh; var oldMeshB = renderers[1].sharedMesh;
            renderers[1].gameObject.SetActive(false);
            var processor = new AvatarOptimizationProcessor(context, opt);
            processor.PrepareTogglePreservingMerge();
            foreach (var r in renderers) owned.Add(r.sharedMesh);
            // A fresh processor recognizes toggle meshes already prepared on the build clone.
            processor = new AvatarOptimizationProcessor(context, opt);
            typeof(AvatarOptimizationProcessor).GetMethod("CombineMeshes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(processor, null);
            var survivor = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r != overlay).ToArray();
            Check(survivor.Length == 1 && survivor[0].sharedMesh.vertexCount == 8, "NDMF processor handoff produces one renderer with no submesh vertex duplication", results);
            owned.Add(survivor[0].sharedMesh);
            var output = (AnimatorOverrideController)(avatar != null ? avatarType.GetField("overrides").GetValue(avatar) : root.GetComponent<Animator>().runtimeAnimatorController);
            var outputBase = (UnityEditor.Animations.AnimatorController)output.runtimeAnimatorController;
            var outputMask = outputBase.layers[0].avatarMask; owned.Add(outputMask);
            var mergedPath = AnimationUtility.CalculateTransformPath(survivor[0].transform, root.transform);
            Check(outputMask != mask && Enumerable.Range(0, outputMask.transformCount).Any(i => outputMask.GetTransformPath(i) == mergedPath && outputMask.GetTransformActive(i)) && mask.transformCount == 3,
                "copied AvatarMask explicitly covers the new renderer without changing source mask", results);
            var outputBaseClip = (AnimationClip)outputBase.layers[0].stateMachine.states[0].state.motion;
            var outputClip = output[outputBaseClip];
            owned.Add(output); owned.Add(outputBase); owned.Add(outputBaseClip); owned.Add(outputClip); owned.Add(outputBase.layers[0].stateMachine); owned.Add(outputBase.layers[0].stateMachine.states[0].state);
            if (authoringBase != null) Check(ReferenceEquals(authoringBase.GetValue(authoringSettings), controller),
                "CCK authoring baseController remains the original asset reference", results);
            if (authoringOverride != null) Check(ReferenceEquals(authoringOverride.GetValue(authoringSettings), overrides),
                "CCK authoring baseOverrideController remains the original asset reference", results);
            Check(output != overrides && outputBase != controller && outputBaseClip != baseClip && outputClip != swapClip,
                "CVR override, underlying controller, base clip and override clip all copied and reattached", results);
            foreach (var clip in new[] { outputBaseClip, outputClip })
            {
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                Check(bindings.Length == 2 && bindings.All(b => b.path == AnimationUtility.CalculateTransformPath(survivor[0].transform, root.transform)) && bindings.Select(b=>b.propertyName).Distinct().Count() == 2 &&
                    bindings.All(b => AnimationUtility.GetObjectReferenceCurve(clip,b)[1].value == alternate),
                    "copied clip remaps independent material swaps without overwriting: " + clip.name, results);
            }
            var outputPath = AnimationUtility.CalculateTransformPath(survivor[0].transform, root.transform);
            var animatedProperties = AnimationUtility.GetCurveBindings(outputClip).Where(b => b.propertyName == propertyBinding).ToArray();
            Check(animatedProperties.Length == 1 && animatedProperties[0].path == outputPath &&
                Mathf.Abs(AnimationUtility.GetEditorCurve(outputClip, animatedProperties[0]).Evaluate(.5f) - .5f) < .001f,
                "matching shader-property curves collapse to one equivalent output curve", results);
            var retainedToggle = AnimationUtility.GetEditorCurve(outputClip, EditorCurveBinding.FloatCurve("B",typeof(GameObject),"m_IsActive"));
            Check(retainedToggle != null && retainedToggle.Evaluate(0) == 0 && !overlayObject.transform.parent.gameObject.activeSelf && survivor[0].gameObject.activeSelf,
                "source toggle and transparent overlay remain off while sibling merged renderer stays active", results);
            Check(overrides.runtimeAnimatorController == controller && state.motion == baseClip && overrides[baseClip] == swapClip &&
                AnimationUtility.GetObjectReferenceCurveBindings(swapClip).Any(b=>b.path == "B") && oldMeshA.vertexCount == 4 && oldMeshB.vertexCount == 4,
                "original override, controller, clip bindings and mesh assets unchanged", results);
            var mergedMesh = survivor[0].sharedMesh;
            Check(Enumerable.Range(0,mergedMesh.blendShapeCount).Any(i=>mergedMesh.GetBlendShapeName(i).StartsWith("MT_Hide_B") && survivor[0].GetBlendShapeWeight(i) == 100),
                "initially inactive toggle retains hidden state after merging", results);
        }

        [MenuItem("Tools/Avatar Optimizer/Test Rumoka merge on temporary clone")]
        public static void RunRumokaMergeSmokeTest()
        {
            var source = FindBenchmarkAvatar();
            var rows = new List<string> { "Merge-only test on a temporary scene clone; no scene or asset saved.", "Source: " + source.name };
            var existing = new HashSet<int>(Resources.FindObjectsOfTypeAll<UnityEngine.Object>().Select(o => o.GetInstanceID()));
            var staging = new GameObject("Merge validation staging"); staging.SetActive(false);
            try
            {
                var clone = UnityEngine.Object.Instantiate(source, staging.transform);
                var opt = clone.GetComponent<AvatarOptimizer>() ?? clone.AddComponent<AvatarOptimizer>();
                opt.reportOnly = false; opt.meshSettings.mergeToggleObjects = true; opt.meshSettings.combineMeshes = true;
                int before = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                var ctor = typeof(nadena.dev.ndmf.BuildContext).GetConstructor(new[] { typeof(GameObject), typeof(string), typeof(bool) });
                var context = (nadena.dev.ndmf.BuildContext)ctor.Invoke(new object[] { clone, null, true });
                var registry = typeof(nadena.dev.ndmf.BuildContext).GetProperty("ObjectRegistry").GetValue(context);
                using var registryScope = (IDisposable)Activator.CreateInstance(typeof(nadena.dev.ndmf.BuildContext).Assembly.GetType("nadena.dev.ndmf.ObjectRegistryScope"), registry);
                var processor = new AvatarOptimizationProcessor(context, opt);
                processor.PrepareTogglePreservingMerge();
                processor = new AvatarOptimizationProcessor(context, opt);
                typeof(AvatarOptimizationProcessor).GetMethod("CombineMeshes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(processor, null);
                processor.FlushBufferedLog();
                int after = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                rows.Add("Renderers: " + before + " -> " + after);
                foreach (var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name.Contains("_Combined")))
                    rows.Add(renderer.name + ": " + renderer.sharedMesh.vertexCount + " vertices, " + renderer.sharedMesh.subMeshCount + " slots");
                Check(after < before, "representative avatar produces a real merge", rows);
            }
            catch (Exception ex) { rows.Add("FAIL: " + ex); throw; }
            finally
            {
                UnityEngine.Object.DestroyImmediate(staging);
                foreach (var asset in Resources.FindObjectsOfTypeAll<UnityEngine.Object>().Where(o => !existing.Contains(o.GetInstanceID()) && !EditorUtility.IsPersistent(o) &&
                    (o is Mesh || o is AnimationClip || o is RuntimeAnimatorController || o is UnityEditor.Animations.AnimatorState ||
                     o is UnityEditor.Animations.AnimatorStateMachine || o is UnityEditor.Animations.AnimatorTransitionBase || o is UnityEditor.Animations.BlendTree || o is AvatarMask)).ToArray())
                    UnityEngine.Object.DestroyImmediate(asset);
                WriteReport("rumoka-merge-smoke.txt", rows);
            }
        }

        private static void Check(bool condition, string name, List<string> results)
        {
            if (!condition) throw new InvalidOperationException(name);
            results.Add("PASS: " + name);
        }

        [MenuItem("Tools/Avatar Optimizer/Inventory Rumoka benchmark prefab")]
        public static void InventoryRumoka()
        {
            var asset = FindBenchmarkAvatar();
            var staging = new GameObject("Optimizer benchmark staging"); staging.SetActive(false);
            var rows = new List<string> { "Read-only avatar inventory; no scene or asset saved.", "Unity: " + Application.unityVersion,
                "Source: " + asset.scene.path + " / " + asset.name + " / " + AssetDatabase.GetAssetPath(asset) };
            try
            {
                var clone = UnityEngine.Object.Instantiate(asset, staging.transform);
                var opt = clone.GetComponent<AvatarOptimizer>() ?? clone.AddComponent<AvatarOptimizer>();
                var renderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                rows.Add("Skinned renderers: " + renderers.Length);
                rows.Add("Vertices: " + renderers.Where(r => r.sharedMesh != null).Sum(r => (long)r.sharedMesh.vertexCount));
                rows.Add("Reachable clips: " + GuardedAvatarPasses.Clips(opt).Select(u => u.clip).Distinct().Count());
                foreach (var renderer in renderers.Where(r => r.sharedMesh != null))
                {
                    var mesh = renderer.sharedMesh;
                    rows.Add(AnimationUtility.CalculateTransformPath(renderer.transform, clone.transform) +
                        " | vertices=" + mesh.vertexCount + " | shapes=" + mesh.blendShapeCount + " | bones=" + renderer.bones.Length +
                        " | simplification=" + (GuardedAvatarPasses.GeometryExclusion(opt, renderer) ?? "eligible for pose validation") +
                        " | merge=" + (GuardedAvatarPasses.RendererMergeRejection(opt, renderer) ?? "eligible for grouping"));
                }
                rows.Add(GuardedAvatarPasses.DescribeMergePlan(opt));
            }
            finally { UnityEngine.Object.DestroyImmediate(staging); WriteReport("rumoka-prefab-inventory.txt", rows); }
        }

        private static void WriteReport(string name, IEnumerable<string> lines)
        {
            string directory = System.IO.Path.Combine(Application.dataPath, "../Library/AvatarOptimizerValidation");
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name));
            System.IO.File.WriteAllLines(path, lines);
            Debug.Log("[AvatarOptimizer] Validation report: " + path + "\n" + string.Join("\n", lines));
        }

        [Serializable]
        private sealed class GeometrySample
        {
            public string name;
            public Vector3[] vertices;
            public int[] indices;
        }

        // Development comparison only. Candidates are measured and destroyed, never assigned.
        // An optional externally loaded Meshia assembly participates without a package dependency.
        [MenuItem("Tools/Avatar Optimizer/Compare Rumoka simplification candidates")]
        public static void CompareRumokaBackends()
        {
            var asset = FindBenchmarkAvatar();
            var rows = new List<string> { "Candidate comparison only; no avatar changes. One cold run per mesh; timings are not a backend ranking.",
                "Target: 75% triangles; border preservation enabled; smart linking disabled where supported.",
                "Corpus: three quantiles of unique readable meshes <= 30000 vertices and <= 64 shapes; larger meshes excluded from this pilot.",
                "Source: " + asset.scene.path + " / " + asset.name + " / " + AssetDatabase.GetAssetPath(asset) };
            var meshes = asset.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh)
                .Where(m => m != null && m.isReadable && m.vertexCount > 0 && m.vertexCount <= 30000 && m.blendShapeCount <= 64)
                .Distinct().OrderBy(m => m.vertexCount).ToArray();
            string directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Library/AvatarOptimizerValidation"));
            System.IO.Directory.CreateDirectory(directory);
            if (meshes.Length == 0) { rows.Add("No eligible pilot meshes."); WriteReport("rumoka-backend-comparison.txt", rows); return; }
            var corpus = new[] { meshes[0], meshes[meshes.Length / 2], meshes[meshes.Length - 1] }.Distinct().ToArray();
            foreach (var mesh in corpus)
            {
                string id = Array.IndexOf(corpus, mesh).ToString();
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "corpus-" + id + ".json"),
                    JsonUtility.ToJson(new GeometrySample { name = mesh.name, vertices = mesh.vertices, indices = mesh.triangles }));
                rows.Add("CORPUS " + id + ": " + mesh.name + "; vertices=" + mesh.vertexCount + "; triangles=" + mesh.triangles.Length / 3 + "; shapes=" + mesh.blendShapeCount);
                foreach (string typeName in new[] { "MilchZocker.SimplificationPrototype.MeshSimplifier", "Meshia.MeshSimplification.MeshSimplifier" })
                {
                    Mesh result = null;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
                        if (type == null) throw new InvalidOperationException("backend assembly not loaded");
                        if (typeName.StartsWith("MilchZocker", StringComparison.Ordinal))
                        {
                            if (GuardedAvatarPasses.HasManyWeights(mesh)) throw new InvalidOperationException("more than four influences: incompatible input");
                            var instance = Activator.CreateInstance(type);
                            var property = type.GetProperty("SimplificationOptions");
                            var options = property.GetValue(instance);
                            foreach (var field in new[] { "PreserveBorderEdges", "PreserveUVSeamEdges", "PreserveUVFoldoverEdges", "PreserveSurfaceCurvature" })
                                options.GetType().GetField(field)?.SetValue(options, true);
                            options.GetType().GetField("EnableSmartLink")?.SetValue(options, false);
                            property.SetValue(instance, options);
                            type.GetMethod("Initialize", new[] { typeof(Mesh) }).Invoke(instance, new object[] { mesh });
                            type.GetMethod("SimplifyMesh", new[] { typeof(float) }).Invoke(instance, new object[] { 0.75f });
                            result = (Mesh)type.GetMethod("ToMesh").Invoke(instance, null);
                        }
                        else
                        {
                            var targetType = type.Assembly.GetType("Meshia.MeshSimplification.MeshSimplificationTarget");
                            var target = Activator.CreateInstance(targetType);
                            targetType.GetField("Kind").SetValue(target, Enum.Parse(targetType.GetField("Kind").FieldType, "RelativeTriangleCount"));
                            targetType.GetField("Value").SetValue(target, 0.75f);
                            var optionsType = type.Assembly.GetType("Meshia.MeshSimplification.MeshSimplifierOptions");
                            var options = optionsType.GetProperty("Default").GetValue(null);
                            optionsType.GetField("PreserveBorderEdges").SetValue(options, true);
                            optionsType.GetField("PreserveSurfaceCurvature").SetValue(options, true);
                            optionsType.GetField("EnableSmartLink").SetValue(options, false);
                            result = new Mesh();
                            type.GetMethods().Single(m => m.Name == "Simplify" && m.GetParameters().Length == 4)
                                .Invoke(null, new[] { (object)mesh, target, options, result });
                        }
                        timer.Stop();
                        rows.Add(typeName + ": " + result.vertexCount + " vertices, " + result.triangles.Length / 3 + " triangles, " + timer.ElapsedMilliseconds +
                            " ms, stream-layout=" + GuardedAvatarPasses.StreamsCompatible(mesh, result) + "; pose acceptance NOT established by this microbenchmark.");
                    }
                    catch (Exception ex) { rows.Add(typeName + ": unavailable/rejected: " + ex.GetBaseException().Message); }
                    finally { if (result != null) UnityEngine.Object.DestroyImmediate(result); }
                    WriteReport("rumoka-backend-comparison.txt", rows);
                }
            }
        }

        private static GameObject FindBenchmarkAvatar()
        {
            var avatars = new List<GameObject>();
            for (int sceneIndex = 0; sceneIndex < UnityEngine.SceneManagement.SceneManager.sceneCount; sceneIndex++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene)) continue;
                foreach (var root in scene.GetRootGameObjects())
                    avatars.AddRange(root.GetComponentsInChildren<Component>(true)
                        .Where(c => c != null && c.GetType().FullName == "ABI.CCK.Components.CVRAvatar").Select(c => c.gameObject));
            }
            var selected = avatars.Distinct().OrderByDescending(a => a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length).FirstOrDefault();
            if (selected == null) throw new InvalidOperationException("No CVR avatar is loaded. Open the representative avatar scene before benchmarking.");
            return selected;
        }
    }
}

#endif
