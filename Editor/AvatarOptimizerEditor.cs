#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

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
            EditorGUILayout.Space(5);

            // Mesh Settings
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
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(5);

            // Blendshape Settings
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
            EditorGUILayout.Space(5);

            // Atlas Settings
            showAtlasSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showAtlasSettings, "Texture Atlas Generation");
            if (showAtlasSettings && atlasSettingsProp != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("generateTextureAtlas"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Animation Safety", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeAnimatedMaterials"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanOverrideController"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("scanAdvancedAvatarSettings"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludeMaterialPatterns"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Atlas Generation Mode", EditorStyles.boldLabel);

                // --- SAFE DRAW: serialized if possible, fallback if not ---
                var enhancedWorkflowProp = atlasSettingsProp.FindPropertyRelative("useEnhancedAtlasWorkflow");
                if (enhancedWorkflowProp != null)
                {
                    EditorGUILayout.PropertyField(enhancedWorkflowProp, new GUIContent("Use Enhanced Workflow"));
                    if (enhancedWorkflowProp.boolValue)
                    {
                        EditorGUILayout.HelpBox(
                            "Enhanced Workflow: Uses improved material grouping and optimization strategies. No external dependencies required.",
                            MessageType.Info);
                    }
                }
                else
                {
                    // Fallback to direct field access if Unity can't find the serialized property
                    var opt = (AvatarOptimizer)target;
                    bool newValue = EditorGUILayout.ToggleLeft("Use Enhanced Workflow", opt.atlasSettings.useEnhancedAtlasWorkflow);
                    if (newValue != opt.atlasSettings.useEnhancedAtlasWorkflow)
                    {
                        Undo.RecordObject(opt, "Toggle Enhanced Workflow");
                        opt.atlasSettings.useEnhancedAtlasWorkflow = newValue;
                        EditorUtility.SetDirty(opt);
                    }

                    if (newValue)
                    {
                        EditorGUILayout.HelpBox(
                            "Enhanced Workflow: Uses improved material grouping and optimization strategies. No external dependencies required.",
                            MessageType.Info);
                    }
                }

                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("mergeIdenticalTextures"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Basic Atlas Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("maxAtlasSize"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("atlasPadding"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Advanced Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumMaterialsForAtlas"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedTextureProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedTextureProperties"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("minimumTextureSize"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Shader Filtering", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("allowedShaderNames"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("excludedShaderNames"));

                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("compressAtlases"));
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("compressionFormat"));

                EditorGUILayout.Space(5);
                EditorGUILayout.PropertyField(atlasSettingsProp.FindPropertyRelative("verboseLogging"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(5);

            // Stats (Debug) - read-only labels, numbers only
            showStats = EditorGUILayout.BeginFoldoutHeaderGroup(showStats, "Debug & Statistics");
            if (showStats)
            {
                EditorGUI.indentLevel++;

                // Prefer serialized stats if available
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
                    // Fallback: direct access so stats never disappear
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

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawStatInt(string label, SerializedProperty prop)
        {
            if (prop == null) return;
            EditorGUILayout.LabelField(label, prop.intValue.ToString());
        }
    }
}
#endif
