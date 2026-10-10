#if UNITY_EDITOR
using nadena.dev.ndmf;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using System.Text.RegularExpressions;

[assembly: ExportsPlugin(typeof(MilchZocker.AvatarOptimizer.CVRAvatarOptimizerPlugin))]

namespace MilchZocker.AvatarOptimizer
{
    #region NDMF registration and build phases

    public class CVRAvatarOptimizerPlugin : Plugin<CVRAvatarOptimizerPlugin>
    {
        public override string QualifiedName => "dev.milchzocker.ndmf-avatar-optimisation";
        public override string DisplayName => "Avatar Optimiser";

        protected override void Configure()
        {
            // CCK/AAS generation runs during Transforming and needs its saved authoring
            // controller. Prepare toggles and copy runtime animation graphs only afterwards.
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("dev.milchzocker.ndmf-merge")
                .Run("Optimise Avatar", ctx =>
                {
                    var optimizer = ctx.AvatarRootTransform.GetComponent<AvatarOptimizer>();
                    if (optimizer == null) return;

                    Debug.Log($"[AvatarOptimizer] Found Avatar Optimizer component on {ctx.AvatarRootObject.name}");

                    var processor = new AvatarOptimizationProcessor(ctx, optimizer);
                    processor.Process();
                });
        }
    }

    #endregion

    #region Avatar processing

    public class AvatarOptimizationProcessor
    {
        private readonly BuildContext context;
        private readonly AvatarOptimizer optimizer;
        private readonly Component avatar;
        private readonly HashSet<string> usedBlendshapes = new HashSet<string>();
        private readonly HashSet<Transform> animatedBones = new HashSet<Transform>();
        private readonly HashSet<Material> animatedMaterials = new HashSet<Material>();
        private readonly Dictionary<Material, List<string>> animatedMaterialProperties = new Dictionary<Material, List<string>>();
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> optimizedMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly Dictionary<Material, Material> materialCopies = new Dictionary<Material, Material>();
        private readonly Dictionary<Transform, HashSet<string>> animatedTransformProperties = new Dictionary<Transform, HashSet<string>>();
        private readonly List<OptimizationIssue> optimizationIssues = new List<OptimizationIssue>();

        private readonly Dictionary<Texture2D, TextureDensityAnalysis> atlasImportSettings = new Dictionary<Texture2D, TextureDensityAnalysis>();
        private readonly Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
        private readonly Dictionary<Texture2D, string> textureFingerprintCache = new Dictionary<Texture2D, string>();

        private readonly Dictionary<Texture2D, long> originalTextureMemory = new Dictionary<Texture2D, long>();
        private readonly HashSet<Texture2D> accountedAtlases = new HashSet<Texture2D>();
        private long totalAtlasMemoryBytes = 0;

        public struct MemoryEstimate
        {
            public long currentBytes;
            public long optimizedBytes;
            public float savingsPercent;
            public int meshCount;
            public int textureCount;
        }

        public class MaterialAtlasAnalysis
        {
            public class MaterialInfo
            {
                public Material material;
                public string shaderName;
                public bool canAtlas;
                public List<string> reasons = new List<string>();
                public List<string> compatibleProperties = new List<string>();
                public List<string> incompatibleProperties = new List<string>();
                public int groupId = -1;
                public List<Material> canAtlasWith = new List<Material>();
                public string animatedSignature = string.Empty;
            }

            public List<MaterialInfo> materials = new List<MaterialInfo>();
            public Dictionary<string, List<MaterialInfo>> materialsByShader = new Dictionary<string, List<MaterialInfo>>();
            public List<List<MaterialInfo>> atlasGroups = new List<List<MaterialInfo>>();
            public int totalMaterials = 0;
            public int atlaseableMaterials = 0;
            public int excludedMaterials = 0;
        }

        private Type cvrAvatarType;
        private Type cvrFaceTrackingType;
        private MaterialAtlasAnalysis atlasAnalysis;

        public enum OptimizationCategory
        {
            Animator,
            Mesh,
            Material,
            Blendshape,
            Physics,
            Shader,
            General
        }

        public enum OptimizationSeverity
        {
            Info,
            Warning,
            Error
        }

        public class OptimizationIssue
        {
            public OptimizationCategory Category;
            public OptimizationSeverity Severity;
            public string Title;
            public string Description;
            public string SuggestedAction;
            public bool CanAutoFix;
            public Component TargetComponent;
            public int? BeforeValue;
            public int? AfterValue;
            public string IssueKey;
        }

        private readonly StringBuilder logBuffer = new StringBuilder(8192);
        private int logIndent = 0;
        private const int MaxLogChunkSize = 12000;

        private void LogBuffered(string msg)
        {
            if (logIndent > 0) logBuffer.Append(' ', logIndent * 2);
            logBuffer.AppendLine(msg);
        }

        private void PushLogIndent() => logIndent++;
        private void PopLogIndent() { if (logIndent > 0) logIndent--; }

        public void FlushBufferedLog()
        {
            if (logBuffer.Length == 0) return;

            string full = logBuffer.ToString();
            logBuffer.Length = 0;

            int total = full.Length;
            int offset = 0;
            int part = 1;
            int parts = Mathf.CeilToInt(total / (float)MaxLogChunkSize);

            while (offset < total)
            {
                int len = Mathf.Min(MaxLogChunkSize, total - offset);
                int lastNewline = full.LastIndexOf('\n', offset + len - 1, len);
                if (lastNewline > offset + 1000)
                {
                    len = lastNewline - offset + 1;
                }

                string chunk = full.Substring(offset, len);
                Debug.Log($"[AvatarOptimizer] ===== Log Part {part}/{parts} =====\n{chunk}");

                offset += len;
                part++;
            }
        }

        public AvatarOptimizationProcessor(BuildContext ctx, AvatarOptimizer opt)
        {
            context = ctx;
            optimizer = opt;

            cvrAvatarType = FindType("ABI.CCK.Components.CVRAvatar");
            cvrFaceTrackingType = FindType("ABI.CCK.Components.CVRFaceTracking");

            if (cvrAvatarType != null && ctx.AvatarRootObject != null)
            {
                avatar = ctx.AvatarRootObject.GetComponentInChildren(cvrAvatarType, true);
            }

            if (avatar == null)
            {
                Debug.LogWarning("[AvatarOptimizer] CVRAvatar component not found in hierarchy; scanning assigned Animator controllers only.");
            }

            toggleMergePrepared = HasPreparedToggleBlendshapes();
        }

        private bool HasPreparedToggleBlendshapes()
        {
            if (!optimizer.meshSettings.mergeToggleObjects)
                return false;

            return context.AvatarRootTransform
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Any(renderer => renderer.sharedMesh != null && Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                    .Any(index => renderer.sharedMesh.GetBlendShapeName(index).StartsWith("MT_Hide_", StringComparison.Ordinal)));
        }

        private bool UseRelaxedToggleMerge()
        {
            return optimizer.meshSettings.mergeToggleObjects && toggleMergePrepared;
        }

        private bool IsAdvancedTaggedTransform(Transform transform)
        {
            if (avatar == null || transform == null ||
                !GetFieldOrPropertyValue<bool>(avatar, "enableAdvancedTagging"))
            {
                return false;
            }

            var entries = GetFieldOrPropertyValue<System.Collections.IEnumerable>(avatar, "advancedTaggingList");
            if (entries == null)
                return false;

            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;

                var taggedObject = GetFieldOrPropertyValue<GameObject>(entry, "gameObject");
                var fallbackObject = GetFieldOrPropertyValue<GameObject>(entry, "fallbackGameObject");
                if ((taggedObject != null && (transform == taggedObject.transform || transform.IsChildOf(taggedObject.transform))) ||
                    (fallbackObject != null && (transform == fallbackObject.transform || transform.IsChildOf(fallbackObject.transform))))
                {
                    return true;
                }
            }

            return false;
        }

        private Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private MaterialAtlasAnalysis AnalyzeMaterialAtlasing()
        {
            var analysis = new MaterialAtlasAnalysis();

            if (!optimizer.atlasSettings.generateTextureAtlas)
            {
                LogBuffered("⚠️ Texture atlasing is disabled - skipping analysis");
                return analysis;
            }

            LogBuffered("🔍 Analyzing Material Atlasing Compatibility...");
            PushLogIndent();

            var renderers = context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true);
            var allMaterials = new HashSet<Material>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null && mat.shader != null)
                        allMaterials.Add(mat);
                }
            }

            allMaterials.UnionWith(MaterialAnimationUtility.GetReferencedMaterials(context.AvatarRootTransform).Where(m => m.shader != null));
            analysis.totalMaterials = allMaterials.Count;
            LogBuffered($"📊 Found {analysis.totalMaterials} unique materials");

            foreach (var mat in allMaterials)
            {
                var info = new MaterialAtlasAnalysis.MaterialInfo
                {
                    material = mat,
                    shaderName = mat.shader.name
                };

                bool excluded = false;

                if (!ShouldIncludeShader(mat.shader))
                {
                    info.canAtlas = false;
                    info.reasons.Add($"❌ Shader '{mat.shader.name}' is in exclusion list");
                    excluded = true;
                }

                info.animatedSignature = GetAnimatedPropertySignature(mat);

                if (info.animatedSignature.Contains(MaterialAnimationUtility.MaterialReferenceProperty, StringComparison.Ordinal))
                {
                    info.canAtlas = false;
                    info.reasons.Add("❌ Material reference is animated");
                    excluded = true;
                }

                if (!excluded && ShouldExcludeMaterialFromAtlas(mat))
                {
                    info.canAtlas = false;
                    if (!string.IsNullOrEmpty(optimizer.atlasSettings.excludeMaterialPatterns))
                    {
                        info.reasons.Add($"❌ Material name matches exclusion pattern");
                    }
                    excluded = true;
                }

                var allowedProps = GetAllowedTexturePropertiesForShader(mat.shader);

                foreach (var prop in allowedProps)
                {
                    if (!mat.HasProperty(prop))
                    {
                        info.incompatibleProperties.Add($"{prop} (not present)");
                        continue;
                    }

                    var tex = mat.GetTexture(prop);
                    if (tex == null)
                    {
                        info.incompatibleProperties.Add($"{prop} (null texture)");
                        continue;
                    }

                    if (tex.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
                    {
                        info.incompatibleProperties.Add($"{prop} ({tex.dimension} texture)");
                        if (!excluded)
                        {
                            info.canAtlas = false;
                            info.reasons.Add($"❌ Property '{prop}' has non-2D texture ({tex.dimension})");
                            excluded = true;
                        }
                        continue;
                    }

                    var tex2D = tex as Texture2D;
                    if (tex2D != null)
                    {
                        int texWidth = tex2D.width;
                        int texHeight = tex2D.height;

                        if (texWidth < optimizer.atlasSettings.minimumTextureSize || 
                            texHeight < optimizer.atlasSettings.minimumTextureSize)
                        {
                            info.incompatibleProperties.Add($"{prop} (too small: {texWidth}x{texHeight})");
                            continue;
                        }

                        info.compatibleProperties.Add($"{prop} ({texWidth}x{texHeight})");
                    }
                }

                if (!excluded && info.compatibleProperties.Count == 0)
                {
                    info.canAtlas = false;
                    info.reasons.Add("❌ No compatible 2D texture properties found");
                    excluded = true;
                }

                if (!excluded)
                {
                    info.canAtlas = true;
                    info.reasons.Add("✅ Can be atlased");
                    analysis.atlaseableMaterials++;
                }
                else
                {
                    analysis.excludedMaterials++;
                }

                analysis.materials.Add(info);

                if (!analysis.materialsByShader.ContainsKey(info.shaderName))
                    analysis.materialsByShader[info.shaderName] = new List<MaterialAtlasAnalysis.MaterialInfo>();

                analysis.materialsByShader[info.shaderName].Add(info);
            }

            LogBuffered($"\n📋 Atlasing Summary:");
            LogBuffered($"   Total Materials: {analysis.totalMaterials}");
            LogBuffered($"   ✅ Atlaseable: {analysis.atlaseableMaterials}");
            LogBuffered($"   ❌ Excluded: {analysis.excludedMaterials}");

            int groupId = 0;
            foreach (var kvp in analysis.materialsByShader)
            {
                var shaderName = kvp.Key;
                var matsForShader = kvp.Value.Where(m => m.canAtlas).ToList();

                foreach (var sigGroup in matsForShader.GroupBy(m => m.animatedSignature))
                {
                    var sigMats = sigGroup.ToList();
                    var sigDesc = DescribeAnimatedSignature(sigGroup.Key);

                    if (sigMats.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
                    {
                        LogBuffered($"\n⚠️ Shader '{shaderName}' ({sigDesc}): {sigMats.Count} atlaseable materials (minimum: {optimizer.atlasSettings.minimumMaterialsForAtlas})");

                        foreach (var info in sigMats)
                        {
                            info.canAtlas = false;
                            info.reasons.Add($"❌ Not enough materials sharing the same animation state ({sigDesc})");
                        }

                        analysis.atlaseableMaterials -= sigMats.Count;
                        analysis.excludedMaterials += sigMats.Count;
                        continue;
                    }

                    LogBuffered($"\n✅ Shader '{shaderName}' ({sigDesc}): {sigMats.Count} materials will be atlased");

                    if (optimizer.atlasSettings.useEnhancedAtlasWorkflow)
                    {
                        var groups = GroupMaterialsByTextureSignature(sigMats);
                        var consolidatedGroups = ConsolidateSmallGroups(groups);

                        foreach (var group in consolidatedGroups)
                        {
                            if (group.Count >= optimizer.atlasSettings.minimumMaterialsForAtlas)
                            {
                                foreach (var info in group)
                                    info.groupId = groupId;

                                analysis.atlasGroups.Add(group);
                                LogBuffered($"   📦 Atlas Group {groupId}: {group.Count} materials with matching textures");
                                PushLogIndent();
                                foreach (var info in group.Take(5))
                                    LogBuffered($"   • {info.material.name}");
                                if (group.Count > 5)
                                    LogBuffered($"   • ... and {group.Count - 5} more");
                                PopLogIndent();
                                groupId++;
                            }
                        }
                    }
                    else
                    {
                        foreach (var info in sigMats)
                            info.groupId = groupId;

                        analysis.atlasGroups.Add(sigMats);
                        LogBuffered($"   📦 Atlas Group {groupId}: All {sigMats.Count} materials");
                        groupId++;
                    }
                }
            }

            if (optimizer.atlasSettings.verboseLogging)
            {
                LogBuffered($"\n🔍 Detailed Material Analysis:");

                foreach (var shaderGroup in analysis.materialsByShader.OrderBy(kvp => kvp.Key))
                {
                    LogBuffered($"\n📘 Shader: {shaderGroup.Key}");
                    PushLogIndent();

                    foreach (var info in shaderGroup.Value.OrderBy(m => m.canAtlas ? 0 : 1))
                    {
                        LogBuffered($"\n🎨 Material: {info.material.name}");
                        PushLogIndent();

                        LogBuffered($"Status: {(info.canAtlas ? "✅ Can Atlas" : "❌ Cannot Atlas")}");

                        if (info.groupId >= 0)
                            LogBuffered($"Atlas Group: #{info.groupId}");

                        foreach (var reason in info.reasons)
                            LogBuffered($"• {reason}");

                        if (info.compatibleProperties.Count > 0)
                        {
                            LogBuffered($"Compatible Properties: {string.Join(", ", info.compatibleProperties)}");
                        }

                        if (info.incompatibleProperties.Count > 0)
                        {
                            LogBuffered($"Incompatible Properties: {string.Join(", ", info.incompatibleProperties)}");
                        }

                        PopLogIndent();
                    }

                    PopLogIndent();
                }
            }

            PopLogIndent();
            LogBuffered($"✅ Material atlasing analysis complete - {analysis.atlasGroups.Count} atlas groups will be generated");

            return analysis;
        }

        private List<List<MaterialAtlasAnalysis.MaterialInfo>> GroupMaterialsByTextureSignature(
            List<MaterialAtlasAnalysis.MaterialInfo> materials)
        {
            var groups = new Dictionary<string, List<MaterialAtlasAnalysis.MaterialInfo>>();

            foreach (var info in materials)
            {
                var signature = BuildTextureSignature(info.material);

                if (!groups.ContainsKey(signature))
                    groups[signature] = new List<MaterialAtlasAnalysis.MaterialInfo>();

                groups[signature].Add(info);
            }

            return groups.Values.ToList();
        }

        private List<List<MaterialAtlasAnalysis.MaterialInfo>> ConsolidateSmallGroups(List<List<MaterialAtlasAnalysis.MaterialInfo>> groups)
        {
            var consolidated = new List<List<MaterialAtlasAnalysis.MaterialInfo>>();
            var pending = new List<MaterialAtlasAnalysis.MaterialInfo>();

            foreach (var group in groups.OrderByDescending(g => g.Count))
            {
                if (group.Count >= optimizer.atlasSettings.minimumMaterialsForAtlas)
                {
                    consolidated.Add(group);
                }
                else
                {
                    pending.AddRange(group);
                }
            }

            if (pending.Count >= optimizer.atlasSettings.minimumMaterialsForAtlas)
            {
                consolidated.Add(pending);

                if (optimizer.atlasSettings.verboseLogging)
                {
                    LogBuffered($"   Consolidated small groups into 1 group ({pending.Count} materials)");
                }
            }
            else if (pending.Count > 0 && optimizer.atlasSettings.verboseLogging)
            {
                LogBuffered($"   Skipped {pending.Count} materials from small groups (below minimum threshold)");
            }

            return consolidated;
        }

        private string BuildTextureSignature(Material mat)
        {
            var parts = new List<int>();
            var allowedProps = GetAllowedTexturePropertiesForShader(mat.shader);

            foreach (var prop in allowedProps.OrderBy(p => p))
            {
                if (mat.HasProperty(prop))
                {
                    var tex = mat.GetTexture(prop);
                    parts.Add(tex != null ? tex.GetInstanceID() : -1);
                }
                else
                {
                    parts.Add(-1);
                }
            }

            return string.Join("|", parts);
        }

        private void AnalyzeAvatar()
        {
            optimizationIssues.Clear();
            AnalyzeAvatarState(optimizationIssues);

            optimizer.stats.analysisIssues = optimizationIssues.Count;
            optimizer.stats.analysisWarnings = optimizationIssues.Count(issue => issue.Severity == OptimizationSeverity.Warning);
            optimizer.stats.analysisInfos = optimizationIssues.Count(issue => issue.Severity == OptimizationSeverity.Info);
            optimizer.stats.lastAnalysisReport = BuildAnalysisReport();

            if (optimizer.reportingSettings.showOptimizationSummary && optimizationIssues.Count > 0)
            {
                LogBuffered($"[AvatarOptimizer] Analysis found {optimizationIssues.Count} optimization findings");
                foreach (var issue in optimizationIssues.Take(Mathf.Min(optimizer.reportingSettings.maxIssuesPerCategory, optimizationIssues.Count)))
                {
                    LogBuffered($"[AvatarOptimizer] [{issue.Severity}] {issue.Title}: {issue.Description}");
                }
            }
            else if (optimizer.reportingSettings.showOptimizationSummary)
            {
                LogBuffered("[AvatarOptimizer] Analysis found no optimization findings");
            }
        }

        private void AnalyzeAvatarState(List<OptimizationIssue> issues)
        {
            if (optimizer.animatorAnalysisSettings.enableAnalysis)
            {
                AnalyzeAnimators(issues);
            }

            if (optimizer.meshAnalysisSettings.enableAnalysis)
            {
                AnalyzeMeshesAndMaterials(issues);
            }

            if (optimizer.blendshapeAnalysisSettings.enableAnalysis)
            {
                AnalyzeBlendshapes(issues);
            }

            if (optimizer.physicsAnalysisSettings.enableAnalysis)
            {
                AnalyzePhysics(issues);
            }
        }

        private void AnalyzeAnimators(List<OptimizationIssue> issues)
        {
            var animators = context.AvatarRootTransform.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
            {
                if (animator == null) continue;

                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;

                var avatarControllers = GetAvatarAnimatorControllers().ToList();
                if (avatarControllers.Count == 0)
                {
                    avatarControllers.Add(controller);
                }

                var analysisController = avatarControllers.FirstOrDefault(candidate => candidate != null && candidate.name == controller.name)
                    ?? controller;

                int layerCount = 0;
                int parameterCount = 0;
                int boolParameterCount = 0;
                int selfTransitionCount = 0;

                if (analysisController is AnimatorController animatorController)
                {
                    layerCount = animatorController.layers.Length;
                    parameterCount = animatorController.parameters.Length;
                    boolParameterCount = animatorController.parameters.Count(p => p.type == AnimatorControllerParameterType.Bool);
                    selfTransitionCount = CountSelfTransitions(animatorController);
                }
                else if (analysisController is AnimatorOverrideController overrideController)
                {
                    var overriddenController = overrideController.runtimeAnimatorController as AnimatorController;
                    if (overriddenController != null)
                    {
                        layerCount = overriddenController.layers.Length;
                        parameterCount = overriddenController.parameters.Length;
                        boolParameterCount = overriddenController.parameters.Count(p => p.type == AnimatorControllerParameterType.Bool);
                    }
                }

                if (optimizer.animatorAnalysisSettings.warnOnExcessiveLayers && layerCount > optimizer.animatorAnalysisSettings.maxRecommendedLayers)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Animator,
                        OptimizationSeverity.Warning,
                        $"Controller '{analysisController.name}' uses {layerCount} layers",
                        "This controller is likely to be more expensive than necessary. Consider reducing layer count or consolidating toggle-heavy structure.",
                        "Simplify layers and consolidate toggles where possible.",
                        false,
                        animator,
                        $"AnimatorLayers:{animator.GetInstanceID()}");
                }

                if (optimizer.animatorAnalysisSettings.warnOnExcessiveParameters && parameterCount > optimizer.animatorAnalysisSettings.maxRecommendedParameters)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Animator,
                        OptimizationSeverity.Warning,
                        $"Controller '{analysisController.name}' uses {parameterCount} parameters",
                        "Too many parameters can increase animator overhead and make the setup harder to maintain.",
                        "Reduce redundant parameters and consolidate similar toggles.",
                        false,
                        animator,
                        $"AnimatorParameters:{animator.GetInstanceID()}");
                }

                if (optimizer.animatorAnalysisSettings.warnOnHeavyToggleSetup && optimizer.animatorAnalysisSettings.preferDirectBlendTreesForHeavyToggles && boolParameterCount > optimizer.animatorAnalysisSettings.maxRecommendedBoolParameters)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Animator,
                        OptimizationSeverity.Info,
                        $"Controller '{analysisController.name}' has {boolParameterCount} bool parameters",
                        "Toggle-heavy controllers can benefit from lighter controller structures and more compact parameter usage, especially for CVR facial and toggle systems.",
                        "Consider consolidating toggles, using direct blend trees, or splitting facial logic into smaller layered controllers.",
                        false,
                        animator,
                        $"AnimatorBoolParams:{animator.GetInstanceID()}");
                }

                if (optimizer.animatorAnalysisSettings.warnOnSelfTransitions && selfTransitionCount > 0)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Animator,
                        OptimizationSeverity.Info,
                        $"Controller '{analysisController.name}' uses {selfTransitionCount} self-transition(s)",
                        "Self-transitions can make controller flow harder to maintain and can be harder to reason about for facial or toggle-heavy setups.",
                        "Prefer explicit state branches, direct blend trees, or a simpler layered structure instead of self-transitions.",
                        false,
                        animator,
                        $"AnimatorSelfTransitions:{animator.GetInstanceID()}");
                }
            }
        }

        private int CountSelfTransitions(AnimatorController controller)
        {
            if (controller == null) return 0;
            var count = 0;
            foreach (var layer in controller.layers)
            {
                count += CountSelfTransitionsInStateMachine(layer.stateMachine);
            }
            return count;
        }

        private int CountSelfTransitionsInStateMachine(AnimatorStateMachine stateMachine)
        {
            if (stateMachine == null) return 0;
            var count = 0;
            foreach (var state in stateMachine.states)
            {
                if (state.state == null) continue;
                foreach (var transition in state.state.transitions)
                {
                    if (transition == null) continue;
                    if (transition.destinationState != null && transition.destinationState == state.state)
                    {
                        count++;
                    }
                }
            }
            foreach (var subStateMachine in stateMachine.stateMachines)
            {
                count += CountSelfTransitionsInStateMachine(subStateMachine.stateMachine);
            }
            return count;
        }

        private void AnalyzeMeshesAndMaterials(List<OptimizationIssue> issues)
        {
            var renderers = context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true);
            var materialSlotCount = 0;
            var meshCount = 0;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                meshCount++;
                materialSlotCount += renderer.sharedMaterials?.Count(mat => mat != null) ?? 0;

                var materials = (renderer.sharedMaterials ?? Array.Empty<Material>()).Where(mat => mat != null).ToList();
                if (materials.Count > 2)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Material,
                        OptimizationSeverity.Info,
                        $"Renderer '{renderer.name}' uses {materials.Count} material slots",
                        "More material slots on a single renderer can increase draw-call complexity and make material grouping less efficient.",
                        "Try consolidating compatible materials or reducing the number of per-renderer material assignments.",
                        false,
                        renderer,
                        $"MaterialSlots:{renderer.GetInstanceID()}");
                }

                foreach (var material in materials)
                {
                    if (material == null) continue;
                    if (material.renderQueue >= 3000 || material.shader != null && material.shader.name.Contains("Transparent", StringComparison.OrdinalIgnoreCase))
                    {
                        AddIssue(
                            issues,
                            OptimizationCategory.Shader,
                            OptimizationSeverity.Warning,
                            $"Material '{material.name}' uses a transparent-style shader",
                            "Transparent materials often cost more to render and may be a good target for simplification or fallback.",
                            "Prefer opaque or cutout-friendly materials where the visual result allows it.",
                            false,
                            renderer,
                            $"TransparentShader:{material.GetInstanceID()}");
                        break;
                    }
                }
            }

            if (meshCount > optimizer.meshAnalysisSettings.maxRecommendedMeshCount)
            {
                AddIssue(
                    issues,
                    OptimizationCategory.Mesh,
                    OptimizationSeverity.Warning,
                    $"Avatar uses {meshCount} renderers",
                    "A large number of renderers can increase draw-call pressure and make the avatar more expensive to render.",
                    "Merge compatible meshes and reduce unnecessary renderers where possible.",
                    false,
                    null,
                    "MeshCount");
            }

            if (materialSlotCount > optimizer.meshAnalysisSettings.maxRecommendedMaterialSlots)
            {
                AddIssue(
                    issues,
                    OptimizationCategory.Material,
                    OptimizationSeverity.Warning,
                    $"Avatar uses {materialSlotCount} material slots",
                    "Many material slots can increase draw-call cost and material overhead.",
                    "Combine compatible materials and reduce unnecessary texture/material sets.",
                    false,
                    null,
                    "MaterialSlotCount");
            }
        }

        private void AnalyzeBlendshapes(List<OptimizationIssue> issues)
        {
            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in skinnedMeshRenderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;

                var mesh = renderer.sharedMesh;
                var blendshapeCount = mesh.blendShapeCount;

                if (blendshapeCount > 12)
                {
                    AddIssue(
                        issues,
                        OptimizationCategory.Blendshape,
                        OptimizationSeverity.Info,
                        $"Skinned mesh '{renderer.name}' uses {blendshapeCount} blendshapes",
                        "Large blendshape counts can increase mesh memory and processing cost. Review whether all shapes are still needed.",
                        "Remove unused or near-zero-delta blendshapes while preserving CVR facial systems.",
                        true,
                        renderer,
                        $"BlendshapeCount:{renderer.GetInstanceID()}");
                }

                if (optimizer.blendshapeAnalysisSettings.preserveCVRFacialShapes)
                {
                    var facialLikeBlendshapes = new List<string>();
                    for (int i = 0; i < blendshapeCount; i++)
                    {
                        var blendshapeName = mesh.GetBlendShapeName(i);
                        if (IsFacialLikeBlendshapeName(blendshapeName))
                        {
                            facialLikeBlendshapes.Add(blendshapeName);
                        }
                    }

                    if (facialLikeBlendshapes.Count > 0)
                    {
                        AddIssue(
                            issues,
                            OptimizationCategory.Blendshape,
                            OptimizationSeverity.Info,
                            $"Skinned mesh '{renderer.name}' has {facialLikeBlendshapes.Count} facial-like blendshape(s)",
                            "These appear to be expression or facial-related shapes and are likely CVR-sensitive. They should be reviewed carefully before any cleanup.",
                            "Preserve facial-related blendshapes unless you have explicit confirmation that they are safe to remove.",
                            false,
                            renderer,
                            $"BlendshapeFacial:{renderer.GetInstanceID()}");
                    }
                }

                if (optimizer.blendshapeAnalysisSettings.removeZeroDeltaBlendshapes)
                {
                    var zeroDeltaBlendshapes = 0;
                    for (int i = 0; i < blendshapeCount; i++)
                    {
                        if (IsZeroDeltaBlendshape(mesh, i))
                        {
                            zeroDeltaBlendshapes++;
                        }
                    }

                    if (zeroDeltaBlendshapes > 0)
                    {
                        AddIssue(
                            issues,
                            OptimizationCategory.Blendshape,
                            OptimizationSeverity.Warning,
                            $"Skinned mesh '{renderer.name}' contains {zeroDeltaBlendshapes} likely empty blendshape(s)",
                            "These shapes contribute little or no deformation and are good candidates for safe cleanup.",
                            "Remove zero-delta blendshapes only after confirming they are not part of a facial or animation system.",
                            true,
                            renderer,
                            $"BlendshapeZeroDelta:{renderer.GetInstanceID()}");
                    }
                }
            }
        }

        private bool IsFacialLikeBlendshapeName(string blendshapeName)
        {
            if (string.IsNullOrWhiteSpace(blendshapeName)) return false;

            var lowerName = blendshapeName.ToLowerInvariant();
            return lowerName.Contains("blink")
                || lowerName.Contains("viseme")
                || lowerName.Contains("eye")
                || lowerName.Contains("face")
                || lowerName.Contains("mouth")
                || lowerName.Contains("lip")
                || lowerName.Contains("jaw")
                || lowerName.Contains("cheek")
                || lowerName.Contains("smile")
                || lowerName.Contains("frown")
                || lowerName.Contains("sad")
                || lowerName.Contains("angry")
                || lowerName.Contains("surprise")
                || lowerName.Contains("kiss");
        }

        private void AnalyzePhysics(List<OptimizationIssue> issues)
        {
            var components = context.AvatarRootTransform.GetComponentsInChildren<Component>(true)
                .Where(component => component != null)
                .ToList();

            var magicaComponents = components
                .Where(component => component.GetType().Name.Contains("Magica", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (magicaComponents.Count > 0)
            {
                AddIssue(
                    issues,
                    OptimizationCategory.Physics,
                    OptimizationSeverity.Warning,
                    $"Detected {magicaComponents.Count} Magica-related component(s)",
                    "Magica Cloth can be expensive depending on collision settings, proxy mesh size, and simulation frequency.",
                    "Review collision settings and proxy mesh complexity for heavy cloth setups.",
                    false,
                    null,
                    "MagicaComponents");
            }

            var colliders = context.AvatarRootTransform.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 24)
            {
                AddIssue(
                    issues,
                    OptimizationCategory.Physics,
                    OptimizationSeverity.Info,
                    $"Detected {colliders.Length} colliders",
                    "A large number of colliders can increase runtime cost, especially when paired with cloth or physics-driven components.",
                    "Reduce unused colliders or consolidate them where possible.",
                    false,
                    null,
                    "ColliderCount");
            }

            foreach (var component in components)
            {
                var typeName = component.GetType().Name;
                var isMagicaLike = typeName.Contains("Magica", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Cloth", StringComparison.OrdinalIgnoreCase);
                var isDynamicBoneLike = typeName.Contains("DynamicBone", StringComparison.OrdinalIgnoreCase);

                if (isMagicaLike)
                {
                    if (optimizer.physicsAnalysisSettings.warnOnMagicaSelfCollision)
                    {
                        var selfCollisionEnabled = TryReadBoolValue(component, "selfCollision", "selfCollisionEnabled", "useSelfCollision");
                        if (selfCollisionEnabled)
                        {
                            AddIssue(
                                issues,
                                OptimizationCategory.Physics,
                                OptimizationSeverity.Warning,
                                $"{component.name} uses self-collision",
                                "Magica self-collision adds runtime cost and can become expensive on dense cloth setups.",
                                "Disable self-collision for less critical cloth objects or simplify the collision setup.",
                                false,
                                component,
                                $"MagicaSelfCollision:{component.GetInstanceID()}");
                        }
                    }

                    if (optimizer.physicsAnalysisSettings.warnOnMagicaMutualCollision)
                    {
                        var mutualCollisionEnabled = TryReadBoolValue(component, "mutualCollision", "mutualCollisionEnabled", "useMutualCollision", "allowMutualCollision");
                        if (mutualCollisionEnabled)
                        {
                            AddIssue(
                                issues,
                                OptimizationCategory.Physics,
                                OptimizationSeverity.Warning,
                                $"{component.name} uses mutual collision",
                                "Magica mutual collision can significantly increase the cost of cloth simulation.",
                                "Reduce the number of colliding cloth objects or disable mutual collision where not required.",
                                false,
                                component,
                                $"MagicaMutualCollision:{component.GetInstanceID()}");
                        }
                    }

                    if (optimizer.physicsAnalysisSettings.warnOnHighProxyVertexCount)
                    {
                        var proxyVertexCount = TryReadMeshVertexCount(component, "proxyMesh", "mesh", "sourceMesh");
                        if (proxyVertexCount > optimizer.physicsAnalysisSettings.maxRecommendedProxyVertexCount)
                        {
                            AddIssue(
                                issues,
                                OptimizationCategory.Physics,
                                OptimizationSeverity.Warning,
                                $"{component.name} uses a large proxy mesh ({proxyVertexCount} vertices)",
                                "Large proxy meshes increase cloth simulation cost and can become a bottleneck during optimization.",
                                "Reduce the proxy mesh density or use a simplified mesh for cloth simulation.",
                                false,
                                component,
                                $"MagicaProxyMesh:{component.GetInstanceID()}");
                        }
                    }
                }

                if (isDynamicBoneLike && optimizer.physicsAnalysisSettings.warnOnDynamicBoneComplexity)
                {
                    var colliderCount = TryReadCollectionCount(component, "m_Colliders", "colliders", "m_ColliderList", "colliderList");
                    if (colliderCount > optimizer.physicsAnalysisSettings.maxRecommendedDynamicBoneColliders)
                    {
                        AddIssue(
                            issues,
                            OptimizationCategory.Physics,
                            OptimizationSeverity.Info,
                            $"{component.name} uses {colliderCount} Dynamic Bone colliders",
                            "Dynamic Bone setups with many colliders can increase runtime complexity and become more sensitive to bone or mesh changes.",
                            "Reduce redundant colliders or simplify the Dynamic Bone setup where possible.",
                            false,
                            component,
                            $"DynamicBoneColliders:{component.GetInstanceID()}");
                    }
                }

                if (optimizer.physicsAnalysisSettings.warnOnHighSimulationFrequency)
                {
                    var frequency = TryReadIntValue(component, "frequency", "simulationFrequency", "simFrequency", "updateFrequency");
                    if (frequency > optimizer.physicsAnalysisSettings.maxRecommendedSimulationFrequency)
                    {
                        AddIssue(
                            issues,
                            OptimizationCategory.Physics,
                            OptimizationSeverity.Warning,
                            $"{component.name} uses a high simulation frequency",
                            "High cloth simulation frequency can significantly increase CPU cost.",
                            "Reduce the simulation frequency or use a lower-cost setup for less critical cloth objects.",
                            false,
                            component,
                            $"ClothFrequency:{component.GetInstanceID()}");
                    }
                }
            }
        }

        private bool TryReadBoolValue(Component component, params string[] candidateNames)
        {
            if (component == null) return false;
            var type = component.GetType();
            foreach (var name in candidateNames)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    var value = field.GetValue(component);
                    if (value is bool boolValue) return boolValue;
                }

                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property != null && property.PropertyType == typeof(bool))
                {
                    try { return (bool)property.GetValue(component); } catch { }
                }
            }
            return false;
        }

        private int TryReadIntValue(Component component, params string[] candidateNames)
        {
            if (component == null) return 0;
            var type = component.GetType();
            foreach (var name in candidateNames)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    var value = field.GetValue(component);
                    if (value is int intValue) return intValue;
                    if (value is float floatValue) return Mathf.RoundToInt(floatValue);
                }

                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property != null)
                {
                    try
                    {
                        var value = property.GetValue(component);
                        if (value is int intValue) return intValue;
                        if (value is float floatValue) return Mathf.RoundToInt(floatValue);
                    }
                    catch { }
                }
            }
            return 0;
        }

        private int TryReadMeshVertexCount(Component component, params string[] candidateNames)
        {
            if (component == null) return 0;
            var type = component.GetType();
            foreach (var name in candidateNames)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    var value = field.GetValue(component);
                    switch (value)
                    {
                        case Mesh mesh: return mesh.vertexCount;
                        case SkinnedMeshRenderer skinned: return skinned.sharedMesh != null ? skinned.sharedMesh.vertexCount : 0;
                        case MeshFilter meshFilter: return meshFilter.sharedMesh != null ? meshFilter.sharedMesh.vertexCount : 0;
                    }
                }

                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property != null)
                {
                    try
                    {
                        var value = property.GetValue(component);
                        switch (value)
                        {
                            case Mesh mesh: return mesh.vertexCount;
                            case SkinnedMeshRenderer skinned: return skinned.sharedMesh != null ? skinned.sharedMesh.vertexCount : 0;
                            case MeshFilter meshFilter: return meshFilter.sharedMesh != null ? meshFilter.sharedMesh.vertexCount : 0;
                        }
                    }
                    catch { }
                }
            }
            return 0;
        }

        private int TryReadCollectionCount(Component component, params string[] candidateNames)
        {
            if (component == null) return 0;
            var type = component.GetType();
            foreach (var name in candidateNames)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    var value = field.GetValue(component);
                    if (value is System.Collections.IEnumerable enumerable && value is not string)
                    {
                        return enumerable.Cast<object?>().Count(item => item != null);
                    }
                }

                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (property != null)
                {
                    try
                    {
                        var value = property.GetValue(component);
                        if (value is System.Collections.IEnumerable enumerable && value is not string)
                        {
                            return enumerable.Cast<object?>().Count(item => item != null);
                        }
                    }
                    catch { }
                }
            }
            return 0;
        }

        private void RefreshIssuesAfterOptimization()
        {
            var currentIssues = new List<OptimizationIssue>();
            AnalyzeAvatarState(currentIssues);

            var mergedIssues = new List<OptimizationIssue>();
            foreach (var currentIssue in currentIssues)
            {
                var previousIssue = optimizationIssues.FirstOrDefault(previous => string.Equals(previous.IssueKey, currentIssue.IssueKey, StringComparison.Ordinal));
                if (previousIssue != null)
                {
                    currentIssue.BeforeValue = previousIssue.BeforeValue ?? GetIssueBeforeValue(previousIssue);
                    currentIssue.AfterValue = GetIssueAfterValue(currentIssue);
                    ApplyBeforeAfterTitle(currentIssue);

                    if (currentIssue.BeforeValue.HasValue && currentIssue.AfterValue.HasValue && currentIssue.BeforeValue.Value == currentIssue.AfterValue.Value)
                    {
                        continue;
                    }
                }
                else
                {
                    currentIssue.AfterValue = GetIssueAfterValue(currentIssue);
                    ApplyBeforeAfterTitle(currentIssue);
                }

                mergedIssues.Add(currentIssue);
            }

            optimizationIssues.Clear();
            optimizationIssues.AddRange(mergedIssues);
        }

        private int? GetIssueBeforeValue(OptimizationIssue issue)
        {
            if (issue.BeforeValue.HasValue)
            {
                return issue.BeforeValue.Value;
            }

            if (issue.Category == OptimizationCategory.Blendshape && issue.TargetComponent is SkinnedMeshRenderer renderer)
            {
                return renderer.sharedMesh != null ? renderer.sharedMesh.blendShapeCount : 0;
            }

            return null;
        }

        private int? GetIssueAfterValue(OptimizationIssue issue)
        {
            switch (issue.Category)
            {
                case OptimizationCategory.Blendshape when issue.TargetComponent is SkinnedMeshRenderer renderer:
                    return renderer.sharedMesh != null ? renderer.sharedMesh.blendShapeCount : 0;
                case OptimizationCategory.Mesh:
                    return context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true).Length;
                case OptimizationCategory.Material:
                    if (issue.IssueKey == "MaterialSlotCount")
                    {
                        return context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true)
                            .Sum(renderer => renderer.sharedMaterials?.Count(mat => mat != null) ?? 0);
                    }
                    return null;
                case OptimizationCategory.Animator:
                    if (issue.Title.Contains("layers", StringComparison.OrdinalIgnoreCase))
                    {
                        return GetAvatarAnimatorControllers()
                            .Select(c => c is AnimatorOverrideController aoc ? aoc.runtimeAnimatorController as AnimatorController : c as AnimatorController)
                            .Where(c => c != null)
                            .Select(controller => controller.layers.Length)
                            .FirstOrDefault();
                    }
                    if (issue.Title.Contains("parameters", StringComparison.OrdinalIgnoreCase))
                    {
                        return GetAvatarAnimatorControllers()
                            .Select(c => c is AnimatorOverrideController aoc ? aoc.runtimeAnimatorController as AnimatorController : c as AnimatorController)
                            .Where(c => c != null)
                            .Select(controller => controller.parameters.Length)
                            .FirstOrDefault();
                    }
                    break;
                case OptimizationCategory.Physics:
                    if (issue.IssueKey == "ColliderCount")
                    {
                        return context.AvatarRootTransform.GetComponentsInChildren<Collider>(true).Length;
                    }
                    break;
            }

            return null;
        }

        private void ApplyBeforeAfterTitle(OptimizationIssue issue)
        {
            if (!issue.BeforeValue.HasValue || !issue.AfterValue.HasValue || issue.BeforeValue.Value == issue.AfterValue.Value)
            {
                return;
            }

            if (issue.Category == OptimizationCategory.Blendshape && issue.TargetComponent is SkinnedMeshRenderer renderer)
            {
                issue.Title = $"Skinned mesh '{renderer.name}' used {issue.BeforeValue.Value} blendshapes and now uses {issue.AfterValue.Value}";
                return;
            }

            if (issue.Title.Contains("uses", StringComparison.OrdinalIgnoreCase) || issue.Title.Contains("contains", StringComparison.OrdinalIgnoreCase))
            {
                issue.Title = $"{issue.Title} ({issue.BeforeValue.Value} → {issue.AfterValue.Value})";
            }
        }

        private string BuildAnalysisReport()
        {
            if (optimizationIssues.Count == 0)
            {
                return "No optimization findings were detected.";
            }

            var report = new StringBuilder();
            report.AppendLine("Optimization analysis summary:");

            foreach (var category in Enum.GetValues(typeof(OptimizationCategory)).Cast<OptimizationCategory>())
            {
                var issues = optimizationIssues.Where(issue => issue.Category == category).ToList();
                if (issues.Count == 0) continue;

                report.AppendLine($"- {category}: {issues.Count} finding(s)");
                foreach (var issue in issues.Take(optimizer.reportingSettings.maxIssuesPerCategory))
                {
                    report.AppendLine($"  • [{issue.Severity}] {issue.Title}");
                    report.AppendLine($"    {issue.SuggestedAction}");
                }
            }

            return report.ToString();
        }

        private void AddIssue(List<OptimizationIssue> issues, OptimizationCategory category, OptimizationSeverity severity, string title, string description, string suggestedAction, bool canAutoFix, Component targetComponent, string issueKey = null)
        {
            issues.Add(new OptimizationIssue
            {
                Category = category,
                Severity = severity,
                Title = title,
                Description = description,
                SuggestedAction = suggestedAction,
                CanAutoFix = canAutoFix,
                TargetComponent = targetComponent,
                IssueKey = issueKey
            });
        }

        private void AddIssue(OptimizationCategory category, OptimizationSeverity severity, string title, string description, string suggestedAction, bool canAutoFix, Component targetComponent)
        {
            AddIssue(optimizationIssues, category, severity, title, description, suggestedAction, canAutoFix, targetComponent);
        }

        public void Process()
        {
            var startTime = Time.realtimeSinceStartup;
            LogBuffered("[AvatarOptimizer] ========== Starting Optimization ==========");

            try
            {
                AnalyzeAvatar();

                if (optimizer.reportOnly)
                {
                    LogBuffered("[AvatarOptimizer] Report-only mode enabled; skipping destructive optimization steps");
                    FlushBufferedLog();
                    return;
                }

                if (optimizer.meshSettings.mergeToggleObjects && !toggleMergePrepared)
                {
                    LogBuffered("[AvatarOptimizer] Preparing toggle-preserving merge after CCK/AAS generation on the baked clone");
                    PushLogIndent();
                    PrepareTogglePreservingMerge();
                    PopLogIndent();
                }

                if (optimizer.boneSettings.preserveAnimatedBones)
                {
                    LogBuffered("[AvatarOptimizer] Preserve animated bones ON");
                    PushLogIndent();
                    CollectAnimatedBones();
                    CollectAnimatedTransforms();
                    PopLogIndent();
                }

                if (optimizer.blendshapeSettings.removeUnusedBlendshapes || optimizer.blendshapeSettings.bakeConstantBlendshapes)
                {
                    LogBuffered("[AvatarOptimizer] Remove unused blendshapes ON");
                    PushLogIndent();
                    CollectUsedBlendshapes();
                    RemoveUnusedBlendshapes();
                    PopLogIndent();
                }

                if (optimizer.boneSettings.removeUnusedBoneReferences)
                {
                    LogBuffered("[AvatarOptimizer] Remove unused bone references ON");
                    PushLogIndent();
                    RemoveUnusedBoneReferences();
                    PopLogIndent();
                }

                if (optimizer.boneSettings.removeBonesWithoutWeights && !optimizer.rigReductionSettings.reduceInfluences && !optimizer.simplificationSettings.enabled)
                {
                    LogBuffered("[AvatarOptimizer] Remove bones without weights ON");
                    PushLogIndent();
                    RemoveBonesWithoutWeights();
                    PopLogIndent();
                }

                if (optimizer.meshSettings.mergeVerticesByDistance || optimizer.meshSettings.deleteLooseVertices)
                {
                    LogBuffered("[AvatarOptimizer] Optimize meshes ON");
                    PushLogIndent();
                    OptimizeMeshes();
                    PopLogIndent();
                }

                if (optimizer.meshSettings.stripUnusedAttributes)
                {
                    LogBuffered("[AvatarOptimizer] Strip unused attributes ON");
                    PushLogIndent();
                    StripUnusedAttributes();
                    PopLogIndent();
                }

                GuardedAvatarPasses.ProcessRigAndSimplification(optimizer, LogBuffered);
                GuardedAvatarPasses.ProcessSurfaces(optimizer, LogBuffered,
                    transformer => ProcessAndReplaceControllerClips(transformer, "_Surface"));

                if (optimizer.meshSettings.combineMeshes || UseRelaxedToggleMerge())
                {
                    LogBuffered(optimizer.meshSettings.combineMeshes
                        ? "[AvatarOptimizer] Combine meshes ON"
                        : "[AvatarOptimizer] Toggle-preserving mesh merge ON");
                    PushLogIndent();
                    CombineMeshes();
                    PopLogIndent();
                }
                else if (optimizer.meshSettings.mergeToggleObjects)
                {
                    LogBuffered("[AvatarOptimizer] Toggle merge skipped: no MT_Hide_* blendshapes were generated on the baked clone");
                }

                if (optimizer.meshSettings.deduplicateMaterials)
                {
                    LogBuffered("[AvatarOptimizer] Deduplicate materials ON");
                    PushLogIndent();
                    DeduplicateMaterials();
                    PopLogIndent();
                }

                if (optimizer.atlasSettings.generateTextureAtlas && !optimizer.surfaceSettings.packUvIslands && !optimizer.surfaceSettings.atlasMaterialSwaps)
                {
                    CollectAnimatedMaterials();

                    LogBuffered("[AvatarOptimizer] ═══ Material Atlasing Analysis ═══");
                    PushLogIndent();
                    atlasAnalysis = AnalyzeMaterialAtlasing();
                    PopLogIndent();
                    LogBuffered("");
                }

                if (optimizer.atlasSettings.generateTextureAtlas && !optimizer.surfaceSettings.packUvIslands && !optimizer.surfaceSettings.atlasMaterialSwaps)
                {
                    LogBuffered("[AvatarOptimizer] Generate texture atlases ON");
                    PushLogIndent();
                    GenerateTextureAtlases();
                    PopLogIndent();
                }

                if (optimizer.atlasSettings.useAdaptiveCompression && !optimizer.atlasSettings.generateTextureAtlas &&
                    !optimizer.surfaceSettings.packUvIslands && !optimizer.surfaceSettings.atlasMaterialSwaps)
                {
                    LogBuffered("[AvatarOptimizer] Standalone adaptive texture compression ON");
                    PushLogIndent();
                    ApplyStandaloneAdaptiveCompression();
                    PopLogIndent();
                }
            }
            catch (Exception ex)
            {
                LogBuffered($"[AvatarOptimizer] !!! Optimization crashed: {ex}");
                throw;
            }
            finally
            {
                optimizer.stats.optimizationTimeSeconds = Time.realtimeSinceStartup - startTime;

                ApplyPendingAtlasImportSettings();

                RefreshIssuesAfterOptimization();
                optimizer.stats.lastAnalysisReport = BuildAnalysisReport();

                LogBuffered("[AvatarOptimizer] Rebuilding MagicaCloth simulations...");
                PushLogIndent();
                if (!optimizer.reportOnly) RebuildMagicaClothData(context.AvatarRootTransform);
                PopLogIndent();

                LogBuffered($"[AvatarOptimizer] ========== Optimization Complete in {optimizer.stats.optimizationTimeSeconds:F2}s ==========");
                LogBuffered($"[AvatarOptimizer] Stats: {optimizer.stats.bonesRemoved} bone objects removed, {optimizer.stats.boneReferencesRemoved} bone references removed, " +
                            $"{optimizer.stats.blendshapesRemoved} blendshapes removed, {optimizer.stats.verticesMerged} vertices merged, " +
                            $"{optimizer.stats.looseVerticesRemoved} loose vertices removed, {optimizer.stats.meshesCombined} meshes combined, " +
                            $"{optimizer.stats.meshesModified} meshes modified, {optimizer.stats.atlasesGenerated} atlases");

                FlushBufferedLog();
            }
        }

        #region Reflection Helpers

        private object GetFieldValue(object obj, string fieldName)
        {
            if (obj == null) return null;
            var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return field?.GetValue(obj);
        }

        private object GetPropertyValue(object obj, string propertyName)
        {
            if (obj == null) return null;
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return property?.GetValue(obj);
        }

        private T GetFieldOrPropertyValue<T>(object obj, string name, T defaultValue = default(T))
        {
            var value = GetFieldValue(obj, name) ?? GetPropertyValue(obj, name);
            return value != null ? (T)value : defaultValue;
        }

        #endregion

        #region Asset Safety

        private Mesh GetOrCreateMeshCopy(SkinnedMeshRenderer smr, string suffix)
        {
            if (smr == null || smr.sharedMesh == null) return null;

            if (optimizedMeshes.ContainsKey(smr))
            {
                return optimizedMeshes[smr];
            }

            var original = smr.sharedMesh;
            var copy = UnityEngine.Object.Instantiate(original);
            copy.name = original.name + suffix;

            optimizedMeshes[smr] = copy;
            smr.sharedMesh = copy;

            return copy;
        }

        private Material GetOrCreateMaterialCopy(Material original)
        {
            if (materialCopies.ContainsKey(original))
            {
                return materialCopies[original];
            }

            var copy = new Material(original);
            copy.name = original.name + "_Atlased";
            materialCopies[original] = copy;
            return copy;
        }

        #endregion

        #region Mesh Utilities

        private void StripUnusedAttributes()
        {
            if (!optimizer.meshSettings.stripUnusedAttributes)
            {
                LogBuffered("[AvatarOptimizer] Attribute stripping disabled");
                return;
            }

            LogBuffered("[AvatarOptimizer] Stripping unused vertex attributes...");
            PushLogIndent();
            int strippedCount = 0;

            foreach (var smr in context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (MaterialAnimationUtility.HasMaterialSwaps(context.AvatarRootTransform, smr))
                {
                    LogBuffered($"[AvatarOptimizer] Preserving vertex attributes on '{smr.name}' because its materials are swapped by animation");
                    continue;
                }
                if (GuardedAvatarPasses.IsGuardedRenderer(optimizer, smr)) continue;
                var mesh = GetOrCreateMeshCopy(smr, "_Stripped");
                if (mesh == null) continue;

                bool modified = false;

                if (optimizer.meshSettings.stripTangents && mesh.tangents != null && mesh.tangents.Length > 0)
                {
                    if (optimizer.meshSettings.verboseLogging)
                        LogBuffered($"  Stripping tangents from {mesh.name}");
                    mesh.tangents = null;
                    modified = true;
                }

                if (optimizer.meshSettings.stripVertexColors && mesh.colors != null && mesh.colors.Length > 0)
                {
                    if (optimizer.meshSettings.verboseLogging)
                        LogBuffered($"  Stripping vertex colors from {mesh.name}");
                    mesh.colors = null;
                    modified = true;
                }

                if (optimizer.meshSettings.stripLightmapUVs)
                {
                    try
                    {
                        var uv2 = mesh.uv2;
                        if (uv2 != null && uv2.Length > 0)
                        {
                            if (optimizer.meshSettings.verboseLogging)
                                LogBuffered($"  Stripping lightmap UVs from {mesh.name}");
                            mesh.uv2 = null;
                            modified = true;
                        }
                    }
                    catch { }
                }

                if (optimizer.meshSettings.stripExtraUVChannels)
                {
                    try
                    {
                        var uv3 = mesh.uv3;
                        if (uv3 != null && uv3.Length > 0)
                        {
                            if (optimizer.meshSettings.verboseLogging)
                                LogBuffered($"  Stripping UV3 from {mesh.name}");
                            mesh.uv3 = null;
                            modified = true;
                        }
                    }
                    catch { }

                    try
                    {
                        var uv4 = mesh.uv4;
                        if (uv4 != null && uv4.Length > 0)
                        {
                            if (optimizer.meshSettings.verboseLogging)
                                LogBuffered($"  Stripping UV4 from {mesh.name}");
                            mesh.uv4 = null;
                            modified = true;
                        }
                    }
                    catch { }
                }

                if (modified) strippedCount++;
            }

            LogBuffered($"[AvatarOptimizer] Stripped attributes from {strippedCount} meshes");
            PopLogIndent();
        }

        private void DeduplicateMaterials()
        {
            LogBuffered("[AvatarOptimizer] Deduplicating materials...");
            PushLogIndent();
            int deduped = 0;

            var materialMap = new Dictionary<string, Material>();
            var animated = MaterialAnimationUtility.CollectAnimatedMaterialProperties(context.AvatarRootTransform, avatar);

            foreach (var renderer in context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.shader == null || animated.ContainsKey(m) || m.name.EndsWith("_SurfaceCopy", StringComparison.Ordinal)) continue;
                    string key = $"{m.shader.name}|{m.mainTexture?.GetInstanceID() ?? 0}|{m.name}";
                    if (materialMap.TryGetValue(key, out var existing))
                    {
                        if (!ReferenceEquals(existing, m))
                        {
                            mats[i] = existing;
                            deduped++;
                            changed = true;
                        }
                    }
                    else
                    {
                        materialMap[key] = m;
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = mats;
                }
            }

            LogBuffered($"[AvatarOptimizer] Deduplicated {deduped} material slots");
            PopLogIndent();
        }

        #endregion

        #region Animated Bones Collection

        private void CollectAnimatedBones()
        {
            LogBuffered("[AvatarOptimizer] Collecting animated bones...");
            PushLogIndent();

            foreach (var controller in GetAvatarAnimatorControllers())
            {
                CollectAnimatedBonesFromController(controller);
            }

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] Found {animatedBones.Count} animated bones");
        }

        private void CollectAnimatedBonesFromController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;

            foreach (var clip in GetAllAnimationClips(controller))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Transform))
                    {
                        var transform = context.AvatarRootTransform.Find(binding.path);
                        if (transform != null)
                        {
                            animatedBones.Add(transform);
                        }
                    }
                }

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!binding.propertyName.Contains("m_Materials"))
                        continue;

                    var target = context.AvatarRootTransform.Find(binding.path);
                    var renderer = target != null ? target.GetComponent<Renderer>() : null;
                    if (renderer == null)
                        continue;

                    int materialIndex = 0;
                    var match = System.Text.RegularExpressions.Regex.Match(
                        binding.propertyName, @"Array\.data\[(\d+)\]");
                    if (match.Success)
                    {
                        materialIndex = int.Parse(match.Groups[1].Value);
                    }

                    if (materialIndex >= renderer.sharedMaterials.Length)
                        continue;

                    var material = renderer.sharedMaterials[materialIndex];
                    if (material == null)
                        continue;

                    animatedMaterials.Add(material);
                    if (!animatedMaterialProperties.ContainsKey(material))
                    {
                        animatedMaterialProperties[material] = new List<string>();
                    }

                    if (!animatedMaterialProperties[material].Contains(MaterialAnimationUtility.MaterialReferenceProperty))
                    {
                        animatedMaterialProperties[material].Add(MaterialAnimationUtility.MaterialReferenceProperty);
                    }
                }
            }
        }

        private void CollectAnimatedTransforms()
        {
            LogBuffered("[AvatarOptimizer] Collecting animated transform properties...");
            PushLogIndent();

            foreach (var controller in GetAvatarAnimatorControllers())
            {
                CollectAnimatedTransformPropertiesFromController(controller);
            }

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] Found {animatedTransformProperties.Count} animated transforms");
        }

        private void CollectAnimatedTransformPropertiesFromController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;

            foreach (var clip in GetAllAnimationClips(controller))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var transform = context.AvatarRootTransform.Find(binding.path);
                    if (transform != null)
                    {
                        if (!animatedTransformProperties.ContainsKey(transform))
                        {
                            animatedTransformProperties[transform] = new HashSet<string>();
                        }
                        animatedTransformProperties[transform].Add(binding.propertyName);
                    }
                }
            }
        }

        #endregion

        #region Blendshape Optimization

        private void CollectUsedBlendshapes()
        {
            LogBuffered("[AvatarOptimizer] Collecting used blendshapes...");
            PushLogIndent();

            var settings = optimizer.blendshapeSettings;

            if (avatar == null)
            {
                LogBuffered("[AvatarOptimizer] Skipping CVR blendshape preservation (no CVRAvatar found)");
            }
            else
            {
                if (settings.preserveBlinkBlendshapes)
                {
                    var useBlinkBlendshapes = GetFieldOrPropertyValue<bool>(avatar, "useBlinkBlendshapes");
                    if (useBlinkBlendshapes)
                    {
                        var blinkBlendshape = GetFieldOrPropertyValue<string[]>(avatar, "blinkBlendshape");
                        if (blinkBlendshape != null)
                        {
                            foreach (var bs in blinkBlendshape)
                            {
                                if (!string.IsNullOrEmpty(bs))
                                {
                                    usedBlendshapes.Add(bs);
                                    if (settings.verboseLogging)
                                        LogBuffered($"[AvatarOptimizer] Preserved blink blendshape: {bs}");
                                }
                            }
                        }
                    }
                }

                if (settings.preserveVisemeBlendshapes)
                {
                    var useVisemeLipsync = GetFieldOrPropertyValue<bool>(avatar, "useVisemeLipsync");
                    if (useVisemeLipsync)
                    {
                        var visemeBlendshapes = GetFieldOrPropertyValue<string[]>(avatar, "visemeBlendshapes");
                        if (visemeBlendshapes != null)
                        {
                            foreach (var bs in visemeBlendshapes)
                            {
                                if (!string.IsNullOrEmpty(bs))
                                {
                                    usedBlendshapes.Add(bs);
                                    if (settings.verboseLogging)
                                        LogBuffered($"[AvatarOptimizer] Preserved viseme blendshape: {bs}");
                                }
                            }
                        }
                    }
                }

                if (settings.preserveEyeLookBlendshapes)
                {
                    var useEyeMovement = GetFieldOrPropertyValue<bool>(avatar, "useEyeMovement");
                    if (useEyeMovement)
                    {
                        var eyeMovementInfo = GetFieldOrPropertyValue<object>(avatar, "eyeMovementInfo");
                        if (eyeMovementInfo != null)
                        {
                            var eyes = GetFieldOrPropertyValue<object[]>(eyeMovementInfo, "eyes");
                            if (eyes != null)
                            {
                                foreach (var eye in eyes)
                                {
                                    if (eye == null) continue;

                                    var blendshapes = new[]
                                    {
                                        GetFieldOrPropertyValue<string>(eye, "eyeBlendShapeUp"),
                                        GetFieldOrPropertyValue<string>(eye, "eyeBlendShapeDown"),
                                        GetFieldOrPropertyValue<string>(eye, "eyeBlendShapeIn"),
                                        GetFieldOrPropertyValue<string>(eye, "eyeBlendShapeOut")
                                    };

                                    foreach (var bs in blendshapes)
                                    {
                                        if (!string.IsNullOrEmpty(bs))
                                        {
                                            usedBlendshapes.Add(bs);
                                            if (settings.verboseLogging)
                                                LogBuffered($"[AvatarOptimizer] Preserved eye look blendshape: {bs}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (settings.preserveFaceTrackingBlendshapes && cvrFaceTrackingType != null)
                {
                    var faceTracking = context.AvatarRootObject.GetComponent(cvrFaceTrackingType);
                    if (faceTracking != null)
                    {
                        var useFacialTracking = GetFieldOrPropertyValue<bool>(faceTracking, "UseFacialTracking");
                        if (useFacialTracking)
                        {
                            var faceBlendShapes = GetFieldOrPropertyValue<string[]>(faceTracking, "FaceBlendShapes");
                            if (faceBlendShapes != null)
                            {
                                foreach (var bs in faceBlendShapes)
                                {
                                    if (!string.IsNullOrEmpty(bs) && bs != "-none-")
                                    {
                                        usedBlendshapes.Add(bs);
                                        if (settings.verboseLogging)
                                            LogBuffered($"[AvatarOptimizer] Preserved face tracking blendshape: {bs}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (settings.scanAdvancedAvatarSettings || settings.scanOverrideController)
            {
                foreach (var controller in GetAvatarAnimatorControllers())
                {
                    CollectBlendshapesFromAnimator(controller);
                }
            }

            if (!string.IsNullOrEmpty(settings.preserveBlendshapePatterns))
            {
                var patterns = settings.preserveBlendshapePatterns.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in patterns)
                {
                    LogBuffered($"[AvatarOptimizer] Will preserve blendshapes matching pattern: {pattern}");
                }
            }

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] Found {usedBlendshapes.Count} used blendshapes");
        }

        private void CollectBlendshapesFromAnimator(RuntimeAnimatorController controller)
        {
            if (controller == null) return;

            foreach (var clip in GetAllAnimationClips(controller))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.propertyName.StartsWith("blendShape."))
                    {
                        string blendshapeName = binding.propertyName.Substring("blendShape.".Length);
                        usedBlendshapes.Add(blendshapeName);
                    }
                }
            }
        }

        private IEnumerable<AnimationClip> GetAllAnimationClips(RuntimeAnimatorController controller)
        {
            var clips = new HashSet<AnimationClip>();
            if (controller == null) return clips;

            // 1. Direct native call to controller.animationClips
            try
            {
                if (controller.animationClips != null)
                {
                    foreach (var clip in controller.animationClips)
                    {
                        if (clip != null) clips.Add(clip);
                    }
                }
            }
            catch { }

            // 2. Traversal pass for deep state machines or override mappings
            if (controller is AnimatorController animController)
            {
                foreach (var layer in animController.layers)
                {
                    CollectClipsFromStateMachine(layer.stateMachine, clips);
                }
            }
            else if (controller is AnimatorOverrideController overrideController)
            {
                if (overrideController.runtimeAnimatorController is AnimatorController runtimeAnimatorController)
                {
                    foreach (var layer in runtimeAnimatorController.layers)
                    {
                        CollectClipsFromStateMachine(layer.stateMachine, clips);
                    }
                }

                foreach (var baseClip in GetAllAnimationClips(overrideController.runtimeAnimatorController)) clips.Add(baseClip);
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrideController.GetOverrides(overrides);

                foreach (var pair in overrides)
                {
                    if (pair.Value != null) clips.Add(pair.Value);
                    if (pair.Key != null) clips.Add(pair.Key);
                }
            }

            return clips;
        }

        private void CollectClipsFromStateMachine(AnimatorStateMachine stateMachine, HashSet<AnimationClip> clips)
        {
            if (stateMachine == null) return;

            foreach (var state in stateMachine.states)
            {
                if (state.state.motion is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else if (state.state.motion is BlendTree blendTree)
                {
                    CollectClipsFromBlendTree(blendTree, clips);
                }
            }

            foreach (var subStateMachine in stateMachine.stateMachines)
            {
                CollectClipsFromStateMachine(subStateMachine.stateMachine, clips);
            }
        }

        private void CollectClipsFromBlendTree(BlendTree blendTree, HashSet<AnimationClip> clips)
        {
            if (blendTree == null) return;

            foreach (var child in blendTree.children)
            {
                if (child.motion is AnimationClip clip)
                {
                    clips.Add(clip);
                }
                else if (child.motion is BlendTree childBlendTree)
                {
                    CollectClipsFromBlendTree(childBlendTree, clips);
                }
            }
        }

        private bool ShouldPreserveBlendshape(string blendshapeName)
        {
            var settings = optimizer.blendshapeSettings;

            if (blendshapeName.StartsWith("MT_Hide_", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(settings.forceRemoveBlendshapePatterns))
            {
                var removePatterns = settings.forceRemoveBlendshapePatterns.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in removePatterns)
                {
                    if (blendshapeName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            if (usedBlendshapes.Contains(blendshapeName))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(settings.preserveBlendshapePatterns))
            {
                var preservePatterns = settings.preserveBlendshapePatterns.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in preservePatterns)
                {
                    if (blendshapeName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsZeroDeltaBlendshape(Mesh mesh, int blendshapeIndex)
        {
            if (!optimizer.blendshapeSettings.removeZeroDeltaBlendshapes)
                return false;

            int frameCount = mesh.GetBlendShapeFrameCount(blendshapeIndex);
            float threshold = optimizer.blendshapeSettings.zeroDeltaThreshold;

            for (int frame = 0; frame < frameCount; frame++)
            {
                Vector3[] deltaVertices = new Vector3[mesh.vertexCount];
                Vector3[] deltaNormals = new Vector3[mesh.vertexCount];
                Vector3[] deltaTangents = new Vector3[mesh.vertexCount];

                mesh.GetBlendShapeFrameVertices(blendshapeIndex, frame, deltaVertices, deltaNormals, deltaTangents);

                foreach (var delta in deltaVertices)
                {
                    if (delta.magnitude > threshold)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void RemoveUnusedBlendshapes()
        {
            LogBuffered("[AvatarOptimizer] --- RemoveUnusedBlendshapes START ---");
            PushLogIndent();

            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int totalRemoved = 0;

            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.sharedMesh == null) continue;

                var mesh = GetOrCreateMeshCopy(smr, "_Optimized");
                if (mesh == null) continue;

                int blendShapeCount = mesh.blendShapeCount;
                var blendshapesToRemove = new List<int>();

                int perMeshLogged = 0;
                const int MaxPerMeshRemovalLogs = 10;
                int suppressed = 0;

                for (int i = 0; i < blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);

                    bool nonzero = smr.GetBlendShapeWeight(i) != 0;
                    bool safe = GuardedAvatarPasses.CanBakeShape(optimizer, smr, i) &&
                        (optimizer.blendshapeSettings.removeUnusedBlendshapes || nonzero);
                    if (safe && (!nonzero || optimizer.blendshapeSettings.bakeConstantBlendshapes) &&
                        !ShouldPreserveBlendshape(name) && !ShouldPreserveFaceMeshBlendshape(smr))
                    {
                        blendshapesToRemove.Add(i);

                        if (optimizer.blendshapeSettings.verboseLogging)
                        {
                            if (perMeshLogged < MaxPerMeshRemovalLogs)
                            {
                                string reason = IsZeroDeltaBlendshape(mesh, i) ? "zero delta" : "unused";
                                LogBuffered($"[AvatarOptimizer] Removing blendshape '{name}' from {smr.name} ({reason})");
                                perMeshLogged++;
                            }
                            else
                            {
                                suppressed++;
                            }
                        }
                    }
                }

                if (suppressed > 0)
                {
                    LogBuffered($"[AvatarOptimizer] ... suppressed {suppressed} more blendshape removal logs for {smr.name}");
                }

                if (blendshapesToRemove.Count > 0)
                {
                    var newMesh = CopyMeshWithoutBlendshapes(smr, mesh, blendshapesToRemove);
                    var retainedWeights = Enumerable.Range(0, mesh.blendShapeCount)
                        .Where(index => !blendshapesToRemove.Contains(index))
                        .Select(index => smr.GetBlendShapeWeight(index)).ToArray();
                    smr.sharedMesh = newMesh;
                    for (int index = 0; index < retainedWeights.Length; index++) smr.SetBlendShapeWeight(index, retainedWeights[index]);
                    optimizedMeshes[smr] = newMesh;
                    totalRemoved += blendshapesToRemove.Count;

                    LogBuffered($"[AvatarOptimizer] Removed {blendshapesToRemove.Count} blendshapes from {smr.name}");
                }
            }

            optimizer.stats.blendshapesRemoved = totalRemoved;
            LogBuffered($"[AvatarOptimizer] Total blendshapes removed: {totalRemoved}");

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- RemoveUnusedBlendshapes END ---");
        }

        private bool ShouldPreserveFaceMeshBlendshape(SkinnedMeshRenderer renderer)
        {
            if (!optimizer.blendshapeAnalysisSettings.preserveCVRFacialShapes || !IsCVRFaceMesh(renderer))
                return false;

            var settings = optimizer.blendshapeSettings;
            return settings.preserveBlinkBlendshapes ||
                   settings.preserveVisemeBlendshapes ||
                   settings.preserveFaceTrackingBlendshapes ||
                   settings.preserveEyeLookBlendshapes;
        }

        private Mesh CopyMeshWithoutBlendshapes(SkinnedMeshRenderer smr, Mesh sourceMesh, List<int> blendshapesToRemove)
        {
            var vertices = sourceMesh.vertices;
            var normals = sourceMesh.normals;
            var tangents = sourceMesh.tangents;

            ApplyCurrentRemovedBlendshapeWeights(smr, sourceMesh, blendshapesToRemove, vertices, normals, tangents);

            var newMesh = UnityEngine.Object.Instantiate(sourceMesh);
            newMesh.name = sourceMesh.name + "_Optimized";
            newMesh.ClearBlendShapes();
            newMesh.vertices = vertices;
            newMesh.normals = normals;
            newMesh.tangents = tangents;
            ObjectRegistry.RegisterReplacedObject(sourceMesh, newMesh);

            int blendShapeCount = sourceMesh.blendShapeCount;
            for (int i = 0; i < blendShapeCount; i++)
            {
                if (!blendshapesToRemove.Contains(i))
                {
                    string name = sourceMesh.GetBlendShapeName(i);
                    int frameCount = sourceMesh.GetBlendShapeFrameCount(i);

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        float weight = sourceMesh.GetBlendShapeFrameWeight(i, frame);
                        Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
                        Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
                        Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];

                        sourceMesh.GetBlendShapeFrameVertices(i, frame, deltaVertices, deltaNormals, deltaTangents);
                        newMesh.AddBlendShapeFrame(name, weight, deltaVertices, deltaNormals, deltaTangents);
                    }
                }
            }

            return newMesh;
        }

        private void ApplyCurrentRemovedBlendshapeWeights(SkinnedMeshRenderer smr, Mesh sourceMesh,
            List<int> blendshapesToRemove, Vector3[] vertices, Vector3[] normals, Vector4[] tangents)
        {
            foreach (var index in blendshapesToRemove)
            {
                float weight = smr.GetBlendShapeWeight(index);
                if (weight == 0) continue;
                if (sourceMesh.GetBlendShapeFrameCount(index) != 1)
                    throw new InvalidOperationException("Nonzero multi-frame shape was not excluded before baking");
                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(index, 0);
                ApplyBlendshapeFrameToMesh(sourceMesh, index, 0, weight / frameWeight, vertices, normals,
                    tangents, normals.Length == vertices.Length, tangents.Length == vertices.Length);
            }
        }

        private void ApplyBlendshapeFrameToMesh(
            Mesh sourceMesh,
            int blendshapeIndex,
            int frame,
            float scale,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            bool hasNormals,
            bool hasTangents)
        {
            if (scale == 0)
                return;

            Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
            Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
            Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];
            sourceMesh.GetBlendShapeFrameVertices(blendshapeIndex, frame, deltaVertices, deltaNormals, deltaTangents);

            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += deltaVertices[i] * scale;

                if (hasNormals)
                {
                    normals[i] += deltaNormals[i] * scale;
                }

                if (hasTangents)
                {
                    tangents[i].x += deltaTangents[i].x * scale;
                    tangents[i].y += deltaTangents[i].y * scale;
                    tangents[i].z += deltaTangents[i].z * scale;
                }
            }
        }

        #endregion

        #region Bone Optimization

        private void RemoveUnusedBoneReferences()
        {
            LogBuffered("[AvatarOptimizer] --- RemoveUnusedBoneReferences START ---");
            PushLogIndent();

            var settings = optimizer.boneSettings;
            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int totalRemoved = 0;

            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0) continue;

                if (GuardedAvatarPasses.IsGuardedRenderer(optimizer, smr)) continue;
                var mesh = GetOrCreateMeshCopy(smr, "_BoneOptimized");
                if (mesh == null) continue;

                var boneWeights = mesh.boneWeights;

                if (boneWeights == null || boneWeights.Length == 0) continue;

                var usedBoneIndices = new HashSet<int>();
                float threshold = settings.minimumBoneWeightThreshold;

                foreach (var weight in boneWeights)
                {
                    if (weight.weight0 > threshold) usedBoneIndices.Add(weight.boneIndex0);
                    if (weight.weight1 > threshold) usedBoneIndices.Add(weight.boneIndex1);
                    if (weight.weight2 > threshold) usedBoneIndices.Add(weight.boneIndex2);
                    if (weight.weight3 > threshold) usedBoneIndices.Add(weight.boneIndex3);
                }

                if (usedBoneIndices.Count == smr.bones.Length)
                {
                    continue;
                }

                var oldToNewBoneIndex = new Dictionary<int, int>();
                var newBones = new List<Transform>();
                var newBindPoses = new List<Matrix4x4>();

                for (int i = 0; i < smr.bones.Length; i++)
                {
                    bool shouldKeep = usedBoneIndices.Contains(i) || !settings.onlyRemoveZeroWeightBones;

                    if (shouldKeep)
                    {
                        oldToNewBoneIndex[i] = newBones.Count;
                        newBones.Add(smr.bones[i]);
                        if (i < mesh.bindposes.Length)
                            newBindPoses.Add(mesh.bindposes[i]);
                    }
                }

                int removed = smr.bones.Length - newBones.Count;
                if (removed > 0 && newBones.Count > 0)
                {
                    try
                    {
                        var newBoneWeights = new BoneWeight[boneWeights.Length];
                        for (int i = 0; i < boneWeights.Length; i++)
                        {
                            var weight = boneWeights[i];
                            newBoneWeights[i] = new BoneWeight
                            {
                                boneIndex0 = oldToNewBoneIndex.ContainsKey(weight.boneIndex0) ? oldToNewBoneIndex[weight.boneIndex0] : 0,
                                boneIndex1 = oldToNewBoneIndex.ContainsKey(weight.boneIndex1) ? oldToNewBoneIndex[weight.boneIndex1] : 0,
                                boneIndex2 = oldToNewBoneIndex.ContainsKey(weight.boneIndex2) ? oldToNewBoneIndex[weight.boneIndex2] : 0,
                                boneIndex3 = oldToNewBoneIndex.ContainsKey(weight.boneIndex3) ? oldToNewBoneIndex[weight.boneIndex3] : 0,
                                weight0 = weight.weight0,
                                weight1 = weight.weight1,
                                weight2 = weight.weight2,
                                weight3 = weight.weight3
                            };

                            if (newBoneWeights[i].boneIndex0 >= newBones.Count) newBoneWeights[i].boneIndex0 = 0;
                            if (newBoneWeights[i].boneIndex1 >= newBones.Count) newBoneWeights[i].boneIndex1 = 0;
                            if (newBoneWeights[i].boneIndex2 >= newBones.Count) newBoneWeights[i].boneIndex2 = 0;
                            if (newBoneWeights[i].boneIndex3 >= newBones.Count) newBoneWeights[i].boneIndex3 = 0;
                        }

                        while (newBindPoses.Count < newBones.Count)
                        {
                            newBindPoses.Add(Matrix4x4.identity);
                        }

                        mesh.boneWeights = newBoneWeights;
                        mesh.bindposes = newBindPoses.ToArray();
                        smr.bones = newBones.ToArray();

                        totalRemoved += removed;
                        LogBuffered($"[AvatarOptimizer] Removed {removed} bone references from {smr.name}");
                    }
                    catch (System.Exception ex)
                    {
                        LogBuffered($"[AvatarOptimizer] Failed to optimize bones on {smr.name}: {ex.Message}");
                    }
                }
            }

            optimizer.stats.boneReferencesRemoved = totalRemoved;
            LogBuffered($"[AvatarOptimizer] Total bone references removed: {totalRemoved}");

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- RemoveUnusedBoneReferences END ---");
        }

        private void RemoveBonesWithoutWeights()
        {
            LogBuffered("[AvatarOptimizer] --- RemoveBonesWithoutWeights START ---");
            PushLogIndent();

            var settings = optimizer.boneSettings;

            bool hasPhysics = false;
            string physicsWarning = "";

            if (settings.checkForMagicaCloth)
            {
                if (CheckForComponentType("MagicaCloth.MagicaCloth"))
                {
                    hasPhysics = true;
                    physicsWarning += "Magica Cloth ";
                }
                else if (CheckForComponentType("MagicaCloth2.MagicaCloth") ||
                         CheckForComponentType("MagicaCloth2.ClothComponent"))
                {
                    hasPhysics = true;
                    physicsWarning += "Magica Cloth 2 ";
                }
            }

            if (settings.checkForDynamicBones && CheckForComponentType("DynamicBone"))
            {
                hasPhysics = true;
                physicsWarning += "Dynamic Bones ";
            }

            if (settings.checkForVRCPhysBones && CheckForComponentType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone"))
            {
                hasPhysics = true;
                physicsWarning += "VRCPhysBones ";
            }

            if (hasPhysics && settings.manualConfirmationPerBone)
            {
                LogBuffered($"[AvatarOptimizer] Detected {physicsWarning}- Manual confirmation required for bone removal");
            }

            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var usedBones = new HashSet<Transform>();

            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.bones != null)
                {
                    foreach (var bone in smr.bones)
                    {
                        if (bone != null)
                        {
                            usedBones.Add(bone);

                            if (settings.preserveChildrenOfUsedBones)
                            {
                                foreach (Transform child in bone.GetComponentsInChildren<Transform>(true))
                                {
                                    usedBones.Add(child);
                                }
                            }
                        }
                    }
                }
            }

            foreach (var bone in animatedBones)
            {
                usedBones.Add(bone);
            }

            var bonesToCheck = context.AvatarRootTransform.GetComponentsInChildren<Transform>(true)
                .Where(t => !usedBones.Contains(t) && t != context.AvatarRootTransform)
                .ToList();

            var preservePatterns = settings.preserveBoneNamePatterns.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            int removedCount = 0;
            foreach (var bone in bonesToCheck)
            {
                bool matchesPreservePattern = preservePatterns.Any(pattern =>
                    bone.name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                if (matchesPreservePattern)
                {
                    LogBuffered($"[AvatarOptimizer] Preserving bone '{bone.name}' (matches name pattern)");
                    continue;
                }

                if (settings.preserveBonesWithConstraints)
                {
                    var constraints = bone.GetComponents<Component>()
                        .Where(c => c.GetType().Name.Contains("Constraint"));

                    if (constraints.Any())
                    {
                        LogBuffered($"[AvatarOptimizer] Preserving bone '{bone.name}' (has constraints)");
                        continue;
                    }
                }

                if (settings.manualConfirmationPerBone)
                {
                    LogBuffered($"[AvatarOptimizer] Bone candidate for removal: '{bone.name}' at path: {GetTransformPath(bone)}");
                }
                else
                {
                    LogBuffered($"[AvatarOptimizer] Would remove unused bone: '{bone.name}' (disabled by default for safety)");
                }
            }

            optimizer.stats.bonesRemoved = removedCount;

            if (settings.manualConfirmationPerBone && bonesToCheck.Count > 0)
            {
                LogBuffered($"[AvatarOptimizer] Found {bonesToCheck.Count} bones that could be removed. " +
                            "Check console for details. Actual removal disabled for safety.");
            }

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- RemoveBonesWithoutWeights END ---");
        }

        private bool CheckForComponentType(string typeName)
        {
            var type = FindType(typeName);
            if (type == null) return false;

            if (!typeof(Component).IsAssignableFrom(type))
            {
                return false;
            }

            try
            {
                return context.AvatarRootTransform.GetComponentsInChildren(type, true).Length > 0;
            }
            catch (System.Exception ex)
            {
                LogBuffered($"[AvatarOptimizer] Error checking for component type '{typeName}': {ex.Message}");
                return false;
            }
        }

        private string GetTransformPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null && transform.parent != context.AvatarRootTransform)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        #endregion

        #region Mesh Optimization

        private bool ShouldProcessMesh(SkinnedMeshRenderer smr)
        {
            var settings = optimizer.meshSettings;
            string meshName = smr.name;

            if (!string.IsNullOrEmpty(settings.meshNameFilter))
            {
                var includePatterns = settings.meshNameFilter.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                bool matches = includePatterns.Any(pattern =>
                    meshName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                if (!matches) return false;
            }

            if (!string.IsNullOrEmpty(settings.meshNameExclude))
            {
                var excludePatterns = settings.meshNameExclude.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                bool matches = excludePatterns.Any(pattern =>
                    meshName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                if (matches) return false;
            }

            return true;
        }

        private void OptimizeMeshes()
        {
            LogBuffered("[AvatarOptimizer] --- OptimizeMeshes START ---");
            PushLogIndent();

            var settings = optimizer.meshSettings;
            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int totalVerticesMerged = 0;
            int totalLooseRemoved = 0;
            int modifiedMeshCount = 0;

            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.sharedMesh == null || !ShouldProcessMesh(smr)) continue;

                if (GuardedAvatarPasses.IsGuardedRenderer(optimizer, smr)) continue;
                var mesh = GetOrCreateMeshCopy(smr, "_MeshOptimized");
                if (mesh == null) continue;

                bool modified = false;
                bool hasSkinnedData = mesh.boneWeights != null && mesh.boneWeights.Length > 0;
                bool hasBlendShapes = mesh.blendShapeCount > 0;

                if (settings.mergeVerticesByDistance)
                {
                    int beforeCount = mesh.vertexCount;
                    int merged = MergeVertices(mesh, settings, hasSkinnedData, hasBlendShapes);
                    totalVerticesMerged += merged;

                    if (merged > 0)
                    {
                        modified = true;
                        string mergeLabel = hasSkinnedData ? "merged safely" : "merged";
                        LogBuffered($"[AvatarOptimizer] {mergeLabel} {merged} vertices in {smr.name}");
                    }
                    else if (hasBlendShapes)
                    {
                        LogBuffered($"[AvatarOptimizer] Applying conservative blendshape-safe cleanup to {smr.name}");
                    }
                }

                if (settings.deleteLooseVertices)
                {
                    int removed = RemoveLooseVertices(mesh);
                    totalLooseRemoved += removed;

                    if (removed > 0)
                    {
                        modified = true;
                        string cleanupLabel = hasBlendShapes ? "removed" : "removed";
                        LogBuffered($"[AvatarOptimizer] {cleanupLabel} {removed} loose vertices in {smr.name}");
                    }
                }

                if (settings.recalculateNormals)
                {
                    mesh.RecalculateNormals();
                    modified = true;
                }

                if (settings.recalculateTangents)
                {
                    mesh.RecalculateTangents();
                    modified = true;
                }

                if (settings.optimizeMeshForRendering && !hasSkinnedData && !hasBlendShapes)
                {
                    mesh.Optimize();
                    modified = true;
                }

                if (settings.applyMeshCompression && !hasSkinnedData && !hasBlendShapes)
                {
                    ModelImporterMeshCompression compressionLevel = settings.compressionLevel switch
                    {
                        AvatarOptimizer.MeshOptimizationSettings.MeshCompressionLevel.Low => ModelImporterMeshCompression.Low,
                        AvatarOptimizer.MeshOptimizationSettings.MeshCompressionLevel.Medium => ModelImporterMeshCompression.Medium,
                        AvatarOptimizer.MeshOptimizationSettings.MeshCompressionLevel.High => ModelImporterMeshCompression.High,
                        _ => ModelImporterMeshCompression.Medium
                    };

                    MeshUtility.SetMeshCompression(mesh, compressionLevel);
                    modified = true;
                }

                if (modified)
                {
                    modifiedMeshCount++;
                }

                if (modified && !hasSkinnedData)
                {
                    mesh.RecalculateBounds();

                    if (optimizer.meshSettings.optimizeIndexBuffer)
                        OptimizeIndexBuffer(mesh);

                    if (optimizer.meshSettings.mergeIdenticalSubmeshes &&
                        !MaterialAnimationUtility.HasMaterialSwaps(context.AvatarRootTransform, smr))
                        MergeIdenticalSubmeshes(mesh, smr.sharedMaterials);

                    if (optimizer.meshSettings.stripUnusedAttributes)
                        StripUnusedVertexAttributesIntelligent(mesh, MaterialAnimationUtility.GetReferencedMaterials(context.AvatarRootTransform, smr).ToArray());
                }
            }

            optimizer.stats.verticesMerged = totalVerticesMerged;
            optimizer.stats.looseVerticesRemoved = totalLooseRemoved;
            optimizer.stats.meshesModified = modifiedMeshCount;

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- OptimizeMeshes END ---");
            LogBuffered($"[AvatarOptimizer] Mesh optimization summary: {totalVerticesMerged} vertices merged, {totalLooseRemoved} loose vertices removed across {modifiedMeshCount} meshes");
        }

        private int MergeVertices(Mesh mesh, AvatarOptimizer.MeshOptimizationSettings settings, bool hasSkinnedData, bool hasBlendShapes)
        {
            if (mesh == null) return 0;

            if (hasBlendShapes)
            {
                return 0;
            }

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv = mesh.uv;
            var colors = mesh.colors;
            var boneWeights = mesh.boneWeights;

            var vertexMap = new Dictionary<int, int>();
            var newVertices = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newTangents = new List<Vector4>();
            var newUV = new List<Vector2>();
            var newColors = new List<Color>();
            var newBoneWeights = new List<BoneWeight>();

            float distanceSq = settings.mergeDistance * settings.mergeDistance;
            float normalThreshold = Mathf.Cos(settings.normalAngleThreshold * Mathf.Deg2Rad);
            float uvDistanceSq = settings.uvDistanceThreshold * settings.uvDistanceThreshold;
            const float boneWeightTolerance = 0.001f;

            for (int i = 0; i < vertices.Length; i++)
            {
                bool merged = false;

                for (int j = 0; j < newVertices.Count; j++)
                {
                    if ((vertices[i] - newVertices[j]).sqrMagnitude >= distanceSq)
                        continue;

                    if (settings.compareNormals && normals.Length > i && newNormals.Count > j)
                    {
                        float normalDot = Vector3.Dot(normals[i].normalized, newNormals[j].normalized);
                        if (normalDot < normalThreshold)
                            continue;
                    }

                    if (settings.compareUVs && uv.Length > i && newUV.Count > j)
                    {
                        if ((uv[i] - newUV[j]).sqrMagnitude >= uvDistanceSq)
                            continue;
                    }

                    if (hasSkinnedData && boneWeights.Length > i && newBoneWeights.Count > j)
                    {
                        if (!HasCompatibleBoneWeights(boneWeights[i], newBoneWeights[j], boneWeightTolerance))
                            continue;
                    }

                    vertexMap[i] = j;
                    merged = true;
                    break;
                }

                if (!merged)
                {
                    vertexMap[i] = newVertices.Count;
                    newVertices.Add(vertices[i]);
                    if (normals.Length > i) newNormals.Add(normals[i]);
                    if (tangents.Length > i) newTangents.Add(tangents[i]);
                    if (uv.Length > i) newUV.Add(uv[i]);
                    if (colors.Length > i) newColors.Add(colors[i]);
                    if (boneWeights.Length > i) newBoneWeights.Add(boneWeights[i]);
                }
            }

            if (newVertices.Count == vertices.Length)
                return 0;

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var triangles = mesh.GetTriangles(i);
                for (int j = 0; j < triangles.Length; j++)
                {
                    triangles[j] = vertexMap[triangles[j]];
                }
                mesh.SetTriangles(triangles, i);
            }

            mesh.SetVertices(newVertices);
            if (newNormals.Count > 0) mesh.SetNormals(newNormals);
            if (newTangents.Count > 0) mesh.SetTangents(newTangents);
            if (newUV.Count > 0) mesh.SetUVs(0, newUV);
            if (newColors.Count > 0) mesh.SetColors(newColors);
            if (newBoneWeights.Count > 0) mesh.boneWeights = newBoneWeights.ToArray();

            mesh.RecalculateBounds();
            if (mesh.vertexCount > 0 && mesh.normals.Length != mesh.vertexCount)
            {
                mesh.RecalculateNormals();
            }

            return vertices.Length - newVertices.Count;
        }

        private bool HasCompatibleBoneWeights(BoneWeight a, BoneWeight b, float tolerance)
        {
            return a.boneIndex0 == b.boneIndex0
                && a.boneIndex1 == b.boneIndex1
                && a.boneIndex2 == b.boneIndex2
                && a.boneIndex3 == b.boneIndex3
                && Mathf.Abs(a.weight0 - b.weight0) <= tolerance
                && Mathf.Abs(a.weight1 - b.weight1) <= tolerance
                && Mathf.Abs(a.weight2 - b.weight2) <= tolerance
                && Mathf.Abs(a.weight3 - b.weight3) <= tolerance;
        }

        private int RemoveLooseVertices(Mesh mesh)
        {
            var usedVertices = new HashSet<int>();

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var triangles = mesh.GetTriangles(i);
                foreach (var index in triangles)
                {
                    usedVertices.Add(index);
                }
            }

            int removed = mesh.vertexCount - usedVertices.Count;

            if (removed > 0)
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uv = mesh.uv;
                var colors = mesh.colors;
                var boneWeights = mesh.boneWeights;

                var oldToNew = new Dictionary<int, int>();
                var newVertices = new List<Vector3>();
                var newNormals = new List<Vector3>();
                var newTangents = new List<Vector4>();
                var newUV = new List<Vector2>();
                var newColors = new List<Color>();
                var newBoneWeights = new List<BoneWeight>();

                var blendShapeFrames = new List<(string name, float weight, Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)>();

                if (mesh.blendShapeCount > 0)
                {
                    for (int shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                    {
                        string shapeName = mesh.GetBlendShapeName(shapeIndex);
                        int frameCount = mesh.GetBlendShapeFrameCount(shapeIndex);
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            var deltaVertices = new Vector3[mesh.vertexCount];
                            var deltaNormals = new Vector3[mesh.vertexCount];
                            var deltaTangents = new Vector3[mesh.vertexCount];
                            mesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                            float frameWeight = mesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);

                            blendShapeFrames.Add((shapeName, frameWeight, deltaVertices, deltaNormals, deltaTangents));
                        }
                    }
                }

                for (int i = 0; i < vertices.Length; i++)
                {
                    if (usedVertices.Contains(i))
                    {
                        oldToNew[i] = newVertices.Count;
                        newVertices.Add(vertices[i]);
                        if (normals.Length > i) newNormals.Add(normals[i]);
                        if (tangents.Length > i) newTangents.Add(tangents[i]);
                        if (uv.Length > i) newUV.Add(uv[i]);
                        if (colors.Length > i) newColors.Add(colors[i]);
                        if (boneWeights.Length > i) newBoneWeights.Add(boneWeights[i]);
                    }
                }

                var subMeshTriangles = new List<int[]>();
                int subMeshCount = mesh.subMeshCount;
                for (int i = 0; i < subMeshCount; i++)
                {
                    var triangles = mesh.GetTriangles(i);
                    for (int j = 0; j < triangles.Length; j++)
                    {
                        triangles[j] = oldToNew[triangles[j]];
                    }
                    subMeshTriangles.Add(triangles);
                }

                var bindposes = mesh.bindposes;
                mesh.Clear(false);
                mesh.bindposes = bindposes;

                mesh.SetVertices(newVertices);
                if (newNormals.Count > 0) mesh.SetNormals(newNormals);
                if (newTangents.Count > 0) mesh.SetTangents(newTangents);
                if (newUV.Count > 0) mesh.SetUVs(0, newUV);
                if (newColors.Count > 0) mesh.SetColors(newColors);
                if (newBoneWeights.Count > 0) mesh.boneWeights = newBoneWeights.ToArray();

                mesh.subMeshCount = subMeshCount;
                for (int i = 0; i < subMeshCount; i++)
                {
                    mesh.SetTriangles(subMeshTriangles[i], i);
                }

                if (mesh.blendShapeCount > 0)
                {
                    mesh.Clear(false);
                    mesh.bindposes = bindposes;
                    mesh.SetVertices(newVertices);
                    if (newNormals.Count > 0) mesh.SetNormals(newNormals);
                    if (newTangents.Count > 0) mesh.SetTangents(newTangents);
                    if (newUV.Count > 0) mesh.SetUVs(0, newUV);
                    if (newColors.Count > 0) mesh.SetColors(newColors);
                    if (newBoneWeights.Count > 0) mesh.boneWeights = newBoneWeights.ToArray();

                    mesh.subMeshCount = subMeshCount;
                    for (int i = 0; i < subMeshCount; i++)
                    {
                        mesh.SetTriangles(subMeshTriangles[i], i);
                    }

                    foreach (var frame in blendShapeFrames)
                    {
                        var newDeltaVertices = new Vector3[newVertices.Count];
                        var newDeltaNormals = new Vector3[newVertices.Count];
                        var newDeltaTangents = new Vector3[newVertices.Count];

                        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                        {
                            if (usedVertices.Contains(vertexIndex))
                            {
                                int targetIndex = oldToNew[vertexIndex];
                                newDeltaVertices[targetIndex] = frame.deltaVertices[vertexIndex];
                                newDeltaNormals[targetIndex] = frame.deltaNormals[vertexIndex];
                                newDeltaTangents[targetIndex] = frame.deltaTangents[vertexIndex];
                            }
                        }

                        mesh.AddBlendShapeFrame(frame.name, frame.weight, newDeltaVertices, newDeltaNormals, newDeltaTangents);
                    }
                }
            }

            return removed;
        }

        #endregion

        #region Mesh Combining

        private readonly Dictionary<string, List<SkinnedMeshRenderer>> toggleRenderersByPath = new Dictionary<string, List<SkinnedMeshRenderer>>();
        private readonly Dictionary<SkinnedMeshRenderer, string> toggleHideShapeNames = new Dictionary<SkinnedMeshRenderer, string>();
        private bool toggleMergePrepared;
        private Dictionary<string, string[]> mergeMaskPaths;

        public void PrepareTogglePreservingMerge()
        {
            if (optimizer.reportOnly || !optimizer.meshSettings.mergeToggleObjects)
            {
                LogBuffered("[AvatarOptimizer] Toggle conversion disabled by Merge Toggle Objects or Report Only; preserving original visibility");
                return;
            }
            toggleMergePrepared = false;
            toggleRenderersByPath.Clear();
            toggleHideShapeNames.Clear();

            var selectedBackend = ResolveToggleMergeBackend();
            if (selectedBackend != AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.InfinimationBlendshape)
            {
                LogBuffered($"[AvatarOptimizer] Toggle backend '{selectedBackend}' is not supported yet; skipping toggle conversion");
                return;
            }

            var renderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.vertexCount > 0)
                .ToArray();
            var claimedRenderers = new HashSet<SkinnedMeshRenderer>();
            var togglePaths = new HashSet<string>();

            var controllers = GetAvatarAnimatorControllers(true).ToList();
            LogBuffered($"[AvatarOptimizer] Scanning {controllers.Count} Animator Controller(s) for toggle bindings...");

            foreach (var controller in controllers)
            {
                var clips = GetAllAnimationClips(controller);
                LogBuffered($"  • Controller '{controller.name}': {clips.Count()} clip(s) found");

                foreach (var clip in clips)
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if ((binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled" ||
                             binding.propertyName.StartsWith("m_IsActive", StringComparison.Ordinal) ||
                             binding.propertyName.StartsWith("m_Enabled", StringComparison.Ordinal)) &&
                            !string.IsNullOrEmpty(binding.path))
                        {
                            togglePaths.Add(binding.path);
                        }
                    }
                }
            }

            LogBuffered($"[AvatarOptimizer] Found {togglePaths.Count} unique toggle path(s) across animation clips");

            foreach (var path in togglePaths.OrderBy(path => path.Count(character => character == '/')))
            {
                var target = context.AvatarRootTransform.Find(path);
                if (target == null)
                    continue;

                var preparationReason = GuardedAvatarPasses.TogglePreparationRejection(optimizer, target);
                if (IsAdvancedTaggedTransform(target) || preparationReason != null)
                {
                    LogBuffered($"[AvatarOptimizer] Skipping toggle target '{path}': {preparationReason ?? "Advanced Tagging boundary"}");
                    continue;
                }

                var targetRenderers = renderers
                    .Where(renderer => renderer.transform == target)
                    .Where(renderer => !IsAdvancedTaggedTransform(renderer.transform))
                    .Where(renderer => !claimedRenderers.Contains(renderer))
                    .ToList();
                if (targetRenderers.Count == 0)
                    continue;

                foreach (var renderer in targetRenderers)
                    claimedRenderers.Add(renderer);

                toggleRenderersByPath[path] = targetRenderers;
            }

            if (toggleRenderersByPath.Count == 0)
            {
                LogBuffered("[AvatarOptimizer] No supported GameObject visibility toggles found");
                return;
            }

            foreach (var pair in toggleRenderersByPath)
            {
                foreach (var renderer in pair.Value)
                {
                    var mesh = UnityEngine.Object.Instantiate(renderer.sharedMesh);
                    mesh.name = renderer.sharedMesh.name + "_ToggleMerge";
                    ObjectRegistry.RegisterReplacedObject(renderer.sharedMesh, mesh);

                    var shapeName = MakeToggleHideShapeName(mesh, pair.Key, renderer.name);
                    var deltas = Enumerable.Repeat(Vector3.positiveInfinity, mesh.vertexCount).ToArray();
                    mesh.AddBlendShapeFrame(shapeName, 100f, deltas, null, null);
                    renderer.sharedMesh = mesh;
                    toggleHideShapeNames[renderer] = shapeName;

                    var target = context.AvatarRootTransform.Find(pair.Key);
                    if (target != null && !target.gameObject.activeSelf)
                    {
                        renderer.SetBlendShapeWeight(mesh.GetBlendShapeIndex(shapeName), 100f);
                    }
                }

                // Keep the source object toggle intact for children, overlays and runtime components.
            }

            RecreateToggleAnimationBindings();
            toggleMergePrepared = toggleHideShapeNames.Count > 0;
            LogBuffered($"[AvatarOptimizer] Converted {toggleRenderersByPath.Count} toggle objects and {toggleHideShapeNames.Count} renderers to hide blendshapes");
        }

        private AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend ResolveToggleMergeBackend()
        {
            if (optimizer.meshSettings.toggleMergeBackend == AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.Automatic)
            {
                var renderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var supportedRendererCount = renderers.Count(renderer =>
                    renderer != null && renderer.sharedMesh != null && renderer.sharedMesh.vertexCount > 0);
                var hideShapeBytes = renderers
                    .Where(renderer => renderer != null && renderer.sharedMesh != null)
                    .Sum(renderer => (long)renderer.sharedMesh.vertexCount * sizeof(float) * 3);

                LogBuffered($"[AvatarOptimizer] Automatic toggle backend evaluation: {supportedRendererCount} renderer(s) support Infinimation; estimated hide-shape storage {hideShapeBytes / (1024f * 1024f):F1} MB");
                return AvatarOptimizer.MeshOptimizationSettings.ToggleMergeBackend.InfinimationBlendshape;
            }

            return optimizer.meshSettings.toggleMergeBackend;
        }

        private string MakeToggleHideShapeName(Mesh mesh, string path, string rendererName)
        {
            var baseName = "MT_Hide_" + SanitizeToggleName(path) + "_" + SanitizeToggleName(rendererName);
            var shapeName = baseName;
            var suffix = 1;
            while (mesh.GetBlendShapeIndex(shapeName) >= 0)
                shapeName = baseName + "_" + suffix++;
            return shapeName;
        }

        private string SanitizeToggleName(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            return builder.ToString();
        }

        private void RecreateToggleAnimationBindings()
        {
            ProcessAndReplaceControllerClips(CreateToggleReplacementClip, "_ToggleMerge");
        }

        private AnimationClip CreateToggleReplacementClip(AnimationClip sourceClip)
        {
            if (sourceClip == null) return null;

            var activeBindings = AnimationUtility.GetCurveBindings(sourceClip)
                .Where(binding => (binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled" ||
                                   binding.propertyName.StartsWith("m_IsActive", StringComparison.Ordinal) ||
                                   binding.propertyName.StartsWith("m_Enabled", StringComparison.Ordinal)) &&
                                  toggleRenderersByPath.ContainsKey(binding.path))
                .ToList();
            if (activeBindings.Count == 0)
                return null;

            var replacement = UnityEngine.Object.Instantiate(sourceClip);
            replacement.name = sourceClip.name + "_ToggleMerge";

            foreach (var binding in activeBindings)
            {
                var sourceCurve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                // Retain the original GameObject curve; unmerged overlays still depend on it.
                if (sourceCurve == null)
                    continue;

                var hideCurve = new AnimationCurve();
                foreach (var sourceKey in sourceCurve.keys)
                {
                    hideCurve.AddKey(new Keyframe(sourceKey.time, sourceKey.value > 0.5f ? 0f : 100f)
                    {
                        inTangent = float.PositiveInfinity,
                        outTangent = float.PositiveInfinity
                    });
                }

                foreach (var renderer in toggleRenderersByPath[binding.path])
                {
                    if (!toggleHideShapeNames.TryGetValue(renderer, out var shapeName))
                        continue;

                    var hideBinding = EditorCurveBinding.FloatCurve(
                        AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform),
                        typeof(SkinnedMeshRenderer),
                        "blendShape." + shapeName);
                    AnimationUtility.SetEditorCurve(replacement, hideBinding, hideCurve);
                }
            }

            return replacement;
        }

        private void ProcessAndReplaceControllerClips(Func<AnimationClip, AnimationClip> clipTransformer, string suffix)
        {
            var controllers = GetAvatarAnimatorControllers(true).ToList();
            int rewrittenClipCount = 0;

            foreach (var controller in controllers)
            {
                if (controller == null) continue;

                var clipMap = new Dictionary<AnimationClip, AnimationClip>();

                foreach (var clip in GetAllAnimationClips(controller))
                {
                    if (clip == null || clipMap.ContainsKey(clip)) continue;

                    // Transformers return unregistered copies; this method owns replacement tracking.
                    var replacement = clipTransformer(clip);
                    if (replacement != null)
                    {
                        ObjectRegistry.RegisterReplacedObject(clip, replacement);
                        clipMap[clip] = replacement;
                        rewrittenClipCount++;
                    }
                }

                if (clipMap.Count == 0) continue;

                var copies = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
                var finalController = (RuntimeAnimatorController)CloneAnimationGraph(controller, clipMap, copies, suffix);
                SetAvatarOverridesField(controller, finalController);

                if (context != null && context.AvatarRootTransform != null && finalController != null)
                {
                    var animators = context.AvatarRootTransform.GetComponentsInChildren<Animator>(true);
                    foreach (var anim in animators)
                    {
                        if (anim != null && anim.transform == context.AvatarRootTransform && anim.runtimeAnimatorController == controller)
                        {
                            anim.runtimeAnimatorController = finalController;
                        }
                    }
                }
            }

            if (rewrittenClipCount > 0)
            {
                LogBuffered($"[AvatarOptimizer] Processed and replaced {rewrittenClipCount} animation clip(s) for '{suffix}'");
            }
        }

        // Instantiate alone does not detach controller sub-assets. Clone the entire mutable graph.
        private UnityEngine.Object CloneAnimationGraph(UnityEngine.Object source,
            Dictionary<AnimationClip, AnimationClip> clipMap,
            Dictionary<UnityEngine.Object, UnityEngine.Object> copies, string suffix)
        {
            if (source == null) return null;
            if (source is AnimationClip clip)
                return clipMap.TryGetValue(clip, out var replacement) ? replacement : clip;
            if (copies.TryGetValue(source, out var existing)) return existing;
            // Native Instantiate recursively remaps strong controller pointers and asserts on
            // some NDMF-generated graphs. Copy into an empty object, then remap our own graph.
            UnityEngine.Object copy;
            if (source is AnimatorOverrideController) copy = new AnimatorOverrideController();
            else if (source is AnimatorController) copy = new AnimatorController();
            else if (source is AnimatorStateMachine) copy = new AnimatorStateMachine();
            else if (source is AnimatorState) copy = new AnimatorState();
            else if (source is AnimatorStateTransition) copy = new AnimatorStateTransition();
            else if (source is AnimatorTransition) copy = new AnimatorTransition();
            else if (source is BlendTree) copy = new BlendTree();
            else if (source is AvatarMask) copy = new AvatarMask();
            else if (source is StateMachineBehaviour) copy = ScriptableObject.CreateInstance(source.GetType());
            else throw new InvalidOperationException("Unsupported animation graph object: " + source.GetType().FullName);
            EditorUtility.CopySerialized(source, copy);
            copy.name = source.name + suffix;
            copies[source] = copy;
            ObjectRegistry.RegisterReplacedObject(source, copy);
            if (source is AvatarMask sourceMask && mergeMaskPaths != null)
            {
                var mask = (AvatarMask)copy;
                foreach (var mapping in mergeMaskPaths.Where(_ => sourceMask.transformCount > 0))
                {
                    int index = mask.transformCount; mask.transformCount = index + 1;
                    mask.SetTransformPath(index, mapping.Key);
                    mask.SetTransformActive(index, GuardedAvatarPasses.MaskIncludes(sourceMask, mapping.Value[0]));
                }
                return copy;
            }
            if (source is AnimatorOverrideController originalOverride)
            {
                // Preserve the mapping before assigning the cloned base, which rebuilds override keys.
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                originalOverride.GetOverrides(overrides);
                var overrideCopy = (AnimatorOverrideController)copy;
                overrideCopy.runtimeAnimatorController = (RuntimeAnimatorController)CloneAnimationGraph(
                    originalOverride.runtimeAnimatorController, clipMap, copies, suffix);
                overrideCopy.ApplyOverrides(overrides.Select(pair => new KeyValuePair<AnimationClip, AnimationClip>(
                    (AnimationClip)CloneAnimationGraph(pair.Key, clipMap, copies, suffix),
                    (AnimationClip)CloneAnimationGraph(pair.Value, clipMap, copies, suffix))).ToList());
                return copy;
            }
            var serialized = new SerializedObject(copy);
            // Instantiate may already remap native self/parent references to new objects.
            // Traverse the stable source graph so the source->copy map also breaks those cycles.
            var sourceSerialized = new SerializedObject(source);
            var property = sourceSerialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                var reference = property.objectReferenceValue;
                if (reference is AnimationClip || reference is AnimatorController ||
                    reference is AnimatorStateMachine || reference is AnimatorState ||
                    reference is AnimatorTransitionBase || reference is BlendTree || reference is StateMachineBehaviour || reference is AvatarMask)
                {
                    var destination = serialized.FindProperty(property.propertyPath);
                    if (destination == null) throw new InvalidOperationException("Animator graph property missing from copy: " + property.propertyPath);
                    destination.objectReferenceValue = CloneAnimationGraph(reference, clipMap, copies, suffix);
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return copy;
        }

        private void SetAvatarOverridesField(RuntimeAnimatorController original, RuntimeAnimatorController replacement)
        {
            if (replacement == null || cvrAvatarType == null || context?.AvatarRootTransform == null) return;

            var cvrComponents = context.AvatarRootTransform.GetComponentsInChildren(cvrAvatarType, true);
            foreach (var comp in cvrComponents)
            {
                if (comp == null || comp.transform != context.AvatarRootTransform) continue;
                var type = comp.GetType();
                var settings = GetFieldValue(comp, "avatarSettings") ?? GetPropertyValue(comp, "avatarSettings");
                if (settings != null)
                    foreach (var settingField in settings.GetType().GetFields())
                        if (settingField.Name != "baseController" && settingField.Name != "baseOverrideController" && typeof(RuntimeAnimatorController).IsAssignableFrom(settingField.FieldType) &&
                            Equals(settingField.GetValue(settings), original) && settingField.FieldType.IsInstanceOfType(replacement))
                            settingField.SetValue(settings, replacement);
                var field = type.GetField("overrides", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    if (Equals(field.GetValue(comp), original) && field.FieldType.IsInstanceOfType(replacement))
                        field.SetValue(comp, replacement);
                    continue;
                }
                var prop = type.GetProperty("overrides", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null && prop.CanRead && prop.CanWrite &&
                    Equals(prop.GetValue(comp), original) && prop.PropertyType.IsInstanceOfType(replacement))
                {
                    prop.SetValue(comp, replacement);
                }
            }
        }

        private void CombineMeshes()
        {
            LogBuffered("[AvatarOptimizer] --- CombineMeshes START ---");
            PushLogIndent();

            if (!optimizer.meshSettings.combineMeshes && !UseRelaxedToggleMerge())
            {
                LogBuffered("[AvatarOptimizer] Mesh combining is disabled in settings; skipping");
                optimizer.stats.meshesCombined = 0;
                PopLogIndent();
                LogBuffered("[AvatarOptimizer] --- CombineMeshes END ---");
                return;
            }

            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinnedMeshRenderers == null || skinnedMeshRenderers.Length == 0)
            {
                LogBuffered("[AvatarOptimizer] No skinned mesh renderers found for combine pass");
                optimizer.stats.meshesCombined = 0;
                PopLogIndent();
                LogBuffered("[AvatarOptimizer] --- CombineMeshes END ---");
                return;
            }

            var combinableGroups = GroupCombinableMeshes(skinnedMeshRenderers);

            if (UseRelaxedToggleMerge())
            {
                LogBuffered($"[AvatarOptimizer] Relaxed toggle merge candidate groups: {combinableGroups.Count}");
            }

            int totalCombined = 0;

            foreach (var group in combinableGroups)
            {
                if (group.Count < 2) continue;
                var budgetReason = GuardedAvatarPasses.MergeBudgetRejection(optimizer, group);
                LogBuffered(GuardedAvatarPasses.DescribeGroup(group));
                if (budgetReason != null) { LogBuffered("[AvatarOptimizer] Merge budget rejected: " + budgetReason); continue; }

                var firstMaterialName = group[0].sharedMaterials != null && group[0].sharedMaterials.Length > 0 && group[0].sharedMaterials[0] != null
                    ? group[0].sharedMaterials[0].name
                    : "(no material)";
                LogBuffered($"[AvatarOptimizer] Combining {group.Count} meshes {(UseRelaxedToggleMerge() ? "with relaxed toggle compatibility " : string.Empty)}with material '{firstMaterialName}'");

                try
                {
                    CombineMeshGroup(group);
                    totalCombined += group.Count - 1;
                }
                catch (System.Exception ex)
                {
                    LogBuffered($"[AvatarOptimizer] Failed to combine mesh group: {ex.Message}");
                }
            }

            optimizer.stats.meshesCombined = totalCombined;
            LogBuffered($"[AvatarOptimizer] Combined {totalCombined} meshes");

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- CombineMeshes END ---");
        }

        private List<List<SkinnedMeshRenderer>> GroupCombinableMeshes(SkinnedMeshRenderer[] renderers)
        {
            using var analysis = GuardedAvatarPasses.BeginMergeAnalysis(optimizer);
            var groups = new List<List<SkinnedMeshRenderer>>();
            var processed = new HashSet<SkinnedMeshRenderer>();
            var rejectionReasons = new Dictionary<string, int>();

            foreach (var smr in renderers)
            {
                if (processed.Contains(smr) || smr.sharedMesh == null) continue;
                if (!CanCombineMesh(smr)) continue;

                var group = new List<SkinnedMeshRenderer> { smr };
                processed.Add(smr);

                foreach (var other in renderers)
                {
                    if (processed.Contains(other) || other.sharedMesh == null) continue;
                    if (!CanCombineMesh(other)) continue;

                    if (group.All(member => CanCombineTogether(member, other)))
                    {
                        group.Add(other);
                        processed.Add(other);
                    }
                    else if (optimizer.mergeBudgetSettings.showMergePlan)
                    {
                        var reason = GetCombineRejectionReason(smr, other);
                        rejectionReasons[reason] = rejectionReasons.TryGetValue(reason, out var count) ? count + 1 : 1;
                        if (optimizer.atlasSettings.verboseLogging)
                        {
                            LogBuffered($"[AvatarOptimizer] Toggle merge rejected '{other.name}' with '{smr.name}': {reason}");
                        }
                    }
                }

                if (group.Count >= 2)
                {
                    groups.Add(group);
                }
            }

            if (optimizer.mergeBudgetSettings.showMergePlan && rejectionReasons.Count > 0)
            {
                LogBuffered("[AvatarOptimizer] Toggle merge compatibility summary:");
                foreach (var rejection in rejectionReasons.OrderByDescending(pair => pair.Value))
                {
                    LogBuffered($"  {rejection.Value} pair checks rejected: {rejection.Key}");
                }
            }

            return groups;
        }

        private string GetCombineRejectionReason(SkinnedMeshRenderer first, SkinnedMeshRenderer second) =>
            GuardedAvatarPasses.MergeRejection(optimizer, first, second) ?? "blendshape preservation is disabled";

        private bool CanCombineMesh(SkinnedMeshRenderer smr)
        {
            string reason = GuardedAvatarPasses.RendererMergeRejection(optimizer, smr);
            if (reason != null)
            {
                if (optimizer.mergeBudgetSettings.showMergePlan) LogBuffered($"[AvatarOptimizer] Merge excluded {smr?.name}: {reason}");
                return false;
            }
            return true;
        }

        private bool CanCombineTogether(SkinnedMeshRenderer smr1, SkinnedMeshRenderer smr2)
        {
            if (smr1 == null || smr2 == null) return false;
            if (GuardedAvatarPasses.MergeRejection(optimizer, smr1, smr2) != null) return false;

            return optimizer.meshSettings.preserveBlendshapesWhenCombining || UseRelaxedToggleMerge() ||
                (smr1.sharedMesh.blendShapeCount == 0 && smr2.sharedMesh.blendShapeCount == 0);
        }

        private bool IsCVRFaceMesh(SkinnedMeshRenderer smr)
        {
            if (smr == null) return false;

            var cvrComponents = context.AvatarRootTransform.GetComponentsInChildren(cvrAvatarType, true);
            foreach (var cvrComp in cvrComponents)
            {
                if (cvrComp == null) continue;
                var faceMesh = GetFieldOrPropertyValue<SkinnedMeshRenderer>(cvrComp, "bodyMesh");
                if (faceMesh != null && faceMesh == smr)
                    return true;
            }

            return false;
        }

        private void CombineMeshGroup(List<SkinnedMeshRenderer> group)
        {
            var first = group[0];
            var combined = GuardedAvatarPasses.BuildMergedMesh(optimizer, group, out var bones, out var materials,
                out var slots, out var shapes);
            GameObject outputObject = null;
            try
            {
                // A sibling renderer stays visible independently of the source object's toggle.
                string name = first.name + "_Combined";
                int suffix = 1;
                while (first.transform.parent.Find(name) != null) name = first.name + "_Combined_" + suffix++;
                outputObject = new GameObject(name);
                outputObject.transform.SetParent(first.transform.parent, false);
                outputObject.transform.localPosition = first.transform.localPosition;
                outputObject.transform.localRotation = first.transform.localRotation;
                outputObject.transform.localScale = first.transform.localScale;
                outputObject.layer = first.gameObject.layer;
                var output = outputObject.AddComponent<SkinnedMeshRenderer>();
                EditorUtility.CopySerialized(first, output);
                outputObject.SetActive(GuardedAvatarPasses.HasToggleShape(first) || first.gameObject.activeSelf);
                // Source meshes and material arrays remain intact while offsets are remapped.
                mergeMaskPaths = new Dictionary<string, string[]> { { GetTransformPath(output.transform), group.Select(r => GetTransformPath(r.transform)).ToArray() } };
                try { ProcessAndReplaceControllerClips(clip => CreateReplacementClipForCombinedMesh(clip, group, output, slots, shapes), "_CombinedMesh"); }
                finally { mergeMaskPaths = null; }
                var initialWeights = group.SelectMany(r => shapes[r].Select(pair =>
                    (name: pair.Value, weight: r.GetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex(pair.Key))))).ToArray();
                var bounds = first.localBounds;
                foreach (var r in group.Skip(1)) bounds.Encapsulate(r.localBounds);
                output.sharedMesh = combined; output.sharedMaterials = materials; output.bones = bones; output.localBounds = bounds;
                foreach (var weight in initialWeights) output.SetBlendShapeWeight(combined.GetBlendShapeIndex(weight.name), weight.weight);
                ObjectRegistry.RegisterReplacedObject(first.sharedMesh, combined);
                ObjectRegistry.RegisterReplacedObject(first, output);
                foreach (var r in group) UnityEngine.Object.DestroyImmediate(r);
                LogBuffered($"[AvatarOptimizer] Combined '{combined.name}': {combined.vertexCount} vertices, {combined.subMeshCount} material slots, {combined.blendShapeCount} blendshapes; source toggles and overlay hierarchy preserved");
                combined = null; outputObject = null;
            }
            finally
            {
                if (combined != null) UnityEngine.Object.DestroyImmediate(combined);
                if (outputObject != null) UnityEngine.Object.DestroyImmediate(outputObject);
            }
        }

        private AnimationClip CreateReplacementClipForCombinedMesh(AnimationClip sourceClip, List<SkinnedMeshRenderer> group,
            SkinnedMeshRenderer firstRenderer, Dictionary<SkinnedMeshRenderer, int[]> slots,
            Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> shapes)
        {
            var floats = AnimationUtility.GetCurveBindings(sourceClip);
            var objects = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            var sourceByPath = group.ToDictionary(r => GetTransformPath(r.transform));
            bool Map(EditorCurveBinding binding, out EditorCurveBinding mapped)
            {
                mapped = binding;
                if (!sourceByPath.TryGetValue(binding.path, out var renderer)) return false;
                if (binding.type != typeof(SkinnedMeshRenderer) && binding.type != typeof(Renderer) &&
                    !(binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")) return false;
                if (binding.type == typeof(GameObject) && GuardedAvatarPasses.HasToggleShape(renderer)) return false;
                mapped.path = GetTransformPath(firstRenderer.transform);
                if (binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    mapped.propertyName = "blendShape." + shapes[renderer][binding.propertyName.Substring(11)];
                else if (GuardedAvatarPasses.MaterialSlot(binding.propertyName, out var index))
                    mapped.propertyName = "m_Materials.Array.data[" + slots[renderer][index] + "]";
                return true;
            }
            if (!floats.Concat(objects).Any(b => Map(b, out _))) return null;
            var copy = UnityEngine.Object.Instantiate(sourceClip);
            copy.name = sourceClip.name + "_CombinedMesh";
            // Clear first, then write: a destination can also be another source binding.
            foreach (var b in floats) if (Map(b, out _) && b.type != typeof(GameObject)) AnimationUtility.SetEditorCurve(copy, b, null);
            foreach (var b in objects) if (Map(b, out _)) AnimationUtility.SetObjectReferenceCurve(copy, b, null);
            foreach (var b in floats) if (Map(b, out var mapped)) AnimationUtility.SetEditorCurve(copy, mapped, AnimationUtility.GetEditorCurve(sourceClip, b));
            foreach (var b in objects) if (Map(b, out var mapped)) AnimationUtility.SetObjectReferenceCurve(copy, mapped, AnimationUtility.GetObjectReferenceCurve(sourceClip, b));
            return copy;
        }

        #endregion

        #region Texture Atlas Generation

        private void GenerateTextureAtlases()
        {
            LogBuffered("[AvatarOptimizer] --- GenerateTextureAtlases START ---");
            PushLogIndent();

            originalTextureMemory.Clear();
            accountedAtlases.Clear();
            totalAtlasMemoryBytes = 0;

            if (animatedMaterials.Count == 0)
            {
                CollectAnimatedMaterials();
            }

            var renderers = context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true);
            var shaderGroups = new Dictionary<string, (string ShaderName, string AnimSignature, List<Material> Materials)>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;

                    if (ShouldExcludeMaterialFromAtlas(mat)) continue;

                    if (!ShouldIncludeShader(mat.shader)) continue;

                    TrackOriginalTextureMemory(mat);

                    string shaderName = mat.shader.name;
                    string animSignature = GetAnimatedPropertySignature(mat);
                    string groupKey = shaderName + "\u0001" + animSignature;

                    if (!shaderGroups.TryGetValue(groupKey, out var group))
                    {
                        group = (shaderName, animSignature, new List<Material>());
                        shaderGroups[groupKey] = group;
                    }

                    if (!group.Materials.Contains(mat))
                        group.Materials.Add(mat);
                }
            }

            int atlasesCreated = 0;

            foreach (var kvp in shaderGroups)
            {
                var shaderName = kvp.Value.ShaderName;
                var animSignature = kvp.Value.AnimSignature;
                var materials = kvp.Value.Materials;

                if (materials.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
                    continue;

                LogBuffered($"[AvatarOptimizer] Analyzing shader: {shaderName} ({materials.Count} materials, {DescribeAnimatedSignature(animSignature)})");

                int generated;
                if (optimizer.atlasSettings.useEnhancedAtlasWorkflow)
                {
                    generated = GenerateAtlasWithEnhancedWorkflow(shaderName, materials);
                }
                else if (optimizer.atlasSettings.mergeIdenticalTextures)
                {
                    generated = GenerateAtlasWithTextureMerging(shaderName, materials);
                }
                else
                {
                    generated = GenerateAtlasForShaderAutomatic(shaderName, materials);
                }

                atlasesCreated += generated;
            }

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool updated = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null && materialCopies.ContainsKey(materials[i]))
                    {
                        materials[i] = materialCopies[materials[i]];
                        updated = true;
                    }
                }

                if (updated)
                {
                    renderer.sharedMaterials = materials;
                }
            }

            long originalMemoryBytes = originalTextureMemory.Values.Sum();
            long savedBytes = originalMemoryBytes - totalAtlasMemoryBytes;
            float savedMB = savedBytes / (1024f * 1024f);

            optimizer.stats.textureMemorySavedMB = Mathf.RoundToInt(savedMB);
            optimizer.stats.atlasesGenerated = atlasesCreated;

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- GenerateTextureAtlases END ---");
        }

        private int GenerateAtlasWithEnhancedWorkflow(string shaderName, List<Material> materials)
        {
            LogBuffered($"[AvatarOptimizer] --- GenerateAtlasWithEnhancedWorkflow START ({shaderName}) ---");
            PushLogIndent();

            var shader = materials[0].shader;
            var allowedProps = GetAllowedTexturePropertiesForShader(shader);

            if (allowedProps.Count == 0)
            {
                PopLogIndent();
                return 0;
            }

            var atlaseableMats = materials.Where(m => IsMaterialAtlaseable(m, allowedProps)).ToList();
            if (atlaseableMats.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
            {
                PopLogIndent();
                return 0;
            }

            int atlasesCreated = AtlasMaterialSubsetRecursive(shaderName, atlaseableMats, allowedProps, enhanced: true);

            PopLogIndent();
            return atlasesCreated;
        }

        private int GenerateAtlasWithTextureMerging(string shaderName, List<Material> materials)
        {
            var textureHash = new Dictionary<string, List<Material>>();

            foreach (var mat in materials)
            {
                var shader = mat.shader;
                int propertyCount = ShaderUtil.GetPropertyCount(shader);
                var textures = new List<Texture2D>();

                for (int i = 0; i < propertyCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        string propName = ShaderUtil.GetPropertyName(shader, i);
                        if (!ShouldIncludeTextureProperty(propName)) continue;

                        if (mat.HasProperty(propName))
                        {
                            var tex = mat.GetTexture(propName) as Texture2D;
                            if (tex != null)
                            {
                                textures.Add(tex);
                            }
                        }
                    }
                }

                string hash = string.Join("_", textures.Select(t => t.GetInstanceID()).OrderBy(id => id));

                if (!textureHash.ContainsKey(hash))
                {
                    textureHash[hash] = new List<Material>();
                }
                textureHash[hash].Add(mat);
            }

            return GenerateAtlasForShaderAutomatic(shaderName, materials);
        }

        private bool ShouldIncludeShader(Shader shader)
        {
            var settings = optimizer.atlasSettings;
            string shaderName = shader.name;

            if (!string.IsNullOrEmpty(settings.excludedShaderNames))
            {
                var excludePatterns = settings.excludedShaderNames.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in excludePatterns)
                {
                    if (shaderName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            if (!string.IsNullOrEmpty(settings.allowedShaderNames))
            {
                var allowPatterns = settings.allowedShaderNames.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (allowPatterns.Contains("*")) return true;

                return allowPatterns.Any(pattern =>
                    shaderName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }

        private bool ShouldIncludeTextureProperty(string propertyName)
        {
            var settings = optimizer.atlasSettings;

            if (!string.IsNullOrEmpty(settings.excludedTextureProperties))
            {
                var excludePatterns = settings.excludedTextureProperties.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in excludePatterns)
                {
                    if (pattern == "*" || MatchesWildcard(propertyName, pattern))
                    {
                        return false;
                    }
                }
            }

            if (!string.IsNullOrEmpty(settings.allowedTextureProperties))
            {
                var allowPatterns = settings.allowedTextureProperties.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (allowPatterns.Contains("*")) return true;

                return allowPatterns.Any(pattern => MatchesWildcard(propertyName, pattern));
            }

            return true;
        }

        private bool MatchesWildcard(string text, string pattern)
        {
            if (pattern == "*") return true;

            if (pattern.Contains("*"))
            {
                var regexPattern = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*");
                return System.Text.RegularExpressions.Regex.IsMatch(text, "^" + regexPattern + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private int GetBytesPerPixelForFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    return 4;
                case TextureFormat.RGB24:
                    return 3;
                case TextureFormat.RGBAHalf:
                    return 8;
                case TextureFormat.RGBAFloat:
                    return 16;
                case TextureFormat.RG16:
                case TextureFormat.RGBA4444:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGB565:
                    return 2;
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 1;
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                    return -1;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                    return 1;
                default:
                    return 4;
            }
        }

        private long CalculateTextureMemoryBytes(Texture2D texture)
        {
            if (texture == null) return 0;
            int bytesPerPixelFlag = GetBytesPerPixelForFormat(texture.format);
            long baseSize;

            if (bytesPerPixelFlag == -1)
                baseSize = ((long)texture.width * texture.height) / 2;
            else if (bytesPerPixelFlag > 0)
                baseSize = (long)texture.width * texture.height * bytesPerPixelFlag;
            else
                baseSize = (long)texture.width * texture.height * 4;

            if (texture.mipmapCount > 1)
            {
                baseSize = (long)(baseSize * 1.33f);
            }

            return baseSize;
        }

        private void TrackOriginalTextureMemory(Material mat)
        {
            if (mat == null || mat.shader == null) return;

            var shader = mat.shader;
            int propertyCount = ShaderUtil.GetPropertyCount(shader);

            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;

                string propName = ShaderUtil.GetPropertyName(shader, i);
                if (!ShouldIncludeTextureProperty(propName)) continue;

                if (!mat.HasProperty(propName)) continue;

                var tex = mat.GetTexture(propName) as Texture2D;
                if (tex != null && !originalTextureMemory.ContainsKey(tex))
                {
                    long memoryBytes = CalculateTextureMemoryBytes(tex);
                    originalTextureMemory[tex] = memoryBytes;
                }
            }
        }

        private string GenerateAtlasName(string shaderName, string propertyName, TextureDensityAnalysis? analysis, int width, int height)
        {
            var settings = optimizer.atlasSettings;
            var parts = new List<string>();

            parts.Add(!string.IsNullOrEmpty(settings.atlasNamePrefix) ? settings.atlasNamePrefix : "Atlas");

            if (settings.includeShaderInName && !string.IsNullOrEmpty(shaderName))
            {
                parts.Add(shaderName.Replace("/", "_").Replace(".", "_"));
            }

            if (settings.includePropertyInName && !string.IsNullOrEmpty(propertyName))
            {
                parts.Add(propertyName);
            }

            if (settings.includeTierInName && analysis.HasValue && analysis.Value.tier != null)
            {
                parts.Add(analysis.Value.tier.tierName);
            }

            parts.Add($"{width}x{height}");

            return string.Join("_", parts);
        }

        private void TrackAtlasMemory(Texture2D atlas)
        {
            if (atlas == null || accountedAtlases.Contains(atlas)) return;

            long atlasMemoryBytes = CalculateTextureMemoryBytes(atlas);
            totalAtlasMemoryBytes += atlasMemoryBytes;
            accountedAtlases.Add(atlas);
        }

        private int GenerateAtlasForShaderAutomatic(string shaderName, List<Material> materials)
        {
            LogBuffered($"[AvatarOptimizer] --- GenerateAtlasForShaderAutomatic START ({shaderName}) ---");
            PushLogIndent();

            var shader = materials[0].shader;
            var allowedProps = GetAllowedTexturePropertiesForShader(shader);

            if (allowedProps.Count == 0)
            {
                PopLogIndent();
                return 0;
            }

            var atlaseableMats = materials.Where(m => IsMaterialAtlaseable(m, allowedProps)).ToList();
            if (atlaseableMats.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
            {
                PopLogIndent();
                return 0;
            }

            int atlasesCreated = AtlasMaterialSubsetRecursive(shaderName, atlaseableMats, allowedProps, enhanced: false);

            PopLogIndent();
            return atlasesCreated;
        }

        private List<string> GetAllowedTexturePropertiesForShader(Shader shader)
        {
            int propertyCount = ShaderUtil.GetPropertyCount(shader);
            var allowedProps = new List<string>();

            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(shader, i);
                if (ShouldIncludeTextureProperty(propName))
                    allowedProps.Add(propName);
            }

            return allowedProps;
        }

        private bool IsMaterialAtlaseable(Material mat, List<string> allowedProps)
        {
            foreach (var prop in allowedProps)
            {
                if (!mat.HasProperty(prop)) continue;
                var tex = mat.GetTexture(prop);
                if (tex == null) continue;

                if (tex.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
                {
                    return false;
                }
            }
            return true;
        }

        private int AtlasMaterialSubsetRecursive(string shaderName, List<Material> mats, List<string> allowedProps, bool enhanced)
        {
            if (TryAtlasSubset(shaderName, mats, allowedProps, enhanced, out int created))
                return created;

            if (mats.Count <= 1)
                return 0;

            int mid = mats.Count / 2;
            var left = mats.Take(mid).ToList();
            var right = mats.Skip(mid).ToList();

            return AtlasMaterialSubsetRecursive(shaderName, left, allowedProps, enhanced)
                 + AtlasMaterialSubsetRecursive(shaderName, right, allowedProps, enhanced);
        }

        private Dictionary<string, List<string>> BuildPropertyGroups(List<Material> mats, List<string> allowedProps)
        {
            var groups = new Dictionary<string, List<string>>();

            foreach (var prop in allowedProps)
            {
                var keyParts = new List<int>(mats.Count);
                bool isNormal = IsNormalProperty(prop);

                foreach (var mat in mats)
                {
                    int id;
                    if (mat != null && mat.HasProperty(prop))
                    {
                        var t = mat.GetTexture(prop) as Texture2D;
                        id = t != null ? t.GetInstanceID() : (isNormal ? -1 : -2);
                    }
                    else
                    {
                        id = (isNormal ? -1 : -2);
                    }
                    keyParts.Add(id);
                }

                string key = string.Join("|", keyParts);

                if (!groups.ContainsKey(key))
                    groups[key] = new List<string>();

                groups[key].Add(prop);
            }

            return groups;
        }

        private bool IsNormalProperty(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return false;
            string lower = propName.ToLower();
            return lower.Contains("normal") || lower.Contains("bump") || lower == "_bumpmap";
        }

        private bool TryAtlasSubset(string shaderName, List<Material> mats, List<string> allowedProps, bool enhanced, out int atlasesCreated)
        {
            atlasesCreated = 0;

            var propGroups = BuildPropertyGroups(mats, allowedProps);
            var representativeProps = allowedProps
                .Where(p => propGroups.Values.Any(list => list[0] == p))
                .ToList();

            if (representativeProps.Count == 0) return false;

            var texturesByRepProp = new Dictionary<string, Texture2D[]>();

            foreach (var repProp in representativeProps)
            {
                var list = new List<Texture2D>(mats.Count);
                int maxWidth = optimizer.atlasSettings.minimumTextureSize;
                int maxHeight = optimizer.atlasSettings.minimumTextureSize;

                foreach (var mat in mats)
                {
                    if (mat.HasProperty(repProp))
                    {
                        var tex = mat.GetTexture(repProp) as Texture2D;
                        if (tex != null)
                        {
                            maxWidth = Mathf.Max(maxWidth, tex.width);
                            maxHeight = Mathf.Max(maxHeight, tex.height);
                        }
                    }
                }

                foreach (var mat in mats)
                {
                    Texture2D tex = null;
                    if (mat.HasProperty(repProp))
                    {
                        tex = mat.GetTexture(repProp) as Texture2D;
                    }

                    if (tex == null)
                    {
                        tex = CreatePlaceholderTexture(repProp, maxWidth, maxHeight);
                    }
                    else
                    {
                        if (optimizer.atlasSettings.preserveNormalMaps && IsNormalProperty(repProp))
                            tex = GetCachedTexture(tex, t => EnsureReadableNormal(EnsureSizeReadable(EnsureReadable(t), maxWidth, maxHeight)));
                        else
                            tex = GetCachedTexture(tex, t => EnsureSizeReadable(EnsureReadable(t), maxWidth, maxHeight));
                    }

                    list.Add(tex);
                }

                texturesByRepProp[repProp] = list.ToArray();
            }

            string driverProp = representativeProps[0];
            var driverTextures = texturesByRepProp[driverProp];
            int optimalPadding = CalculateAtlasPadding((int)optimizer.atlasSettings.maxAtlasSize, mats.Count);

            var driverRes = ManagedAtlasPacker.PackTextures(
                driverTextures,
                (int)optimizer.atlasSettings.maxAtlasSize,
                optimalPadding,
                true);

            if (driverRes == null || driverRes.atlas == null) return false;

            TrackAtlasMemory(driverRes.atlas);
            int atlasW = driverRes.width;
            int atlasH = driverRes.height;

            var atlasesByRep = new Dictionary<string, (Texture2D atlas, Rect[] rects)>();
            var driverAnalysis = AnalyzeTextureDensity(driverRes.atlas, driverProp);
            driverRes.atlas.name = GenerateAtlasName(shaderName, driverProp, driverAnalysis, atlasW, atlasH);
            atlasesByRep[driverProp] = (driverRes.atlas, driverRes.uvRects);

            foreach (var repProp in representativeProps)
            {
                if (repProp == driverProp) continue;

                if (enhanced)
                {
                    var fixedAtlas = BuildAtlasFromFixedLayout(texturesByRepProp[repProp], driverRes.uvRects, atlasW, atlasH, shaderName, repProp, null);
                    if (fixedAtlas == null) return false;
                    var analysis = AnalyzeTextureDensity(fixedAtlas, repProp);
                    atlasesByRep[repProp] = (fixedAtlas, driverRes.uvRects);
                }
                else
                {
                    var res = ManagedAtlasPacker.PackTextures(
                        texturesByRepProp[repProp],
                        (int)optimizer.atlasSettings.maxAtlasSize,
                        optimalPadding,
                        true);

                    if (res == null || res.atlas == null) return false;
                    TrackAtlasMemory(res.atlas);
                    var analysis = AnalyzeTextureDensity(res.atlas, repProp);
                    res.atlas.name = GenerateAtlasName(shaderName, repProp, analysis, res.width, res.height);
                    atlasesByRep[repProp] = (res.atlas, res.uvRects);
                }
            }

            var atlasesByProp = new Dictionary<string, (Texture2D atlas, Rect[] rects)>();
            foreach (var g in propGroups)
            {
                string repProp = g.Value[0];
                var repAtlas = atlasesByRep[repProp];
                foreach (var prop in g.Value)
                {
                    atlasesByProp[prop] = repAtlas;
                }
            }

            var rectByMaterial = new Dictionary<Material, Rect>();
            for (int i = 0; i < mats.Count; i++)
            {
                var originalMat = mats[i];
                var matCopy = enhanced ? null : GetOrCreateMaterialCopy(originalMat);

                foreach (var kvp in atlasesByProp)
                {
                    string prop = kvp.Key;
                    var atlas = kvp.Value.atlas;
                    var rect = kvp.Value.rects[i];

                    if (!enhanced)
                    {
                        if (matCopy.HasProperty(prop))
                            matCopy.SetTexture(prop, atlas);

                        string stProp = prop + "_ST";
                        Vector4 originalST = originalMat.HasProperty(stProp)
                            ? originalMat.GetVector(stProp)
                            : new Vector4(1, 1, 0, 0);

                        Vector4 newST = new Vector4(
                            originalST.x * rect.width,
                            originalST.y * rect.height,
                            originalST.z * rect.width + rect.x,
                            originalST.w * rect.height + rect.y
                        );

                        if (matCopy.HasProperty(stProp))
                            matCopy.SetVector(stProp, newST);
                    }
                }
                rectByMaterial[originalMat] = driverRes.uvRects[i];
            }

            if (enhanced)
            {
                var masterMat = new Material(mats[0]);
                masterMat.name = mats[0].name + "_MASTER_Atlased";

                foreach (var kvp in atlasesByProp)
                {
                    string prop = kvp.Key;
                    var atlas = kvp.Value.atlas;
                    if (masterMat.HasProperty(prop))
                        masterMat.SetTexture(prop, atlas);

                    string stProp = prop + "_ST";
                    if (masterMat.HasProperty(stProp))
                        masterMat.SetVector(stProp, new Vector4(1, 1, 0, 0));
                }

                ApplyEnhancedBakingAndReplace(masterMat, mats, rectByMaterial);
            }

            atlasesCreated = atlasesByRep.Count;
            return true;
        }

        private void ApplyEnhancedBakingAndReplace(Material masterMat, List<Material> subset, Dictionary<Material, Rect> rectByMaterial)
        {
            var renderers = context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                var slots = new List<int>();
                var rects = new List<Rect>();

                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (!subset.Contains(m)) continue;

                    if (rectByMaterial.TryGetValue(m, out var r))
                    {
                        slots.Add(i);
                        rects.Add(r);
                    }
                }

                if (slots.Count == 0) continue;

                if (renderer is SkinnedMeshRenderer smr)
                {
                    for (int s = 0; s < slots.Count; s++)
                    {
                        BakeSubmeshUVsToRect(smr, slots[s], rects[s]);
                    }
                }
                else if (renderer is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        BakeMeshUVsToRect(mf, rects[0]);
                    }
                }

                for (int s = 0; s < slots.Count; s++)
                {
                    mats[slots[s]] = masterMat;
                }

                renderer.sharedMaterials = mats;
            }
        }

        private Texture2D EnsureReadable(Texture2D tex)
        {
            if (tex == null) return null;
            try { if (tex.isReadable) return tex; } catch { }

            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, tex.mipmapCount > 1);
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            copy.name = tex.name + "_ReadableCopy";
            return copy;
        }

        private Texture2D EnsureSizeReadable(Texture2D tex, int width, int height)
        {
            tex = EnsureReadable(tex);
            if (tex == null) return null;
            if (tex.width == width && tex.height == height) return tex;

            width = Mathf.Max(2, width);
            height = Mathf.Max(2, height);

            var scaled = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            scaled.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            scaled.name = tex.name + $"_Scaled_{width}x{height}";
            return scaled;
        }

        private Texture2D CreatePlaceholderTexture(string propName, int width, int height)
        {
            bool isNormal = IsNormalProperty(propName);
            width = Mathf.Max(2, width);
            height = Mathf.Max(2, height);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.name = isNormal ? $"AO_NormalPlaceholder_{width}x{height}" : $"AO_WhitePlaceholder_{width}x{height}";

            Color c = isNormal ? new Color(0.5f, 0.5f, 1f, 1f) : Color.white;
            var pixels = Enumerable.Repeat(c, width * height).ToArray();
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private int CalculateAtlasPadding(int atlasSize, int textureCount)
        {
            int basePadding = atlasSize <= 1024 ? 2 : (atlasSize <= 2048 ? 4 : 8);
            return Mathf.Clamp(basePadding, 1, 16);
        }

        private string GetTextureFingerprint(Texture2D tex)
        {
            if (tex == null) return "null";
            if (textureFingerprintCache.TryGetValue(tex, out string cached)) return cached;
            var sb = new StringBuilder();
            sb.Append($"{tex.width}x{tex.height}_{tex.format}_");
            var readable = EnsureReadable(tex);
            if (readable != null)
            {
                int sx = Mathf.Max(1, readable.width / 8);
                int sy = Mathf.Max(1, readable.height / 8);
                for (int y = 0; y < readable.height; y += sy)
                {
                    for (int x = 0; x < readable.width; x += sx)
                    {
                        var c = readable.GetPixel(x, y);
                        sb.Append($"{c.r:F2}{c.g:F2}{c.b:F2}_");
                    }
                }
            }
            else
            {
                sb.Append(tex.GetInstanceID());
            }
            string fp = sb.ToString();
            textureFingerprintCache[tex] = fp;
            return fp;
        }

        private Texture2D GetCachedTexture(Texture2D original, Func<Texture2D, Texture2D> processor)
        {
            if (original == null) return null;
            string fp = GetTextureFingerprint(original);
            if (textureCache.TryGetValue(fp, out var cached)) return cached;
            var processed = processor(original);
            textureCache[fp] = processed;
            return processed;
        }

        private Texture2D EnsureReadableNormal(Texture2D tex)
        {
            var readable = EnsureReadable(tex);
            if (readable == null) return null;
            var pixels = readable.GetPixels();
            bool needsConversion = false;
            for (int i = 0; i < Mathf.Min(64, pixels.Length); i++)
            {
                if (pixels[i].b < 0.5f)
                {
                    needsConversion = true; break;
                }
            }
            if (needsConversion)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    var n = new Vector3(pixels[i].r * 2f - 1f, pixels[i].g * 2f - 1f, pixels[i].b * 2f - 1f).normalized;
                    if (n.z < 0) n.z = -n.z;
                    pixels[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, pixels[i].a);
                }
                readable.SetPixels(pixels);
                readable.Apply(true);
            }
            return readable;
        }

        private void OptimizeIndexBuffer(Mesh mesh)
        {
            if (!optimizer.meshSettings.optimizeIndexBuffer || mesh == null) return;
            try { mesh.Optimize(); } catch { }
        }

        private void MergeIdenticalSubmeshes(Mesh mesh, Material[] materials)
        {
            if (mesh == null || materials == null || materials.Length <= 1) return;
            var groups = new Dictionary<Material, List<int>>();
            int count = Mathf.Min(materials.Length, mesh.subMeshCount);
            for (int i = 0; i < count; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;
                if (!groups.ContainsKey(mat)) groups[mat] = new List<int>();
                groups[mat].Add(i);
            }
            if (groups.Count == count) return;

            var newTris = new List<int[]>();
            var newMats = new List<Material>();
            foreach (var kv in groups)
            {
                newMats.Add(kv.Key);
                if (kv.Value.Count == 1)
                {
                    newTris.Add(mesh.GetTriangles(kv.Value[0]));
                }
                else
                {
                    var combined = kv.Value.SelectMany(idx => mesh.GetTriangles(idx)).ToArray();
                    newTris.Add(combined);
                }
            }
            mesh.subMeshCount = newTris.Count;
            for (int i = 0; i < newTris.Count; i++)
            {
                mesh.SetTriangles(newTris[i], i);
            }
        }

        private void StripUnusedVertexAttributesIntelligent(Mesh mesh, Material[] materials)
        {
            if (mesh == null || materials == null || !optimizer.meshSettings.stripUnusedAttributes) return;
            bool needTangents = false, needColors = false, needUV2 = false;
            foreach (var mat in materials.Where(m => m != null && m.shader != null))
            {
                var sh = mat.shader;
                int pc = ShaderUtil.GetPropertyCount(sh);
                for (int i = 0; i < pc; i++)
                {
                    if (ShaderUtil.GetPropertyType(sh, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        string pname = ShaderUtil.GetPropertyName(sh, i).ToLower();
                        if (pname.Contains("normal") || pname.Contains("bump")) needTangents = true;
                        if (pname.Contains("_detail")) needUV2 = true;
                    }
                    else if (ShaderUtil.GetPropertyType(sh, i) == ShaderUtil.ShaderPropertyType.Color)
                    {
                        needColors = true;
                    }
                }
            }

            if (!needTangents && mesh.tangents != null && mesh.tangents.Length > 0) mesh.tangents = null;
            if (!needColors && mesh.colors != null && mesh.colors.Length > 0) mesh.colors = null;
            try { if (!needUV2) mesh.uv2 = null; } catch { }
        }

        #endregion

        #region Texture Information Density Analysis

        private struct TextureDensityAnalysis
        {
            public float complexityScore;
            public AvatarOptimizer.CompressionTier tier;
            public int uniqueColorCount;
            public float edgeDensity;
            public float colorVariance;
            public string analysisReason;
        }

        private TextureDensityAnalysis AnalyzeTextureDensity(Texture2D texture, string propertyName)
        {
            if (texture == null)
            {
                return new TextureDensityAnalysis { complexityScore = 0f, tier = null, analysisReason = "Null texture" };
            }

            var analysis = new TextureDensityAnalysis();
            var readable = EnsureReadable(texture);
            if (readable == null)
            {
                analysis.complexityScore = 0.5f;
                analysis.tier = GetCompressionTierForScore(0.5f, propertyName);
                analysis.analysisReason = "Unreadable texture";
                return analysis;
            }

            int stride = Mathf.Max(1, readable.width / 256);
            var sampledPixels = new List<Color32>();

            for (int y = 0; y < readable.height; y += stride)
            {
                for (int x = 0; x < readable.width; x += stride)
                {
                    sampledPixels.Add(readable.GetPixel(x, y));
                }
            }

            var uniqueColors = new HashSet<Color32>(sampledPixels).Count;
            analysis.uniqueColorCount = uniqueColors;
            float colorDiversity = Mathf.Clamp01(uniqueColors / 256f);

            var settings = optimizer.atlasSettings;
            float baseScore = colorDiversity * settings.colorDiversityWeight;

            analysis.complexityScore = Mathf.Clamp01(baseScore);
            analysis.tier = GetCompressionTierForScore(analysis.complexityScore, propertyName);
            analysis.analysisReason = analysis.tier != null ? analysis.tier.tierName : "Default";

            return analysis;
        }

        private AvatarOptimizer.CompressionTier GetCompressionTierForScore(float score, string propertyName)
        {
            var tiers = optimizer.atlasSettings.compressionTiers
                .Where(t => t.enableTier)
                .OrderBy(t => t.minComplexity)
                .ToList();

            foreach (var tier in tiers)
            {
                if (score < tier.minComplexity || score > tier.maxComplexity) continue;
                return tier;
            }

            return tiers.Count > 0 ? tiers[tiers.Count / 2] : null;
        }

        private void ApplyDensityBasedImportSettings(Texture2D texture, string texturePath, TextureDensityAnalysis analysis, string propertyName)
        {
            if (string.IsNullOrEmpty(texturePath) || analysis.tier == null) return;

            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) return;

            importer.maxTextureSize = analysis.tier.maxTextureSize;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private void ApplyPendingAtlasImportSettings()
        {
            var applied = new List<Texture2D>();
            foreach (var kvp in atlasImportSettings)
            {
                var tex = kvp.Key;
                var analysis = kvp.Value;
                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    string texName = Path.GetFileNameWithoutExtension(path);
                    ApplyDensityBasedImportSettings(tex, path, analysis, texName);
                    applied.Add(tex);
                }
            }

            foreach (var t in applied)
                atlasImportSettings.Remove(t);
        }

        private void ApplyStandaloneAdaptiveCompression()
        {
            var processedTextures = new Dictionary<Texture2D, Texture2D>();
            var skippedLarger = 0;

            foreach (var material in MaterialAnimationUtility.GetReferencedMaterials(context.AvatarRootTransform))
            {
                if (material == null || material.shader == null) continue;

                Material materialCopy = null;
                foreach (var property in GetAllowedTexturePropertiesForShader(material.shader))
                {
                    if (!material.HasProperty(property)) continue;

                    var sourceTexture = material.GetTexture(property) as Texture2D;
                    if (sourceTexture == null || accountedAtlases.Contains(sourceTexture)) continue;

                    if (!processedTextures.TryGetValue(sourceTexture, out var compressedTexture))
                    {
                        compressedTexture = CreateStandaloneCompressedTexture(sourceTexture, property, ref skippedLarger);
                        processedTextures[sourceTexture] = compressedTexture;
                    }

                    if (compressedTexture == null) continue;

                    materialCopy ??= GetOrCreateMaterialCopy(material);
                    materialCopy.SetTexture(property, compressedTexture);
                }

            }

            foreach (var renderer in context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = renderer.sharedMaterials.Select(material =>
                    material != null && materialCopies.TryGetValue(material, out var copy) ? copy : material).ToArray();

            ProcessAndReplaceControllerClips(source =>
            {
                AnimationClip copy = null;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(source, binding);
                    bool changed = false;
                    for (int index = 0; index < keys.Length; index++)
                    {
                        if (keys[index].value is Material material && materialCopies.TryGetValue(material, out var replacement))
                        {
                            keys[index].value = replacement;
                            changed = true;
                        }
                    }
                    if (!changed) continue;
                    if (copy == null)
                    {
                        copy = UnityEngine.Object.Instantiate(source);
                        copy.name = source.name + "_Materials";
                    }
                    AnimationUtility.SetObjectReferenceCurve(copy, binding, keys);
                }
                return copy;
            }, "_Materials");
        }

        private Texture2D CreateStandaloneCompressedTexture(Texture2D sourceTexture, string propertyName, ref int skippedLarger)
        {
            var analysis = AnalyzeTextureDensity(sourceTexture, propertyName);
            if (analysis.tier == null) return null;

            var compressed = EnsureSizeReadable(sourceTexture, sourceTexture.width, sourceTexture.height);
            if (compressed == null) return null;

            compressed.name = sourceTexture.name + "_AdaptiveCopy";
            try
            {
                compressed.Compress(analysis.tier.crunchQuality >= 70);
                compressed.Apply(true, false);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(compressed);
                return null;
            }

            return compressed;
        }

        #endregion

        private Texture2D BuildAtlasFromFixedLayout(Texture2D[] textures, Rect[] rects, int atlasWidth, int atlasHeight, string shaderName, string propertyName, TextureDensityAnalysis? analysis)
        {
            if (textures == null || rects == null || textures.Length != rects.Length) return null;

            var atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
            var clearPixels = Enumerable.Repeat(new Color32(0, 0, 0, 0), atlasWidth * atlasHeight).ToArray();
            atlas.SetPixels32(clearPixels);

            for (int i = 0; i < textures.Length; i++)
            {
                var tex = EnsureReadable(textures[i]);
                if (tex == null) continue;

                var r = rects[i];
                int x = Mathf.RoundToInt(r.x * atlasWidth);
                int y = Mathf.RoundToInt(r.y * atlasHeight);
                int w = Mathf.RoundToInt(r.width * atlasWidth);
                int h = Mathf.RoundToInt(r.height * atlasHeight);
                if (w <= 0 || h <= 0) continue;

                var scaled = EnsureSizeReadable(tex, w, h);
                atlas.SetPixels(x, y, w, h, scaled.GetPixels());
            }

            atlas.Apply();
            atlas.name = GenerateAtlasName(shaderName, propertyName, analysis, atlasWidth, atlasHeight);
            TrackAtlasMemory(atlas);
            return atlas;
        }

        private void BakeSubmeshUVsToRect(SkinnedMeshRenderer smr, int materialSlot, Rect rect)
        {
            var mesh = GetOrCreateMeshCopy(smr, "_UVBaked");
            if (mesh == null) return;

            int subMeshCount = mesh.subMeshCount;
            if (materialSlot < 0 || materialSlot >= subMeshCount) return;

            var uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0) return;

            var triangles = mesh.GetTriangles(materialSlot);
            var used = new HashSet<int>(triangles);

            for (int i = 0; i < uvs.Length; i++)
            {
                if (!used.Contains(i)) continue;
                Vector2 uv = uvs[i];
                uv.x = rect.x + (uv.x * rect.width);
                uv.y = rect.y + (uv.y * rect.height);
                uvs[i] = uv;
            }

            mesh.uv = uvs;
        }

        private void BakeMeshUVsToRect(MeshFilter mf, Rect rect)
        {
            if (mf.sharedMesh == null) return;
            var mesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
            mesh.name = mf.sharedMesh.name + "_UVBaked";
            mf.sharedMesh = mesh;

            var uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0) return;

            for (int i = 0; i < uvs.Length; i++)
            {
                Vector2 uv = uvs[i];
                uv.x = rect.x + uv.x * rect.width;
                uv.y = rect.y + uv.y * rect.height;
                uvs[i] = uv;
            }

            mesh.uv = uvs;
        }

        private void CollectAnimatedMaterials()
        {
            LogBuffered("[AvatarOptimizer] Collecting animated materials...");
            PushLogIndent();

            foreach (var pair in MaterialAnimationUtility.CollectAnimatedMaterialProperties(context.AvatarRootTransform, avatar))
            {
                animatedMaterials.Add(pair.Key);
                animatedMaterialProperties[pair.Key] = pair.Value;
            }

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] Found {animatedMaterials.Count} animated materials");
        }

        private IEnumerable<RuntimeAnimatorController> GetAvatarAnimatorControllers(bool rootOnly = false)
        {
            var controllers = new HashSet<RuntimeAnimatorController>();

            // 1. Scan all CVRAvatar components across the hierarchy
            if (cvrAvatarType != null && context != null && context.AvatarRootTransform != null)
            {
                var cvrComponents = context.AvatarRootTransform.GetComponentsInChildren(cvrAvatarType, true);
                foreach (var cvrComp in cvrComponents)
                {
                    if (cvrComp == null || (rootOnly && cvrComp.transform != context.AvatarRootTransform)) continue;
                    var rawObj = GetFieldValue(cvrComp, "overrides") ?? GetPropertyValue(cvrComp, "overrides");
                    if (rawObj is RuntimeAnimatorController rac && rac != null)
                    {
                        controllers.Add(rac);
                    }
                    var settings = GetFieldValue(cvrComp, "avatarSettings") ?? GetPropertyValue(cvrComp, "avatarSettings");
                    if (settings != null)
                        foreach (var field in settings.GetType().GetFields())
                            if ((!rootOnly || (field.Name != "baseController" && field.Name != "baseOverrideController")) && field.GetValue(settings) is RuntimeAnimatorController authoringController && authoringController != null)
                                controllers.Add(authoringController);
                }
            }

            // 2. Scan all standard Animator components in the hierarchy
            if (context != null && context.AvatarRootTransform != null)
            {
                var animators = context.AvatarRootTransform.GetComponentsInChildren<Animator>(true);
                foreach (var anim in animators)
                {
                    if (anim != null && anim.runtimeAnimatorController != null && (!rootOnly || anim.transform == context.AvatarRootTransform))
                    {
                        controllers.Add(anim.runtimeAnimatorController);
                    }
                }
            }

            return controllers;
        }

        private string ExtractPropertyName(string bindingPropertyName)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                bindingPropertyName, @"(?:material\.|Array\.data\[\d+\]\.)(_[^\.]+)");

            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        private bool ShouldExcludeMaterialFromAtlas(Material material)
        {
            // Swaps need a common UV layout for every alternative before they can be atlased safely.
            if (animatedMaterialProperties.TryGetValue(material, out var swapProperties) &&
                swapProperties.Contains(MaterialAnimationUtility.MaterialReferenceProperty)) return true;
            var settings = optimizer.atlasSettings;

            if (settings.excludeAnimatedMaterials && animatedMaterialProperties.TryGetValue(material, out var properties) &&
                properties.Contains(MaterialAnimationUtility.MaterialReferenceProperty))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(settings.excludeMaterialPatterns))
            {
                var patterns = settings.excludeMaterialPatterns.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p));

                foreach (var pattern in patterns)
                {
                    if (material.name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private const string NotAnimatedSignature = "\u0002__static__";

        private string GetAnimatedPropertySignature(Material material)
        {
            if (!optimizer.atlasSettings.excludeAnimatedMaterials)
            {
                return string.Empty;
            }

            if (animatedMaterialProperties.TryGetValue(material, out var props) && props.Count > 0)
            {
                return string.Join(",", props.OrderBy(p => p, StringComparer.Ordinal));
            }

            return NotAnimatedSignature;
        }

        private string DescribeAnimatedSignature(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return "animation state ignored";
            if (signature == NotAnimatedSignature) return "not animated";
            return $"animated: {signature}";
        }

        #region MagicaCloth Rebuilding

        private void RebuildMagicaClothData(Transform avatarRoot)
        {
#if CCKADDINMAGICACLOTHSUPPORT
            if (avatarRoot == null) return;

            var renderDeformers = avatarRoot.GetComponentsInChildren<MagicaRenderDeformer>(true).ToList();
            foreach (var rd in renderDeformers)
            {
                if (rd == null) continue;
                try { BuildManager.CreateComponent(rd); EditorUtility.SetDirty(rd); } catch { }
            }

            var virtualDeformers = avatarRoot.GetComponentsInChildren<MagicaVirtualDeformer>(true).ToList();
            foreach (var vd in virtualDeformers)
            {
                if (vd == null) continue;
                try { BuildManager.CreateComponent(vd); EditorUtility.SetDirty(vd); } catch { }
            }

            var boneCloths = avatarRoot.GetComponentsInChildren<MagicaBoneCloth>(true).ToList();
            foreach (var bc in boneCloths)
            {
                if (bc == null) continue;
                try { BuildManager.CreateComponent(bc); EditorUtility.SetDirty(bc); } catch { }
            }

            var meshCloths = avatarRoot.GetComponentsInChildren<MagicaMeshCloth>(true).ToList();
            foreach (var mc in meshCloths)
            {
                if (mc == null) continue;
                try { BuildManager.CreateComponent(mc); EditorUtility.SetDirty(mc); } catch { }
            }
#else
            LogBuffered("MagicaCloth support is not enabled (CCKADDINMAGICACLOTHSUPPORT not defined).");
#endif
        }

        #endregion
    }

    #endregion

    #region Texture atlas packing

    public static class ManagedAtlasPacker
    {
        public class AtlasResult
        {
            public Texture2D atlas;
            public Rect[] uvRects;
            public int width;
            public int height;
        }

        // Shared placeholders
        private static Texture2D _transparentPlaceholder;
        private static Texture2D _neutralNormalPlaceholder;

        public static Texture2D TransparentPlaceholder
        {
            get
            {
                if (_transparentPlaceholder == null)
                {
                    _transparentPlaceholder = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    var cols = new Color[]
                    {
                        new Color(0,0,0,0), new Color(0,0,0,0),
                        new Color(0,0,0,0), new Color(0,0,0,0)
                    };
                    _transparentPlaceholder.SetPixels(cols);
                    _transparentPlaceholder.Apply();
                    _transparentPlaceholder.name = "AO_TransparentPlaceholder";
                }
                return _transparentPlaceholder;
            }
        }

        public static Texture2D NeutralNormalPlaceholder
        {
            get
            {
                if (_neutralNormalPlaceholder == null)
                {
                    _neutralNormalPlaceholder = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    var cols = new Color[]
                    {
                        new Color(0.5f,0.5f,1f,1f), new Color(0.5f,0.5f,1f,1f),
                        new Color(0.5f,0.5f,1f,1f), new Color(0.5f,0.5f,1f,1f)
                    };
                    _neutralNormalPlaceholder.SetPixels(cols);
                    _neutralNormalPlaceholder.Apply();
                    _neutralNormalPlaceholder.name = "AO_NeutralNormalPlaceholder";
                }
                return _neutralNormalPlaceholder;
            }
        }

        public static AtlasResult PackTextures(Texture2D[] textures, int maxSize, int padding, bool allowResize)
        {
            if (textures == null || textures.Length == 0)
                return null;

            // Sanitize input
            for (int i = 0; i < textures.Length; i++)
                textures[i] = textures[i] ?? TransparentPlaceholder;

            try
            {
                var atlas = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                Rect[] rects = atlas.PackTextures(textures, padding, maxSize, false);

                if (rects == null || rects.Length == 0)
                    throw new Exception("PackTextures returned empty rects");

                return new AtlasResult
                {
                    atlas = atlas,
                    uvRects = rects,
                    width = atlas.width,
                    height = atlas.height
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ManagedAtlasPacker] Failed to pack textures. Dumping failing set:");
                DumpTextureSet(textures, maxSize, padding);

                if (!allowResize)
                {
                    Debug.LogError("[ManagedAtlasPacker] Pack failed and resizing disabled: " + ex.Message);
                    return null;
                }

                Debug.LogWarning("[ManagedAtlasPacker] Failed to pack textures, trying with reduced sizes");

                // Reduce sizes progressively
                var reduced = new Texture2D[textures.Length];
                for (int i = 0; i < textures.Length; i++)
                    reduced[i] = ResizeToFit(textures[i], maxSize);

                try
                {
                    var atlas = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    Rect[] rects = atlas.PackTextures(reduced, padding, maxSize, false);

                    if (rects == null || rects.Length == 0)
                        throw new Exception("Reduced PackTextures returned empty rects");

                    return new AtlasResult
                    {
                        atlas = atlas,
                        uvRects = rects,
                        width = atlas.width,
                        height = atlas.height
                    };
                }
                catch (Exception ex2)
                {
                    Debug.LogError("[ManagedAtlasPacker] Reduced pack still failed. Dumping reduced set:");
                    DumpTextureSet(reduced, maxSize, padding);
                    Debug.LogError("[ManagedAtlasPacker] Final failure: " + ex2.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Builds an atlas using precomputed rects (same UV layout).
        /// </summary>
        public static Texture2D BuildAtlasUsingRects(Texture2D[] textures, Rect[] rects, int atlasWidth, int atlasHeight)
        {
            if (textures == null || rects == null || textures.Length != rects.Length)
                return null;

            Texture2D atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
            var clear = new Color32[atlasWidth * atlasHeight];
            atlas.SetPixels32(clear);

            for (int i = 0; i < textures.Length; i++)
            {
                var tex = textures[i] ?? TransparentPlaceholder;
                Rect r = rects[i];

                int x = Mathf.RoundToInt(r.x * atlasWidth);
                int y = Mathf.RoundToInt(r.y * atlasHeight);
                int w = Mathf.RoundToInt(r.width * atlasWidth);
                int h = Mathf.RoundToInt(r.height * atlasHeight);

                if (w <= 0 || h <= 0) continue;

                var scaled = Resize(tex, w, h);
                atlas.SetPixels(x, y, w, h, scaled.GetPixels());
            }

            atlas.Apply();
            return atlas;
        }

        private static void DumpTextureSet(Texture2D[] textures, int maxSize, int padding)
        {
            Debug.Log($"[ManagedAtlasPacker] Pack params: maxSize={maxSize}, padding={padding}, count={textures.Length}");

            for (int i = 0; i < textures.Length; i++)
            {
                var t = textures[i];
                if (t == null)
                {
                    Debug.Log($"[ManagedAtlasPacker]  [{i}] NULL texture");
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(t);
                Debug.Log($"[ManagedAtlasPacker]  [{i}] '{t.name}' {t.width}x{t.height} fmt={t.format} mip={t.mipmapCount > 1} path='{path}'");
            }
        }

        private static Texture2D ResizeToFit(Texture2D src, int maxSize)
        {
            if (src == null) return TransparentPlaceholder;

            int w = src.width;
            int h = src.height;

            if (w <= maxSize && h <= maxSize)
                return src;

            float scale = Mathf.Min((float)maxSize / w, (float)maxSize / h);
            int nw = Mathf.Max(2, Mathf.RoundToInt(w * scale));
            int nh = Mathf.Max(2, Mathf.RoundToInt(h * scale));

            return Resize(src, nw, nh);
        }

        private static Texture2D Resize(Texture2D src, int w, int h)
        {
            if (src == null) return TransparentPlaceholder;

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            dst.name = src.name + $"_Resized_{w}x{h}";
            return dst;
        }
    }

    #endregion

    #region Shared material animation analysis

    /// <summary>
    /// Shared helper for discovering which material properties are driven by animation clips
    /// on a CVR avatar's animator controllers. Used by both the runtime optimization pass and
    /// the editor-time pre-optimization estimate so their "animated material" detection - and
    /// therefore their atlas grouping - stays consistent.
    /// </summary>
    internal static class MaterialAnimationUtility
    {
        public const string MaterialReferenceProperty = "__material_reference__";

        private static Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private static object GetFieldValue(object obj, string fieldName)
        {
            if (obj == null) return null;
            var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        private static object GetPropertyValue(object obj, string propertyName)
        {
            if (obj == null) return null;
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.GetValue(obj);
        }

        private static T GetFieldOrPropertyValue<T>(object obj, string name, T defaultValue = default(T))
        {
            var value = GetFieldValue(obj, name) ?? GetPropertyValue(obj, name);
            return value != null ? (T)value : defaultValue;
        }

        /// <summary>
        /// Finds the CVRAvatar component (if any) on the given avatar root.
        /// </summary>
        public static Component FindCvrAvatarComponent(Transform avatarRoot)
        {
            if (avatarRoot == null) return null;
            var cvrAvatarType = FindType("ABI.CCK.Components.CVRAvatar");
            return cvrAvatarType == null ? null : avatarRoot.GetComponent(cvrAvatarType);
        }

        private static string ExtractPropertyName(string bindingPropertyName)
        {
            var match = Regex.Match(bindingPropertyName, @"(?:material\.|Array\.data\[\d+\]\.)(_[^\.]+)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        /// <summary>
        /// Scans the avatar's animator controllers for material property curves and returns,
        /// for each animated material, the set of property names driven by animation.
        /// </summary>
        private static IEnumerable<AnimationClip> GetAvatarClips(Transform root)
        {
            var controllers = new HashSet<RuntimeAnimatorController>();
            var avatarType = FindType("ABI.CCK.Components.CVRAvatar");
            if (avatarType != null)
                foreach (var component in root.GetComponentsInChildren(avatarType, true))
                {
                    var controller = GetFieldOrPropertyValue<RuntimeAnimatorController>(component, "overrides");
                    if (controller != null) controllers.Add(controller);
                }
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null) controllers.Add(animator.runtimeAnimatorController);
            return controllers.SelectMany(controller => controller.animationClips).Where(clip => clip != null).Distinct();
        }

        private static Renderer ResolveRenderer(Transform root, EditorCurveBinding binding)
        {
            if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type)) return null;
            var target = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
            return target != null ? target.GetComponent(binding.type) as Renderer : null;
        }

        public static bool HasMaterialSwaps(Transform root, Renderer renderer)
        {
            return GetAvatarClips(root).Any(clip => AnimationUtility.GetObjectReferenceCurveBindings(clip)
                .Any(binding => binding.propertyName.Contains("m_Materials") && ResolveRenderer(root, binding) == renderer));
        }

        public static HashSet<Material> GetReferencedMaterials(Transform root, Renderer renderer = null)
        {
            var materials = new HashSet<Material>();
            if (root == null) return materials;
            var renderers = renderer != null ? new[] { renderer } : root.GetComponentsInChildren<Renderer>(true);
            foreach (var target in renderers) materials.UnionWith(target.sharedMaterials.Where(material => material != null));
            foreach (var clip in GetAvatarClips(root))
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!binding.propertyName.Contains("m_Materials")) continue;
                    var target = ResolveRenderer(root, binding);
                    if (target == null || (renderer != null && target != renderer)) continue;
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        if (key.value is Material material) materials.Add(material);
                }
            return materials;
        }

        public static Dictionary<Material, List<string>> CollectAnimatedMaterialProperties(Transform avatarRoot, Component avatar)
        {
            var result = new Dictionary<Material, List<string>>();
            if (avatarRoot == null) return result;
            // Build slot alternatives across ALL clips: a swap and a property curve may live in different states.
            var slots = new Dictionary<Renderer, Dictionary<int, HashSet<Material>>>();
            var clips = GetAvatarClips(avatarRoot).ToList();
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                slots[renderer] = new Dictionary<int, HashSet<Material>>();
                for (int index = 0; index < renderer.sharedMaterials.Length; index++)
                    slots[renderer][index] = new HashSet<Material>(new[] { renderer.sharedMaterials[index] }.Where(m => m != null));
            }
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!binding.propertyName.Contains("m_Materials")) continue;
                    var renderer = ResolveRenderer(avatarRoot, binding);
                    if (renderer == null) continue;
                    var match = Regex.Match(binding.propertyName, @"Array\.data\[(\d+)\]");
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index)) continue;
                    if (!slots[renderer].TryGetValue(index, out var materials))
                        slots[renderer][index] = materials = new HashSet<Material>();
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        if (key.value is Material material) materials.Add(material);
                    foreach (var material in materials) AddProperty(result, material, MaterialReferenceProperty);
                }
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var property = ExtractPropertyName(binding.propertyName);
                    if (string.IsNullOrEmpty(property)) continue;
                    var renderer = ResolveRenderer(avatarRoot, binding);
                    if (renderer == null) continue;
                    var match = Regex.Match(binding.propertyName, @"Array\.data\[(\d+)\]");
                    // Unindexed renderer material properties can affect every slot.
                    var materials = match.Success && int.TryParse(match.Groups[1].Value, out var index)
                        ? (slots[renderer].TryGetValue(index, out var slot) ? slot : Enumerable.Empty<Material>())
                        : slots[renderer].Values.SelectMany(slot => slot);
                    foreach (var material in materials) AddProperty(result, material, property);
                }
            return result;
        }

        private static void AddProperty(Dictionary<Material, List<string>> result, Material material, string property)
        {
            if (!result.TryGetValue(material, out var properties)) result[material] = properties = new List<string>();
            if (!properties.Contains(property)) properties.Add(property);
        }
    }

    #endregion

    // Shared safety checks and opt-in passes. Kept with the build plugin to retain the two-file Editor layout.
    internal static class GuardedAvatarPasses
    {
        private sealed class MergeAnalysis : IDisposable
        {
            internal AvatarOptimizer owner;
            internal ClipUse[] clips;
            internal readonly Dictionary<SkinnedMeshRenderer, string> eligibility = new Dictionary<SkinnedMeshRenderer, string>();
            internal readonly Dictionary<(AnimationClip, EditorCurveBinding), string> curves = new Dictionary<(AnimationClip, EditorCurveBinding), string>();
            internal MergeAnalysis previous;
            public void Dispose() { mergeAnalysis = previous; }
        }
        private static MergeAnalysis mergeAnalysis;
        internal static IDisposable BeginMergeAnalysis(AvatarOptimizer opt)
        {
            var scope = new MergeAnalysis { owner = opt, clips = Clips(opt).ToArray(), previous = mergeAnalysis };
            mergeAnalysis = scope; return scope;
        }
        private static string CachedCurveSignature(AnimationClip clip, EditorCurveBinding binding)
        {
            if (mergeAnalysis == null) return CurveSignature(AnimationUtility.GetEditorCurve(clip, binding));
            var key = (clip, binding);
            if (!mergeAnalysis.curves.TryGetValue(key, out var value))
                mergeAnalysis.curves[key] = value = CurveSignature(AnimationUtility.GetEditorCurve(clip, binding));
            return value;
        }

        internal sealed class ClipUse
        {
            public Transform root;
            public AnimationClip clip;
            public RuntimeAnimatorController controller;
            private EditorCurveBinding[] floatBindings, objectBindings;
            public EditorCurveBinding[] FloatBindings => floatBindings ?? (floatBindings = AnimationUtility.GetCurveBindings(clip));
            public EditorCurveBinding[] ObjectBindings => objectBindings ?? (objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip));
        }

        internal static IEnumerable<ClipUse> Clips(AvatarOptimizer opt)
        {
            if (mergeAnalysis != null && mergeAnalysis.owner == opt)
            { foreach (var use in mergeAnalysis.clips) yield return use; yield break; }
            var owners = new List<(Transform root, RuntimeAnimatorController controller)>();
            foreach (var component in opt.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if (component is Animator animator && animator.runtimeAnimatorController != null)
                    owners.Add((animator.transform, animator.runtimeAnimatorController));
                if (component.GetType().FullName == "ABI.CCK.Components.CVRAvatar")
                {
                    var value = component.GetType().GetField("overrides")?.GetValue(component) as RuntimeAnimatorController;
                    if (value != null) owners.Add((component.transform, value));
                    // Also protect authoring inputs which may be materialized by AAS later.
                    var settings = component.GetType().GetField("avatarSettings")?.GetValue(component);
                    if (settings != null)
                        foreach (var field in settings.GetType().GetFields())
                            if (field.GetValue(settings) is RuntimeAnimatorController controller)
                                owners.Add((component.transform, controller));
                }
            }
            foreach (var owner in owners.Distinct())
            {
                var visitedControllers = new HashSet<RuntimeAnimatorController>();
                var visitedClips = new HashSet<AnimationClip>();
                for (var controller = owner.controller; controller != null && visitedControllers.Add(controller);
                    controller = (controller as AnimatorOverrideController)?.runtimeAnimatorController)
                    foreach (var clip in controller.animationClips.Where(c => c != null))
                        if (visitedClips.Add(clip)) yield return new ClipUse { root = owner.root, clip = clip, controller = owner.controller };
            }
        }

        internal static Transform Target(ClipUse use, EditorCurveBinding binding) =>
            string.IsNullOrEmpty(binding.path) ? use.root : use.root.Find(binding.path);

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Matches(string value, string patterns) => (patterns ?? "").Split(',')
            .Any(pattern => !string.IsNullOrWhiteSpace(pattern) && value.IndexOf(pattern.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);

        internal static bool HasShapeBinding(AvatarOptimizer opt, SkinnedMeshRenderer renderer, string name) =>
            Clips(opt).Any(use => use.FloatBindings.Any(binding =>
                Target(use, binding) == renderer.transform && binding.propertyName == "blendShape." + name));

        internal static bool CanBakeShape(AvatarOptimizer opt, SkinnedMeshRenderer renderer, int index)
        {
            var mesh = renderer.sharedMesh;
            string name = mesh.GetBlendShapeName(index);
            if (name.StartsWith("MT_Hide_", StringComparison.Ordinal) || Matches(name, opt.blendshapeSettings.preserveBlendshapePatterns) ||
                Matches(name, opt.blendshapeAnalysisSettings.preserveBlendshapePatterns) || HasShapeBinding(opt, renderer, name)) return false;
            // Strings and direct renderer/mesh references in runtime components are dependencies,
            // including CVR facial references even when their corresponding feature is disabled today.
            foreach (var component in opt.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return false;
                if (component is Transform || component is Renderer || component is Animator || component == opt) continue;
                var serialized = new SerializedObject(component);
                var it = serialized.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType == SerializedPropertyType.String && it.stringValue == name) return false;
                    if (it.propertyType == SerializedPropertyType.ObjectReference &&
                        (it.objectReferenceValue == renderer || it.objectReferenceValue == mesh)) return false;
                }
            }
            float weight = renderer.GetBlendShapeWeight(index);
            if (!Finite(weight)) return false;
            if (weight == 0) return true;
            if (mesh.GetBlendShapeFrameCount(index) != 1) return false;
            float frameWeight = mesh.GetBlendShapeFrameWeight(index, 0);
            if (!Finite(frameWeight) || frameWeight <= 0) return false;
            var positions = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(index, 0, positions, normals, tangents);
            return positions.Concat(normals).Concat(tangents).All(v => Finite(v.x) && Finite(v.y) && Finite(v.z));
        }

        internal static bool HasManyWeights(Mesh mesh)
        {
            using (var counts = mesh.GetBonesPerVertex())
                for (int i = 0; i < counts.Length; i++) if (counts[i] > 4) return true;
            return false;
        }

        internal static bool IsGuardedRenderer(AvatarOptimizer opt, SkinnedMeshRenderer renderer) =>
            renderer.sharedMesh != null && (HasManyWeights(renderer.sharedMesh) || opt.simplificationSettings.enabled ||
                opt.rigReductionSettings.reduceInfluences || opt.rigReductionSettings.mergeEquivalentBoneReferences ||
                opt.surfaceSettings.packUvIslands || opt.surfaceSettings.atlasMaterialSwaps);

        internal static string ComponentDependency(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            var cloth = MeshClothDependency(opt, renderer);
            if (cloth != null) return cloth;
            foreach (var component in opt.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return "missing component";
                if (component == opt || component is Transform || component is Renderer || component is Animator) continue;
                string type = component.GetType().FullName ?? "";
                // Bone simulation and constraints keep their Transform targets. They do
                // not depend on the renderer's vertex layout and are not exclusions.
                if (component is Cloth || component is UnityEngine.Animations.IConstraint ||
                    type.StartsWith("MagicaCloth.", StringComparison.Ordinal) || type.StartsWith("MagicaCloth2.", StringComparison.Ordinal) ||
                    type.Contains("DynamicBone") || type.Contains("PhysBone") || component.GetType().Name.EndsWith("Constraint", StringComparison.Ordinal)) continue;
                var iterator = new SerializedObject(component).GetIterator();
                while (iterator.Next(true))
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                        (iterator.objectReferenceValue == renderer || iterator.objectReferenceValue == renderer.sharedMesh ||
                        (iterator.objectReferenceValue is Transform bone && renderer.bones.Contains(bone))))
                        return "runtime component references mesh or rig: " + type;
            }
            return null;
        }

        internal static string MeshClothDependency(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            if (renderer.GetComponent<Cloth>() != null) return "Unity Cloth targets this renderer: " + renderer.name;
            foreach (var component in opt.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var type = component.GetType().FullName;
                if (type == "MagicaCloth2.MagicaCloth")
                {
                    var serialized = new SerializedObject(component);
                    var kind = serialized.FindProperty("serializeData.clothType");
                    if (kind == null || kind.propertyType != SerializedPropertyType.Enum) continue;
                    if (kind.enumValueIndex < 0 || kind.enumValueIndex >= kind.enumNames.Length || kind.enumNames[kind.enumValueIndex] != "MeshCloth") continue;
                    var renderers = serialized.FindProperty("serializeData.sourceRenderers");
                    if (renderers == null || !renderers.isArray) continue;
                    for (int i = 0; i < renderers.arraySize; i++)
                        if (renderers.GetArrayElementAtIndex(i).objectReferenceValue == renderer)
                            return "Magica Cloth 2 MeshCloth '" + component.name + "' targets this renderer";
                }
                else if (type == "MagicaCloth.MagicaMeshCloth")
                {
                    var virtualDeformer = new SerializedObject(component).FindProperty("virtualDeformer")?.objectReferenceValue;
                    if (virtualDeformer == null) continue;
                    var renderDeformers = new SerializedObject(virtualDeformer).FindProperty("deformer.renderDeformerList");
                    if (renderDeformers == null || !renderDeformers.isArray) continue;
                    for (int i = 0; i < renderDeformers.arraySize; i++)
                    {
                        var renderDeformer = renderDeformers.GetArrayElementAtIndex(i).objectReferenceValue;
                        if (renderDeformer == null) continue;
                        var target = new SerializedObject(renderDeformer).FindProperty("deformer.targetObject")?.objectReferenceValue;
                        if (target == renderer.gameObject)
                            return "Magica Cloth MeshCloth '" + component.name + "' targets this renderer through its deformer";
                    }
                }
            }
            return null;
        }

        internal static bool Tagged(AvatarOptimizer opt, Transform target)
        {
            foreach (var component in opt.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().FullName != "ABI.CCK.Components.CVRAvatar") continue;
                var property = new SerializedObject(component).FindProperty("advancedTaggingList");
                if (property == null) continue;
                var end = property.GetEndProperty();
                while (property.Next(true) && !SerializedProperty.EqualContents(property, end))
                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        Transform t = (property.objectReferenceValue as GameObject)?.transform ?? (property.objectReferenceValue as Component)?.transform;
                        if (t != null && (target == t || target.IsChildOf(t) || t.IsChildOf(target))) return true;
                    }
            }
            return false;
        }

        internal static string Visibility(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            var result = new StringBuilder(renderer.enabled ? "enabled" : "disabled");
            for (var t = renderer.transform; t != null && t != opt.transform.parent; t = t.parent)
            {
                bool converted = t == renderer.transform && HasToggleShape(renderer);
                if (!converted && !t.gameObject.activeSelf) result.Append("|inactive:").Append(t.GetInstanceID());
                foreach (var use in Clips(opt))
                    foreach (var binding in use.FloatBindings)
                        if (Target(use, binding) == t && ((!converted && binding.propertyName == "m_IsActive") ||
                            (t == renderer.transform && binding.propertyName == "m_Enabled" && binding.type == typeof(SkinnedMeshRenderer))))
                            result.Append('|').Append(use.root.GetInstanceID()).Append(':').Append(use.clip.GetInstanceID())
                                .Append(':').Append(binding.type.FullName).Append(':').Append(binding.propertyName)
                                .Append(':').Append(CurveSignature(AnimationUtility.GetEditorCurve(use.clip, binding)));
            }
            return result.ToString();
        }

        private static string CurveSignature(AnimationCurve curve)
        {
            if (curve == null) return "null";
            var result = new StringBuilder().Append((int)curve.preWrapMode).Append(':').Append((int)curve.postWrapMode);
            foreach (var key in curve.keys)
            {
                foreach (float value in new[] { key.time, key.value, key.inTangent, key.outTangent, key.inWeight, key.outWeight })
                    result.Append(':').Append(Convert.ToBase64String(BitConverter.GetBytes(value)));
                result.Append(':').Append((int)key.weightedMode);
            }
            return result.ToString();
        }

        internal static bool MaterialSlot(string property, out int slot)
        {
            var match = Regex.Match(property ?? "", @"^m_Materials\.Array\.data\[(\d+)\]$");
            slot = -1;
            return match.Success && int.TryParse(match.Groups[1].Value, out slot);
        }

        private static bool MergeMaterialSupported(Material material) => material != null && material.shader != null &&
            material.renderQueue <= 2500 && (material.GetTag("RenderType", false, "Opaque") == "Opaque" ||
                material.GetTag("RenderType", false, "Opaque") == "TransparentCutout");

        internal static Mesh BuildMergedMesh(AvatarOptimizer opt, List<SkinnedMeshRenderer> group,
            out Transform[] bones, out Material[] materials, out Dictionary<SkinnedMeshRenderer, int[]> slots,
            out Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> shapes)
        {
            var result = new Mesh { name = "CombinedMesh_" + group[0].name };
            slots = new Dictionary<SkinnedMeshRenderer, int[]>();
            shapes = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var tangents = new List<Vector4>();
            var colors = new List<Color>(); var uvs = Enumerable.Range(0, 8).Select(_ => new List<Vector4>()).ToArray();
            var weights = new List<List<BoneWeight1>>(); var allBones = new List<Transform>(); var bindposes = new List<Matrix4x4>();
            var allMaterials = new List<Material>(); var indices = new List<List<int>>();
            var staticSlots = new Dictionary<Material, int>();
            var usedNames = new HashSet<string>();
            int count = group.Sum(r => r.sharedMesh.vertexCount);
            result.indexFormat = count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            try
            {
                foreach (var renderer in group)
                {
                    var mesh = renderer.sharedMesh;
                    int offset = vertices.Count, boneOffset = allBones.Count;
                    vertices.AddRange(mesh.vertices); normals.AddRange(mesh.normals); tangents.AddRange(mesh.tangents); colors.AddRange(mesh.colors);
                    for (int channel = 0; channel < 8; channel++)
                    { var values = new List<Vector4>(); mesh.GetUVs(channel, values); uvs[channel].AddRange(values); }
                    foreach (var sourceWeights in ReadWeights(mesh))
                        weights.Add(sourceWeights.Select(w => new BoneWeight1 { boneIndex = w.boneIndex + boneOffset, weight = w.weight }).ToList());
                    allBones.AddRange(renderer.bones); bindposes.AddRange(mesh.bindposes);
                    var animatedSlots = new HashSet<int>(Clips(opt).SelectMany(use => use.ObjectBindings
                        .Where(b => Target(use, b) == renderer.transform && MaterialSlot(b.propertyName, out _))
                        .Select(b => { MaterialSlot(b.propertyName, out var slot); return slot; })));
                    var mapping = new int[mesh.subMeshCount]; slots[renderer] = mapping;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        var material = renderer.sharedMaterials[sub];
                        int destination;
                        // Animated slots stay independent even when their initial materials are identical.
                        if (animatedSlots.Contains(sub) || !staticSlots.TryGetValue(material, out destination))
                        {
                            destination = allMaterials.Count; allMaterials.Add(material); indices.Add(new List<int>());
                            if (!animatedSlots.Contains(sub)) staticSlots[material] = destination;
                        }
                        mapping[sub] = destination;
                        indices[destination].AddRange(mesh.GetIndices(sub).Select(i => i + offset));
                    }
                    var names = new Dictionary<string, string>(); shapes[renderer] = names;
                    for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    {
                        string original = mesh.GetBlendShapeName(shape), name = original;
                        int suffix = 1; while (!usedNames.Add(name)) name = original + "_Merged_" + suffix++;
                        names[original] = name;
                    }
                }
                result.SetVertices(vertices);
                if (normals.Count > 0) result.SetNormals(normals);
                if (tangents.Count > 0) result.SetTangents(tangents);
                if (colors.Count > 0) result.SetColors(colors);
                for (int channel = 0; channel < 8; channel++)
                {
                    if (uvs[channel].Count == 0) continue;
                    int dimension = group[0].sharedMesh.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel));
                    if (dimension == 2) result.SetUVs(channel, uvs[channel].Select(v => new Vector2(v.x, v.y)).ToList());
                    else if (dimension == 3) result.SetUVs(channel, uvs[channel].Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                    else result.SetUVs(channel, uvs[channel]);
                }
                result.bindposes = bindposes.ToArray(); WriteWeights(result, weights);
                result.subMeshCount = indices.Count;
                for (int sub = 0; sub < indices.Count; sub++) result.SetTriangles(indices[sub], sub, false);
                int vertexOffset = 0;
                foreach (var renderer in group)
                {
                    var mesh = renderer.sharedMesh;
                    for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                        for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                        {
                            var dp = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                            mesh.GetBlendShapeFrameVertices(shape, frame, dp, dn, dt);
                            var positions = new Vector3[count]; var frameNormals = new Vector3[count]; var frameTangents = new Vector3[count];
                            Array.Copy(dp, 0, positions, vertexOffset, dp.Length); Array.Copy(dn, 0, frameNormals, vertexOffset, dn.Length); Array.Copy(dt, 0, frameTangents, vertexOffset, dt.Length);
                            result.AddBlendShapeFrame(shapes[renderer][mesh.GetBlendShapeName(shape)], mesh.GetBlendShapeFrameWeight(shape, frame), positions, frameNormals, frameTangents);
                        }
                    vertexOffset += mesh.vertexCount;
                }
                result.RecalculateBounds(); bones = allBones.ToArray(); materials = allMaterials.ToArray();
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        internal static string RendererMergeRejection(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            if (mergeAnalysis == null || mergeAnalysis.owner != opt || renderer == null) return RendererMergeRejectionUncached(opt, renderer);
            if (!mergeAnalysis.eligibility.TryGetValue(renderer, out var reason))
                mergeAnalysis.eligibility[renderer] = reason = RendererMergeRejectionUncached(opt, renderer);
            return reason;
        }

        private static string RendererMergeRejectionUncached(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null || renderer.bones.Length == 0) return "missing skinned mesh/rig";
            if (renderer.transform == opt.transform || renderer.transform.parent == null) return "avatar-root renderer cannot be moved to a sibling";
            if (!renderer.sharedMesh.isReadable) return "mesh is not readable";
            if (renderer.GetComponents<SkinnedMeshRenderer>().Length != 1) return "multiple skinned renderers share an animation path";
            if (renderer.sharedMesh.subMeshCount != renderer.sharedMaterials.Length) return "material/submesh count mismatch";
            if (Enumerable.Range(0, renderer.sharedMesh.subMeshCount).Any(i => renderer.sharedMesh.GetTopology(i) != MeshTopology.Triangles)) return "non-triangle topology";
            for (int channel = 0; channel < 8; channel++)
                if (renderer.sharedMesh.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel)) == 1)
                    return "one-dimensional UV stream is unsupported";
            if (renderer.bones.Any(b => b == null) || renderer.bones.Length != renderer.sharedMesh.bindposes.Length) return "invalid rig";
            if (Tagged(opt, renderer.transform)) return "Advanced Tagging or fallback boundary";
            if (ComponentDependency(opt, renderer) is string dependency) return dependency;
            if (renderer.sharedMaterials.Any(m => !MergeMaterialSupported(m))) return "transparent or unsupported render state (opaque and cutout supported)";
            if ((opt.meshSettings.mergeToggleObjects ? opt.meshSettings.excludeFaceMeshFromToggleMerge : opt.meshSettings.excludeFaceMeshFromCombine) &&
                opt.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().FullName == "ABI.CCK.Components.CVRAvatar" &&
                    c.GetType().GetField("bodyMesh")?.GetValue(c) as SkinnedMeshRenderer == renderer)) return "protected CVR face mesh";
            foreach (var use in Clips(opt))
            {
                foreach (var binding in use.FloatBindings.Concat(use.ObjectBindings))
                {
                    if (Target(use, binding) != renderer.transform) continue;
                    if (use.root != opt.transform) return "renderer animation belongs to a nested animation root";
                    if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive") continue;
                    if (!typeof(Renderer).IsAssignableFrom(binding.type))
                    { if (binding.type == typeof(Transform)) return "renderer transform is animated"; continue; }
                    if (binding.propertyName == "m_Enabled") continue;
                    if (binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    { if (renderer.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring(11)) < 0) return "animated blendshape is missing"; continue; }
                    if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal) && !binding.isPPtrCurve) continue;
                    if (!MaterialSlot(binding.propertyName, out var slot))
                        return "renderer-wide property animation cannot be isolated per material slot: " + binding.propertyName;
                    if (slot < 0 || slot >= renderer.sharedMaterials.Length) return "animated material slot is out of range";
                    if (AnimationUtility.GetObjectReferenceCurve(use.clip, binding)?.Any(k => !(k.value is Material material) || !MergeMaterialSupported(material)) == true)
                        return "material swap reaches transparent or unsupported material";
                }
            }
            return null;
        }

        internal static bool HasMaterialAnimation(AvatarOptimizer opt, Renderer renderer) => Clips(opt).Any(use =>
            use.FloatBindings.Concat(use.ObjectBindings)
                .Any(binding => Target(use, binding) == renderer.transform &&
                    (binding.propertyName.Contains("material.") || binding.propertyName.Contains("m_Materials"))));

        internal static bool MaskIncludes(AvatarMask mask, string path)
        {
            bool active = mask.transformCount == 0; int longest = -1;
            for (int i = 0; i < mask.transformCount; i++)
            {
                string candidate = mask.GetTransformPath(i);
                if ((path == candidate || candidate.Length == 0 || path.StartsWith(candidate + "/", StringComparison.Ordinal)) && candidate.Length > longest)
                { longest = candidate.Length; active = mask.GetTransformActive(i); }
            }
            return active;
        }

        internal static string MaterialAnimationMergeRejection(AvatarOptimizer opt, SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            var properties = new HashSet<string>();
            foreach (var use in Clips(opt))
            {
                var bindings = use.FloatBindings.Where(c => typeof(Renderer).IsAssignableFrom(c.type) &&
                    c.propertyName.StartsWith("material.", StringComparison.Ordinal)).ToArray();
                var left = bindings.Where(c => Target(use, c) == a.transform).ToDictionary(c => c.propertyName);
                var right = bindings.Where(c => Target(use, c) == b.transform).ToDictionary(c => c.propertyName);
                foreach (var property in left.Keys.Concat(right.Keys).Distinct())
                {
                    if (!left.TryGetValue(property, out var x) || !right.TryGetValue(property, out var y))
                        return "shader-property animation differs: " + property + " is not driven on both renderers in '" + use.clip.name + "'";
                    if (x.type != y.type || CachedCurveSignature(use.clip, x) != CachedCurveSignature(use.clip, y))
                        return "shader-property curves differ: " + property + " in '" + use.clip.name + "'";
                    properties.Add(property.Substring("material.".Length).Split('.')[0]);
                }
            }
            if (properties.Count == 0) return null;
            // Renderer property blocks also contain unanimated vector/color components and write-default values.
            var materials = a.sharedMaterials.Concat(b.sharedMaterials).ToList();
            foreach (var use in Clips(opt))
                foreach (var binding in use.ObjectBindings)
                    if ((Target(use, binding) == a.transform || Target(use, binding) == b.transform) && MaterialSlot(binding.propertyName, out _))
                        materials.AddRange(AnimationUtility.GetObjectReferenceCurve(use.clip, binding).Select(k => k.value as Material));
            foreach (var property in properties)
            {
                string first = null;
                foreach (var material in materials.Distinct())
                {
                    if (material == null || !material.HasProperty(property)) return "animated shader property is missing from a reachable material: " + property;
                    int index = material.shader.FindPropertyIndex(property);
                    if (index < 0) return "animated shader property has no declared type: " + property;
                    string value;
                    switch (material.shader.GetPropertyType(index))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range: value = material.GetFloat(property).ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: value = VectorSignature(material.GetVector(property)); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color: value = VectorSignature((Vector4)material.GetColor(property)); break;
                        default: return "unsupported animated shader property type: " + property;
                    }
                    if (first != null && first != value) return "animated shader property defaults differ: " + property;
                    first = value;
                }
            }
            return null;
        }

        private static string VectorSignature(Vector4 value) => string.Join(",", new[] { value.x, value.y, value.z, value.w }
            .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        internal static string MergeRejection(AvatarOptimizer opt, SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            var reason = RendererMergeRejection(opt, a) ?? RendererMergeRejection(opt, b);
            if (reason != null) return reason;
            if (a.bones.Length != a.sharedMesh.bindposes.Length || b.bones.Length != b.sharedMesh.bindposes.Length) return "invalid bind poses";
            foreach (UnityEngine.Rendering.VertexAttribute attribute in Enum.GetValues(typeof(UnityEngine.Rendering.VertexAttribute)))
            {
                if (attribute == UnityEngine.Rendering.VertexAttribute.BlendWeight || attribute == UnityEngine.Rendering.VertexAttribute.BlendIndices) continue;
                if (a.sharedMesh.HasVertexAttribute(attribute) != b.sharedMesh.HasVertexAttribute(attribute) ||
                    a.sharedMesh.GetVertexAttributeDimension(attribute) != b.sharedMesh.GetVertexAttributeDimension(attribute)) return "different vertex stream layouts";
            }
            if (Clips(opt).Any(use => use.FloatBindings.Any(binding =>
                binding.type == typeof(Transform) && (Target(use, binding) == a.transform || Target(use, binding) == b.transform))))
                return "renderer transform is animated";
            if (a.shadowCastingMode != b.shadowCastingMode || a.receiveShadows != b.receiveShadows ||
                a.lightProbeUsage != b.lightProbeUsage || a.reflectionProbeUsage != b.reflectionProbeUsage ||
                a.probeAnchor != b.probeAnchor || a.gameObject.layer != b.gameObject.layer ||
                a.lightProbeProxyVolumeOverride != b.lightProbeProxyVolumeOverride || a.forceRenderingOff != b.forceRenderingOff ||
                a.lightmapIndex != b.lightmapIndex || a.realtimeLightmapIndex != b.realtimeLightmapIndex ||
                a.lightmapScaleOffset != b.lightmapScaleOffset || a.realtimeLightmapScaleOffset != b.realtimeLightmapScaleOffset ||
                a.sortingLayerID != b.sortingLayerID || a.sortingOrder != b.sortingOrder ||
                a.motionVectorGenerationMode != b.motionVectorGenerationMode || a.quality != b.quality ||
                a.updateWhenOffscreen != b.updateWhenOffscreen || a.skinnedMotionVectors != b.skinnedMotionVectors ||
                a.allowOcclusionWhenDynamic != b.allowOcclusionWhenDynamic || a.renderingLayerMask != b.renderingLayerMask)
                return "different renderer settings";
            foreach (var controller in Clips(opt).Select(u => u.controller).Distinct())
            {
                var baseController = controller;
                while (baseController is AnimatorOverrideController over) baseController = over.runtimeAnimatorController;
                if (!(baseController is AnimatorController animatorController)) continue;
                foreach (var layer in animatorController.layers)
                    if (layer.avatarMask != null && MaskIncludes(layer.avatarMask, AnimationUtility.CalculateTransformPath(a.transform, opt.transform)) !=
                        MaskIncludes(layer.avatarMask, AnimationUtility.CalculateTransformPath(b.transform, opt.transform))) return "different AvatarMask coverage";
            }
            var materialReason = MaterialAnimationMergeRejection(opt, a, b);
            if (materialReason != null) return materialReason;
            if (Visibility(opt, a) != Visibility(opt, b)) return "different original visibility (convert explicitly within budgets first)";
            // Distinct animated transform frames cannot be merged safely by a static CombineMeshes matrix.
            if (a.transform.parent != b.transform.parent || a.transform.localToWorldMatrix != b.transform.localToWorldMatrix)
                return "different transform frames";
            return null;
        }

        internal static string MergeBudgetRejection(AvatarOptimizer opt, List<SkinnedMeshRenderer> group)
        {
            var bounds = group[0].bounds;
            double volume = 0;
            long added = 0;
            int vertices = group.Sum(r => r.sharedMesh.vertexCount);
            foreach (var r in group)
            {
                bounds.Encapsulate(r.bounds);
                volume += Math.Max(0.000001, r.bounds.size.x * r.bounds.size.y * r.bounds.size.z);
                added += (long)(vertices - r.sharedMesh.vertexCount) * Enumerable.Range(0, r.sharedMesh.blendShapeCount).Sum(r.sharedMesh.GetBlendShapeFrameCount) * 36;
            }
            double combinedVolume = bounds.size.x * bounds.size.y * bounds.size.z;
            if (combinedVolume / Math.Max(volume, 0.000001) > opt.mergeBudgetSettings.maximumBoundsVolumeRatio) return "culling bounds budget exceeded";
            if (opt.mergeBudgetSettings.maximumAddedMorphMemoryMB > 0 && added > opt.mergeBudgetSettings.maximumAddedMorphMemoryMB * 1024.0 * 1024.0) return "additional morph memory budget exceeded";
            bool toggles = group.Any(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Any(i => r.sharedMesh.GetBlendShapeName(i).StartsWith("MT_Hide_")));
            if (toggles && opt.mergeBudgetSettings.maximumHiddenVertices > 0 && vertices > opt.mergeBudgetSettings.maximumHiddenVertices) return "hidden submitted vertices budget exceeded";
            return null;
        }

        internal static string DescribeGroup(List<SkinnedMeshRenderer> group)
        {
            int vertices = group.Sum(r => r.sharedMesh.vertexCount);
            long morphBytes = group.Sum(r => (long)vertices * r.sharedMesh.blendShapeCount * 36);
            return $"[AvatarOptimizer] Proposed group [{string.Join(", ", group.Select(r => r.name))}]: {group.Count - 1} fewer renderer components; independent animated material slots retain their draw calls; " +
                $"{vertices} submitted vertices if visible, up to {vertices} hidden vertices with independent toggles; conservative morph allocation {morphBytes} bytes; " +
                $"{group.Count} culling bounds become one. These are estimates, not GPU timings.";
        }

        public static string DescribeMergePlan(AvatarOptimizer opt)
        {
            var report = new StringBuilder();
            var groups = new List<List<SkinnedMeshRenderer>>();
            foreach (var r in opt.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var reason = RendererMergeRejection(opt, r);
                if (reason != null) { report.AppendLine(r.name + ": " + reason); continue; }
                var group = groups.FirstOrDefault(g => g.All(member => MergeRejection(opt, member, r) == null));
                if (group == null) groups.Add(new List<SkinnedMeshRenderer> { r }); else group.Add(r);
            }
            foreach (var group in groups.Where(g => g.Count > 1)) report.AppendLine(DescribeGroup(group)).AppendLine(MergeBudgetRejection(opt, group) ?? "Eligible");
            return report.Length == 0 ? "No eligible merge groups." : report.ToString();
        }

        internal static bool HasToggleShape(SkinnedMeshRenderer renderer) => renderer.sharedMesh != null &&
            Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Any(i => renderer.sharedMesh.GetBlendShapeName(i).StartsWith("MT_Hide_", StringComparison.Ordinal));

        internal static bool CanPrepareToggle(AvatarOptimizer opt, Transform target) => TogglePreparationRejection(opt, target) == null;

        internal static string TogglePreparationRejection(AvatarOptimizer opt, Transform target)
        {
            if (!opt.meshSettings.mergeToggleObjects) return "Merge Toggle Objects is disabled";
            if (Tagged(opt, target)) return "Advanced Tagging boundary";
            var renderer = target.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null) return "toggle target has no directly attached skinned renderer; ancestor toggles stay intact";
            var reason = RendererMergeRejection(opt, renderer);
            if (reason != null) return reason;
            if (!renderer.enabled) return "renderer is disabled";
            int vertices = renderer.sharedMesh.vertexCount;
            if (opt.mergeBudgetSettings.maximumHiddenVertices > 0 && vertices > opt.mergeBudgetSettings.maximumHiddenVertices) return "hidden vertex limit exceeded";
            if (opt.mergeBudgetSettings.maximumAddedMorphMemoryMB > 0 && vertices * 36.0 > opt.mergeBudgetSettings.maximumAddedMorphMemoryMB * 1048576.0) return "hide-shape memory limit exceeded";
            if (Clips(opt).Any(use => use.FloatBindings.Any(b => Target(use, b) == target && b.propertyName == "m_Enabled")))
                return "renderer-enabled animation requires a separate visibility conversion";
            return null;
        }

        private static Type BackendType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        internal static bool BackendAvailable(string name, out string reason)
        {
            reason = null;
            if (name == "UnityMeshSimplifier")
            {
                if (BackendType("MilchZocker.SimplificationPrototype.MeshSimplifier") == null)
                    reason = "Bundled UnityMeshSimplifier runtime is unavailable.";
            }
            else if (name == "Meshia")
            {
                var missing = new List<string>();
                if (!VersionAtLeast(Application.unityVersion, "2022.3")) missing.Add("Unity 2022.3+");
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (var requirement in new[] { "com.unity.burst|1.8.21", "com.unity.collections|2.5.7", "com.unity.mathematics|1.3.2" })
                {
                    var parts = requirement.Split('|');
                    var package = packages.FirstOrDefault(p => p.name == parts[0]);
                    if (package == null || !VersionAtLeast(package.version, parts[1])) missing.Add(parts[0] + " " + parts[1] + "+");
                }
                var backend = BackendType("Meshia.MeshSimplification.MeshSimplifier");
                var target = BackendType("Meshia.MeshSimplification.MeshSimplificationTarget");
                var options = BackendType("Meshia.MeshSimplification.MeshSimplifierOptions");
                if (backend == null || target == null || options == null ||
                    backend.GetMethod("Simplify", new[] { typeof(Mesh), target, options, typeof(Mesh) }) == null ||
                    target.GetField("Kind") == null || target.GetField("Value") == null ||
                    options.GetProperty("Default") == null || options.GetField("PreserveBorderEdges") == null ||
                    options.GetField("PreserveSurfaceCurvature") == null || options.GetField("EnableSmartLink") == null)
                    missing.Add("compatible Meshia runtime (tested with 3.2.0)");
                if (missing.Count > 0) reason = "Meshia requires: " + string.Join(", ", missing) + ".";
            }
            else reason = "Unknown simplification backend: " + name;
            return reason == null;
        }

        private static bool VersionAtLeast(string value, string minimum)
        {
            var match = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^\d+\.\d+(?:\.\d+)?");
            return match.Success && Version.TryParse(match.Value, out var version) && version >= new Version(minimum);
        }

        internal static Mesh CreateSimplificationCandidate(Mesh source, string name, float ratio)
        {
            if (!BackendAvailable(name, out var reason)) throw new InvalidOperationException(reason + " No automatic fallback is used.");
            if (HasManyWeights(source)) throw new InvalidOperationException("More than four influences per vertex are not supported by these simplification adapters.");
            if (name == "Meshia")
            {
                var backend = BackendType("Meshia.MeshSimplification.MeshSimplifier");
                var targetType = BackendType("Meshia.MeshSimplification.MeshSimplificationTarget");
                var optionsType = BackendType("Meshia.MeshSimplification.MeshSimplifierOptions");
                var target = Activator.CreateInstance(targetType);
                var kind = targetType.GetField("Kind");
                kind.SetValue(target, Enum.Parse(kind.FieldType, "RelativeTriangleCount"));
                targetType.GetField("Value").SetValue(target, Mathf.Clamp01(ratio));
                var options = optionsType.GetProperty("Default").GetValue(null);
                optionsType.GetField("PreserveBorderEdges").SetValue(options, true);
                optionsType.GetField("PreserveSurfaceCurvature").SetValue(options, true);
                optionsType.GetField("EnableSmartLink").SetValue(options, false);
                var output = new Mesh();
                try
                {
                    backend.GetMethod("Simplify", new[] { typeof(Mesh), targetType, optionsType, typeof(Mesh) })
                        .Invoke(null, new[] { (object)source, target, options, output });
                    return output;
                }
                catch { UnityEngine.Object.DestroyImmediate(output); throw; }
            }
            var ums = BackendType("MilchZocker.SimplificationPrototype.MeshSimplifier");
            var instance = Activator.CreateInstance(ums);
            var optionsProperty = ums.GetProperty("SimplificationOptions");
            var umsOptions = optionsProperty.GetValue(instance);
            foreach (var field in new[] { "PreserveBorderEdges", "PreserveUVSeamEdges", "PreserveUVFoldoverEdges", "PreserveSurfaceCurvature" })
                umsOptions.GetType().GetField(field).SetValue(umsOptions, true);
            umsOptions.GetType().GetField("EnableSmartLink").SetValue(umsOptions, false);
            optionsProperty.SetValue(instance, umsOptions);
            ums.GetMethod("Initialize", new[] { typeof(Mesh) }).Invoke(instance, new object[] { source });
            ums.GetMethod("SimplifyMesh", new[] { typeof(float) }).Invoke(instance, new object[] { Mathf.Clamp01(ratio) });
            return (Mesh)ums.GetMethod("ToMesh").Invoke(instance, null);
        }

        internal static void ProcessRigAndSimplification(AvatarOptimizer opt, Action<string> log)
        {
            if (opt.reportOnly) return;
            foreach (var renderer in opt.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var original = renderer.sharedMesh;
                if (original == null) continue;
                if (opt.rigReductionSettings.mergeEquivalentBoneReferences || opt.rigReductionSettings.reduceInfluences)
                {
                    Mesh candidate = null;
                    try
                    {
                        var clothReason = MeshClothDependency(opt, renderer);
                        if (clothReason != null) throw new InvalidOperationException(clothReason);
                        candidate = UnityEngine.Object.Instantiate(original);
                        candidate.name = original.name + "_RigBudget";
                        var bones = renderer.bones;
                        var poses = original.bindposes;
                        if (bones.Length != poses.Length) throw new InvalidOperationException("bone/bind-pose count mismatch");
                        var remap = Enumerable.Range(0, bones.Length).ToArray();
                        if (opt.rigReductionSettings.mergeEquivalentBoneReferences)
                            for (int i = 0; i < bones.Length; i++)
                                for (int j = 0; j < i; j++)
                                    if (bones[i] != null && bones[i] == bones[j] && poses[i].Equals(poses[j])) { remap[i] = remap[j]; break; }
                        var kept = remap.Distinct().OrderBy(i => i).ToArray();
                        var compact = kept.Select((value, index) => (value, index)).ToDictionary(p => p.value, p => p.index);
                        var streams = ReadWeights(original);
                        bool loss = false;
                        for (int vertex = 0; vertex < streams.Count; vertex++)
                        {
                            if (streams[vertex].Any(w => w.boneIndex < 0 || w.boneIndex >= bones.Length || !Finite(w.weight) || w.weight < 0))
                                throw new InvalidOperationException("invalid imported bone weights");
                            var weights = streams[vertex].GroupBy(w => remap[w.boneIndex])
                                .Select(g => new BoneWeight1 { boneIndex = g.Key, weight = g.Sum(w => w.weight) }).OrderByDescending(w => w.weight).ToList();
                            if (opt.rigReductionSettings.reduceInfluences && weights.Count > opt.rigReductionSettings.maximumInfluences)
                            {
                                loss = true;
                                weights = weights.Take(Math.Max(1, opt.rigReductionSettings.maximumInfluences)).ToList();
                                float sum = weights.Sum(w => w.weight);
                                if (sum <= 0) throw new InvalidOperationException("zero total weight");
                                weights = weights.Select(w => new BoneWeight1 { boneIndex = w.boneIndex, weight = w.weight / sum }).ToList();
                            }
                            streams[vertex] = weights;
                        }
                        if (loss)
                        {
                            var reason = GeometryExclusion(opt, renderer);
                            if (reason != null) throw new InvalidOperationException(reason);
                            // Validate against the original bone table before compacting references.
                            WriteWeights(candidate, streams);
                            if (!ValidatePoses(opt, renderer, candidate, opt.rigReductionSettings.maximumPositionError, true, out reason))
                                throw new InvalidOperationException(reason);
                        }
                        if (loss || kept.Length != bones.Length)
                        {
                            foreach (var weights in streams)
                                for (int i = 0; i < weights.Count; i++) weights[i] = new BoneWeight1 { boneIndex = compact[weights[i].boneIndex], weight = weights[i].weight };
                            WriteWeights(candidate, streams);
                            candidate.bindposes = kept.Select(i => poses[i]).ToArray();
                            renderer.sharedMesh = candidate;
                            renderer.bones = kept.Select(i => bones[i]).ToArray();
                            ObjectRegistry.RegisterReplacedObject(original, candidate);
                            log($"[AvatarOptimizer] {renderer.name}: coalesced {bones.Length - kept.Length} equivalent references; influence reduction {loss}. No Transforms deleted.");
                            candidate = null;
                        }
                    }
                    catch (Exception ex) { log($"[AvatarOptimizer] Rig reduction skipped {renderer.name}: {ex.Message}"); }
                    finally { if (candidate != null) UnityEngine.Object.DestroyImmediate(candidate); }
                }
                if (!opt.simplificationSettings.enabled) continue;
                string exclusion = GeometryExclusion(opt, renderer);
                if (exclusion != null) { log($"[AvatarOptimizer] Simplification skipped {renderer.name}: {exclusion}"); continue; }
                Mesh simplified = null;
                try
                {
                    if (!opt.simplificationSettings.allowSkinnedMeshes && ReadWeights(renderer.sharedMesh).Any(w => w.Count > 0))
                        throw new InvalidOperationException("skinned simplification is opt-in");
                    if (HasManyWeights(renderer.sharedMesh)) throw new InvalidOperationException("simplification backends use four-weight input; retaining original variable-length weights");
                    if (!opt.simplificationSettings.preserveSeams) throw new InvalidOperationException("prototype requires seam protection; disabling it is unsupported");
                    simplified = CreateSimplificationCandidate(renderer.sharedMesh, opt.simplificationSettings.backend,
                        opt.simplificationSettings.targetTriangleRatio);
                    if (!StreamsCompatible(renderer.sharedMesh, simplified)) throw new InvalidOperationException("backend changed required stream/frame layout");
                    if (simplified.triangles.Length >= renderer.sharedMesh.triangles.Length) throw new InvalidOperationException("no triangle reduction");
                    if (!ValidatePoses(opt, renderer, simplified, opt.simplificationSettings.maximumPositionError, false, out var reason))
                        throw new InvalidOperationException(reason);
                    var source = renderer.sharedMesh;
                    simplified.name = source.name + "_PoseValidated";
                    renderer.sharedMesh = simplified;
                    ObjectRegistry.RegisterReplacedObject(source, simplified);
                    log($"[AvatarOptimizer] Simplified {renderer.name}: {source.triangles.Length / 3} to {simplified.triangles.Length / 3} triangles; sampled pose error accepted.");
                    simplified = null;
                }
                catch (Exception ex) { log($"[AvatarOptimizer] Simplification skipped {renderer.name}: {ex.GetBaseException().Message}"); }
                finally { if (simplified != null) UnityEngine.Object.DestroyImmediate(simplified); }
            }
        }

        internal static List<List<BoneWeight1>> ReadWeights(Mesh mesh)
        {
            var result = new List<List<BoneWeight1>>(mesh.vertexCount);
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                int offset = 0;
                for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
                {
                    var list = new List<BoneWeight1>();
                    if (counts.Length > 0) for (int i = 0; i < counts[vertex]; i++) list.Add(weights[offset++]);
                    result.Add(list);
                }
            }
            return result;
        }

        internal static void WriteWeights(Mesh mesh, List<List<BoneWeight1>> weights)
        {
            // Drop the old packed index layout before Unity chooses the layout for the
            // replacement stream (e.g. two influences becoming six).
            mesh.boneWeights = Array.Empty<BoneWeight>();
            using (var counts = new Unity.Collections.NativeArray<byte>(weights.Select(w => checked((byte)w.Count)).ToArray(), Unity.Collections.Allocator.Temp))
            using (var data = new Unity.Collections.NativeArray<BoneWeight1>(weights.SelectMany(w => w).ToArray(), Unity.Collections.Allocator.Temp))
                mesh.SetBoneWeights(counts, data);
        }

        internal static string GeometryExclusion(AvatarOptimizer opt, SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null || !mesh.isReadable) return "mesh is not readable";
            if (opt.simplificationSettings.protectedRegions.Any(t => t != null && (renderer.transform == t || renderer.transform.IsChildOf(t)))) return "protected region";
            if (opt.simplificationSettings.protectFaceAndHands && (Matches(renderer.name, "face,head,hand,finger") ||
                renderer.bones.Any(b => b != null && Matches(b.name, "finger,thumb,index,middle,ring,pinky,hand")))) return "protected face/hand region";
            if (Tagged(opt, renderer.transform)) return "Advanced Tagging boundary";
            if (ComponentDependency(opt, renderer) is string dependency) return dependency;
            if (mesh.vertices.Any(v => !Finite(v.x) || !Finite(v.y) || !Finite(v.z))) return "non-finite geometry";
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                if (mesh.GetBlendShapeName(shape).StartsWith("MT_Hide_", StringComparison.Ordinal)) return "non-finite toggle shape";
            return null;
        }

        internal static bool StreamsCompatible(Mesh source, Mesh result)
        {
            if (result == null || source.subMeshCount != result.subMeshCount || source.blendShapeCount != result.blendShapeCount ||
                !source.bindposes.SequenceEqual(result.bindposes)) return false;
            foreach (UnityEngine.Rendering.VertexAttribute attribute in Enum.GetValues(typeof(UnityEngine.Rendering.VertexAttribute)))
                if (source.HasVertexAttribute(attribute) != result.HasVertexAttribute(attribute) ||
                    (source.HasVertexAttribute(attribute) && source.GetVertexAttributeDimension(attribute) != result.GetVertexAttributeDimension(attribute))) return false;
            for (int shape = 0; shape < source.blendShapeCount; shape++)
            {
                if (source.GetBlendShapeName(shape) != result.GetBlendShapeName(shape) || source.GetBlendShapeFrameCount(shape) != result.GetBlendShapeFrameCount(shape)) return false;
                for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    if (source.GetBlendShapeFrameWeight(shape, frame) != result.GetBlendShapeFrameWeight(shape, frame)) return false;
            }
            foreach (var weights in ReadWeights(result))
                if (weights.Count > 0 && (weights.Any(w => !Finite(w.weight) || w.weight < 0) || Math.Abs(weights.Sum(w => w.weight) - 1) > 0.0001f)) return false;
            return true;
        }

        private sealed class PoseTest { public AnimationClip clip; public string root; public float time; public int bone = -1; public Vector3 rotation; public int shape = -1; public float weight; }

        internal static bool ValidatePoses(AvatarOptimizer opt, SkinnedMeshRenderer source, Mesh candidate, float tolerance, bool sameTopology, out string reason)
        {
            reason = null;
            if (!Finite(tolerance) || tolerance <= 0) { reason = "invalid position error budget"; return false; }
            var tests = new List<PoseTest> { new PoseTest() };
            var uses = Clips(opt).Concat(opt.simplificationSettings.validationClips.Where(c => c != null).Select(c => new ClipUse { root = opt.transform, clip = c }))
                .GroupBy(u => (u.root, u.clip)).Select(g => g.First()).ToList();
            foreach (var use in uses)
                foreach (float time in PoseSampleTimes(use.clip))
                    tests.Add(new PoseTest { clip = use.clip, root = AnimationUtility.CalculateTransformPath(use.root, opt.transform), time = time });
            var weighted = ReadWeights(source.sharedMesh).SelectMany(w => w).Select(w => w.boneIndex).Distinct().ToArray();
            foreach (int bone in weighted)
                foreach (var rotation in new[] { new Vector3(30,0,0), new Vector3(-30,0,0), new Vector3(0,30,0), new Vector3(0,-30,0), new Vector3(0,0,30), new Vector3(0,0,-30) })
                    tests.Add(new PoseTest { bone = bone, rotation = rotation });
            for (int shape = 0; shape < source.sharedMesh.blendShapeCount; shape++)
                for (int frame = 0; frame < source.sharedMesh.GetBlendShapeFrameCount(shape); frame++)
                    tests.Add(new PoseTest { shape = shape, weight = source.sharedMesh.GetBlendShapeFrameWeight(shape, frame) });
            if (tests.Count > opt.simplificationSettings.poseValidationBudget) { reason = $"needs {tests.Count} poses; budget is {opt.simplificationSettings.poseValidationBudget} (not silently truncated)"; return false; }
            long comparisons = sameTopology ? (long)source.sharedMesh.vertexCount * tests.Count :
                (long)tests.Count * (source.sharedMesh.vertexCount * (long)candidate.triangles.Length / 3 + candidate.vertexCount * (long)source.sharedMesh.triangles.Length / 3);
            if (comparisons > opt.simplificationSettings.maximumValidationComparisons) { reason = "surface validation comparison budget exceeded"; return false; }
            if (!sameTopology && (!AttributesWithin(source.sharedMesh, candidate, opt.simplificationSettings) ||
                !AttributesWithin(candidate, source.sharedMesh, opt.simplificationSettings)))
            { reason = "UV/color/tangent deviation exceeds attribute budget"; return false; }
            var staging = new GameObject("Optimizer pose validation");
            staging.SetActive(false);
            GameObject clone = null;
            var originalMesh = new Mesh(); var reducedMesh = new Mesh();
            var previousSkinWeights = QualitySettings.skinWeights;
            try
            {
                // Validate the full imported stream, even when the current editor quality
                // setting would normally truncate skinning to one, two, or four influences.
                QualitySettings.skinWeights = SkinWeights.Unlimited;
                clone = UnityEngine.Object.Instantiate(opt.gameObject, staging.transform);
                var target = clone.transform.Find(AnimationUtility.CalculateTransformPath(source.transform, opt.transform));
                var renderer = target != null ? target.GetComponent<SkinnedMeshRenderer>() : clone.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) { reason = "pose renderer missing"; return false; }
                renderer.quality = SkinQuality.Auto;
                var transforms = clone.GetComponentsInChildren<Transform>(true);
                var positions = transforms.Select(t => t.localPosition).ToArray();
                var rotations = transforms.Select(t => t.localRotation).ToArray();
                var scales = transforms.Select(t => t.localScale).ToArray();
                var defaults = Enumerable.Range(0, source.sharedMesh.blendShapeCount).Select(source.GetBlendShapeWeight).ToArray();
                foreach (var test in tests)
                {
                    for (int i = 0; i < transforms.Length; i++) { transforms[i].localPosition = positions[i]; transforms[i].localRotation = rotations[i]; transforms[i].localScale = scales[i]; }
                    renderer.sharedMesh = source.sharedMesh;
                    for (int i = 0; i < defaults.Length; i++) renderer.SetBlendShapeWeight(i, defaults[i]);
                    if (test.clip != null)
                    {
                        var owner = string.IsNullOrEmpty(test.root) ? clone.transform : clone.transform.Find(test.root);
                        if (owner == null) { reason = "clip owner missing"; return false; }
                        test.clip.SampleAnimation(owner.gameObject, test.time);
                        if (renderer.sharedMesh != source.sharedMesh) { reason = "mesh is swapped by animation"; return false; }
                    }
                    if (test.bone >= 0)
                    {
                        if (test.bone >= renderer.bones.Length || renderer.bones[test.bone] == null) { reason = "invalid bone"; return false; }
                        renderer.bones[test.bone].localRotation *= Quaternion.Euler(test.rotation);
                    }
                    if (test.shape >= 0) renderer.SetBlendShapeWeight(test.shape, test.weight);
                    renderer.BakeMesh(originalMesh);
                    var evaluatedWeights = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
                    renderer.sharedMesh = candidate;
                    for (int i = 0; i < evaluatedWeights.Length; i++) renderer.SetBlendShapeWeight(i, evaluatedWeights[i]);
                    renderer.BakeMesh(reducedMesh);
                    bool valid = sameTopology ? originalMesh.vertexCount == reducedMesh.vertexCount && originalMesh.vertices.Zip(reducedMesh.vertices, (a,b) => (a-b).magnitude).All(d => Finite(d) && d <= tolerance) :
                        SurfaceWithin(originalMesh, reducedMesh, tolerance, opt.simplificationSettings.maximumNormalErrorDegrees) &&
                        SurfaceWithin(reducedMesh, originalMesh, tolerance, opt.simplificationSettings.maximumNormalErrorDegrees);
                    if (!valid) { reason = "sampled pose exceeds geometric/normal error budget"; return false; }
                }
                return true;
            }
            finally
            {
                QualitySettings.skinWeights = previousSkinWeights;
                UnityEngine.Object.DestroyImmediate(staging); UnityEngine.Object.DestroyImmediate(originalMesh); UnityEngine.Object.DestroyImmediate(reducedMesh);
            }
        }

        private static IEnumerable<float> PoseSampleTimes(AnimationClip clip)
        {
            var keys = AnimationUtility.GetCurveBindings(clip)
                .SelectMany(b => AnimationUtility.GetEditorCurve(clip, b).keys.Select(k => k.time))
                .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)
                    .SelectMany(b => AnimationUtility.GetObjectReferenceCurve(clip, b).Select(k => k.time)))
                .Concat(new[] { 0f, clip.length }).Where(Finite).Distinct().OrderBy(t => t).ToArray();
            return keys.Concat(keys.Zip(keys.Skip(1), (a, b) => (a + b) * 0.5f)).Distinct();
        }

        internal static bool SurfaceWithin(Mesh from, Mesh to, float tolerance, float normalAngle)
        {
            var a = from.vertices; var b = to.vertices; var na = from.normals; var nb = to.normals;
            for (int slot = 0; slot < from.subMeshCount; slot++)
            {
                var indices = to.GetTriangles(slot);
                foreach (int index in from.GetTriangles(slot).Distinct())
                {
                    float best = float.PositiveInfinity; Vector3 normal = Vector3.zero;
                    for (int tri = 0; tri < indices.Length; tri += 3)
                    {
                        int i=indices[tri], j=indices[tri+1], k=indices[tri+2];
                        var closest = ClosestPoint(a[index], b[i], b[j], b[k]);
                        float distance = (a[index] - closest).sqrMagnitude;
                        if (distance < best)
                        {
                            best = distance;
                            var bary = Barycentric(closest, b[i], b[j], b[k]);
                            normal = nb.Length == b.Length ? (nb[i]*bary.x+nb[j]*bary.y+nb[k]*bary.z).normalized : Vector3.Cross(b[j]-b[i],b[k]-b[i]).normalized;
                        }
                    }
                    if (!Finite(best) || best > tolerance*tolerance || (na.Length == a.Length && Vector3.Angle(na[index],normal) > normalAngle)) return false;
                }
            }
            return true;
        }

        internal static bool AttributesWithin(Mesh from, Mesh to, AvatarOptimizer.SimplificationSettings settings)
        {
            if (!Finite(settings.maximumUvError) || settings.maximumUvError < 0 ||
                !Finite(settings.maximumColorError) || settings.maximumColorError < 0) return false;
            var fromUvs = new List<Vector4>[8]; var toUvs = new List<Vector4>[8];
            for (int channel = 0; channel < 8; channel++)
            {
                from.GetUVs(channel, fromUvs[channel] = new List<Vector4>());
                to.GetUVs(channel, toUvs[channel] = new List<Vector4>());
                if ((fromUvs[channel].Count == 0) != (toUvs[channel].Count == 0)) return false;
            }
            var a = from.vertices; var b = to.vertices;
            var ca = from.colors; var cb = to.colors; var ta = from.tangents; var tb = to.tangents;
            if ((ca.Length == 0) != (cb.Length == 0) || (ta.Length == 0) != (tb.Length == 0)) return false;
            for (int slot = 0; slot < from.subMeshCount; slot++)
            {
                var triangles = to.GetTriangles(slot);
                foreach (int vertex in from.GetTriangles(slot).Distinct())
                {
                    float best = float.PositiveInfinity; int triangle = -1; Vector3 bary = Vector3.zero;
                    for (int t = 0; t < triangles.Length; t += 3)
                    {
                        var p = b[triangles[t]]; var q = b[triangles[t+1]]; var r = b[triangles[t+2]];
                        var point = ClosestPoint(a[vertex], p, q, r);
                        float distance = (a[vertex] - point).sqrMagnitude;
                        if (distance < best) { best = distance; triangle = t; bary = Barycentric(point, p, q, r); }
                    }
                    if (triangle < 0 || !Finite(best)) return false;
                    int i = triangles[triangle], j = triangles[triangle+1], k = triangles[triangle+2];
                    for (int channel = 0; channel < 8; channel++)
                        if (fromUvs[channel].Count > 0)
                        {
                            float difference = (fromUvs[channel][vertex] - (toUvs[channel][i]*bary.x + toUvs[channel][j]*bary.y + toUvs[channel][k]*bary.z)).magnitude;
                            if (!Finite(difference) || difference > settings.maximumUvError) return false;
                        }
                    if (ca.Length > 0)
                    {
                        float difference = ((Vector4)ca[vertex] - (Vector4)(cb[i]*bary.x + cb[j]*bary.y + cb[k]*bary.z)).magnitude;
                        if (!Finite(difference) || difference > settings.maximumColorError) return false;
                    }
                    if (ta.Length > 0)
                    {
                        var tangent = tb[i]*bary.x + tb[j]*bary.y + tb[k]*bary.z;
                        float angle = Vector3.Angle((Vector3)ta[vertex], (Vector3)tangent);
                        if (!Finite(angle) || angle > settings.maximumNormalErrorDegrees || Mathf.Sign(ta[vertex].w) != Mathf.Sign(tangent.w)) return false;
                    }
                }
            }
            return true;
        }

        private static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b-a; var ac = c-a; var ap = p-a;
            float aa = Vector3.Dot(ab,ab), bb = Vector3.Dot(ac,ac), abac = Vector3.Dot(ab,ac);
            float denominator = aa*bb-abac*abac;
            if (Math.Abs(denominator) < 1e-20f) return new Vector3(1,0,0);
            float v = (bb*Vector3.Dot(ap,ab)-abac*Vector3.Dot(ap,ac))/denominator;
            float w = (aa*Vector3.Dot(ap,ac)-abac*Vector3.Dot(ap,ab))/denominator;
            return new Vector3(1-v-w,v,w);
        }

        internal static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab=b-a; var ac=c-a; var ap=p-a;
            float d1=Vector3.Dot(ab,ap), d2=Vector3.Dot(ac,ap);
            if (d1<=0 && d2<=0) return a;
            var bp=p-b; float d3=Vector3.Dot(ab,bp), d4=Vector3.Dot(ac,bp);
            if (d3>=0 && d4<=d3) return b;
            float vc=d1*d4-d3*d2;
            if (vc<=0 && d1>=0 && d3<=0) return a+ab*(d1/(d1-d3));
            var cp=p-c; float d5=Vector3.Dot(ab,cp), d6=Vector3.Dot(ac,cp);
            if (d6>=0 && d5<=d6) return c;
            float vb=d5*d2-d1*d6;
            if (vb<=0 && d2>=0 && d6<=0) return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;
            if (va<=0 && d4-d3>=0 && d5-d6>=0) return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
            float denominator=va+vb+vc;
            if (Math.Abs(denominator)<1e-20f) return a;
            return a+ab*(vb/denominator)+ac*(vc/denominator);
        }
        private static bool LegacyStandardUvLayout(Material material) => material != null && material.shader == Shader.Find("Standard") &&
            material.GetFloat("_Mode") == 0 && material.renderQueue < 2450 &&
            (!material.HasProperty("_UVSec") || material.GetFloat("_UVSec") == 0) && !material.IsKeywordEnabled("_PARALLAXMAP");

        private static HashSet<string> PropertyNames(string names) => new HashSet<string>((names ?? "").Split(',')
            .Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.Ordinal);

        internal static void SurfaceTextureUsage(AvatarOptimizer opt, Material material, bool packing,
            out HashSet<string> uv0, out HashSet<string> independent, out HashSet<string> unused)
        {
            uv0 = new HashSet<string>(); independent = new HashSet<string>(); unused = new HashSet<string>();
            if (material == null || material.shader == null) throw new InvalidOperationException("missing material/shader");
            var profile = (opt.surfaceSettings.shaderProfiles ?? Array.Empty<AvatarOptimizer.SurfaceShaderProfile>())
                .FirstOrDefault(p => p != null && p.shader == material.shader);
            if (profile != null)
            {
                uv0 = PropertyNames(profile.uv0Textures); independent = PropertyNames(profile.independentTextures);
                foreach (var rule in profile.disabledTextureRules ?? Array.Empty<AvatarOptimizer.DisabledTextureRule>())
                    if (rule != null && !string.IsNullOrEmpty(rule.textureProperty) && !string.IsNullOrEmpty(rule.featureProperty) &&
                        material.HasProperty(rule.textureProperty) && material.HasProperty(rule.featureProperty) &&
                        material.GetFloat(rule.featureProperty) == rule.disabledValue)
                        unused.Add(rule.textureProperty);
            }
            else if (material.shader.name == "lilToon" || material.shader.name == "Hidden/lilToonCutout" || material.shader.name == "Hidden/lilToonTransparent")
            {
                // Audited against lilToon 2.3.4 lil_common_frag.hlsl. Unknown maps remain
                // unclassified instead of being guessed from conventional property names.
                uv0 = PropertyNames("_MainTex,_MainColorAdjustMask,_BumpMap,_ShadowStrengthMask,_ShadowBorderMask,_ShadowBlurMask");
                independent.Add("_MainGradationTex"); // sampled by color, not mesh UV
                var shadowColors = PropertyNames("_ShadowColorTex,_Shadow2ndColorTex,_Shadow3rdColorTex");
                if (material.HasProperty("_ShadowColorType") && material.GetFloat("_ShadowColorType") == 1) independent.UnionWith(shadowColors);
                else uv0.UnionWith(shadowColors);
                if (material.HasProperty("_UseBumpMap") && material.GetFloat("_UseBumpMap") == 0) unused.Add("_BumpMap");
                if (material.HasProperty("_UseShadow") && material.GetFloat("_UseShadow") == 0)
                    unused.UnionWith(PropertyNames("_ShadowStrengthMask,_ShadowBorderMask,_ShadowBlurMask,_ShadowColorTex,_Shadow2ndColorTex,_Shadow3rdColorTex"));
                if (packing)
                {
                    foreach (string feature in new[] { "_UseMain2ndTex", "_UseMain3rdTex", "_UseParallax", "_UseAudioLink", "_UseGlitter", "_ShiftBackfaceUV" })
                        if (material.HasProperty(feature) && material.GetFloat(feature) != 0)
                            throw new InvalidOperationException("lilToon UV packing needs a custom profile for active " + feature);
                    if (material.HasProperty("_MainTex_ScrollRotate") && material.GetVector("_MainTex_ScrollRotate") != Vector4.zero)
                        throw new InvalidOperationException("lilToon main texture uses animated/rotated UVs");
                    if (material.HasProperty("_DissolveParams") && material.GetVector("_DissolveParams").x != 0)
                        throw new InvalidOperationException("lilToon dissolve coordinates require a custom profile");
                    foreach (string lod in new[] { "_ShadowStrengthMaskLOD", "_ShadowBorderMaskLOD", "_ShadowBlurMaskLOD" })
                        if (material.HasProperty(lod) && material.GetFloat(lod) != 0)
                            throw new InvalidOperationException("lilToon explicit shadow-mask derivatives require a custom profile");
                }
            }
            else if (LegacyStandardUvLayout(material))
            {
                uv0.UnionWith(material.GetTexturePropertyNames());
                foreach (var pair in new[] { ("_BumpMap", "_NORMALMAP"), ("_DetailNormalMap", "_DETAIL_MULX2"),
                    ("_DetailAlbedoMap", "_DETAIL_MULX2"), ("_EmissionMap", "_EMISSION"), ("_MetallicGlossMap", "_METALLICGLOSSMAP") })
                    if (!material.IsKeywordEnabled(pair.Item2)) unused.Add(pair.Item1);
            }
            else throw new InvalidOperationException("shader '" + material.shader.name + "' needs a verified surface texture profile");
            if (uv0.Overlaps(independent)) throw new InvalidOperationException("shader profile classifies a texture both as UV0 and independent");
            if (packing)
                foreach (string property in material.GetTexturePropertyNames())
                    if (material.GetTexture(property) != null && !uv0.Contains(property) && !independent.Contains(property) && !unused.Contains(property))
                        throw new InvalidOperationException("texture '" + property + "' has no verified UV mapping for shader '" + material.shader.name + "'");
        }

        private sealed class Island
        {
            public int[] vertices;
            public Rect source;
            public RectInt destination;
            public int width, height;
        }
        private sealed class SurfaceChange
        {
            public SkinnedMeshRenderer renderer;
            public Mesh original, mesh;
            public Material[] materials;
            public Dictionary<int, Dictionary<Material, Material>> replacements = new Dictionary<int, Dictionary<Material, Material>>();
            public List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        }

        internal static void ProcessSurfaces(AvatarOptimizer opt, Action<string> log, Action<Func<AnimationClip, AnimationClip>> rewrite)
        {
            var settings = opt.surfaceSettings;
            if (opt.reportOnly || (!settings.packUvIslands && !settings.atlasMaterialSwaps && !settings.removeUnusedTextures)) return;
            var changes = new List<SurfaceChange>();
            // Controller replacement changes the live inventory. Keep the original ownership
            // snapshot for every controller participating in this transaction.
            var clipUses = Clips(opt).ToArray();
            foreach (var renderer in opt.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                var change = new SurfaceChange { renderer = renderer, original = renderer.sharedMesh, materials = renderer.sharedMaterials.ToArray() };
                try
                {
                    if (Tagged(opt, renderer.transform)) throw new InvalidOperationException("Advanced Tagging boundary");
                    if (ComponentDependency(opt, renderer) is string dependency) throw new InvalidOperationException(dependency);
                    var mesh = renderer.sharedMesh;
                    if (!mesh.isReadable || mesh.uv.Length != mesh.vertexCount || mesh.subMeshCount != change.materials.Length)
                        throw new InvalidOperationException("UV0, readability, or material-slot layout unsupported");
                    var families = change.materials.Select(m => new HashSet<Material>(m != null ? new[] {m} : Array.Empty<Material>())).ToArray();
                    var swapped = new HashSet<int>();
                    foreach (var use in Clips(opt))
                    {
                        foreach (var binding in use.FloatBindings)
                            if (Target(use, binding) == renderer.transform && (binding.propertyName.Contains("material.") || binding.propertyName.Contains("m_Materials")))
                                throw new InvalidOperationException("animated material properties/texture transforms");
                        foreach (var binding in use.ObjectBindings)
                        {
                            if (Target(use, binding) != renderer.transform) continue;
                            var match = System.Text.RegularExpressions.Regex.Match(binding.propertyName, @"^m_Materials\.Array\.data\[(\d+)\]$");
                            if (!match.Success)
                            {
                                if (binding.propertyName.Contains("material") || binding.propertyName.Contains("m_Mesh")) throw new InvalidOperationException("texture or mesh object-reference animation");
                                continue;
                            }
                            int slot = int.Parse(match.Groups[1].Value);
                            if (slot >= families.Length) throw new InvalidOperationException("invalid animated material slot");
                            swapped.Add(slot);
                            foreach (var key in AnimationUtility.GetObjectReferenceCurve(use.clip, binding))
                            {
                                if (key.value != null && !(key.value is Material)) throw new InvalidOperationException("non-material slot keyframe");
                                if (key.value is Material alternative) families[slot].Add(alternative);
                            }
                        }
                    }
                    if (swapped.Count > 1) throw new InvalidOperationException("multiple independent swap slots are not supported");
                    if (families.Any(f => f.Count > settings.maximumSwapVariants)) throw new InvalidOperationException("swap variant budget exceeded");
                    change.mesh = UnityEngine.Object.Instantiate(mesh);
                    change.assets.Add(change.mesh);
                    change.mesh.name = mesh.name + "_IslandLayout";
                    var uv = mesh.uv;
                    var vertexOwners = new Dictionary<int, int>();
                    for (int slot = 0; slot < mesh.subMeshCount; slot++)
                    {
                        if (mesh.GetTopology(slot) != MeshTopology.Triangles) throw new InvalidOperationException("non-triangle topology");
                        foreach (int index in mesh.GetTriangles(slot))
                        {
                            if (vertexOwners.TryGetValue(index, out int owner) && owner != slot) throw new InvalidOperationException("vertices shared between material slots need explicit duplication");
                            vertexOwners[index] = slot;
                        }
                    }
                    for (int slot = 0; slot < families.Length; slot++)
                    {
                        var family = families[slot];
                        if (family.Count == 0) continue;
                        if (family.Any(m => !SameMaterialState(family.First(), m))) throw new InvalidOperationException("variant material render/property states differ");
                        bool pack = swapped.Contains(slot) ? settings.atlasMaterialSwaps : settings.packUvIslands;
                        var uvProperties = new HashSet<string>();
                        HashSet<string> unusedEverywhere = null;
                        foreach (var material in family)
                        {
                            SurfaceTextureUsage(opt, material, pack, out var mapped, out _, out var unused);
                            uvProperties.UnionWith(mapped);
                            if (unusedEverywhere == null) unusedEverywhere = unused;
                            else unusedEverywhere.IntersectWith(unused);
                        }
                        var map = new Dictionary<Material, Material>();
                        foreach (var source in family)
                        {
                            var copy = new Material(source) { name = source.name + "_SurfaceCopy" };
                            change.assets.Add(copy); map[source] = copy;
                        }
                        if (settings.removeUnusedTextures)
                        {
                            foreach (var property in unusedEverywhere)
                                foreach (var copy in map.Values) if (copy.HasProperty(property)) copy.SetTexture(property, null);
                        }
                        if (pack)
                        {
                            var properties = uvProperties.Where(p => !unusedEverywhere.Contains(p) && map.Values.Any(m => m.HasProperty(p) && m.GetTexture(p) != null)).ToArray();
                            foreach (var property in properties)
                                foreach (var material in map.Values)
                                {
                                    if (material.GetTexture(property) == null) throw new InvalidOperationException("variant missing a texture map");
                                    if (!(material.GetTexture(property) is Texture2D texture) || texture.wrapMode != TextureWrapMode.Clamp ||
                                        material.GetTextureScale(property) != Vector2.one || material.GetTextureOffset(property) != Vector2.zero)
                                        throw new InvalidOperationException("tiling, non-2D texture, or texture transform unsupported");
                                }
                            if (properties.Length == 0) throw new InvalidOperationException("no textures to pack");
                            int width = map.Values.Max(m => properties.Max(p => m.GetTexture(p).width));
                            int height = map.Values.Max(m => properties.Max(p => m.GetTexture(p).height));
                            var islands = BuildIslands(mesh.GetTriangles(slot), uv, width, height);
                            int size = LayoutIslands(islands, settings.maximumAtlasSize, settings.mipPadding);
                            if (size == 0) throw new InvalidOperationException("island rectangles exceed atlas limit at source texel density");
                            // Compare unique source allocation to all generated variants/properties, using conservative RGBA32+mips.
                            long sourceBytes = map.Values.SelectMany(m => properties.Select(p => (Texture2D)m.GetTexture(p))).Distinct()
                                .Sum(t => (long)UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t));
                            long outputBytes = (long)size * size * 4 * 4 / 3 * map.Count * properties.Length;
                            if (outputBytes >= sourceBytes) throw new InvalidOperationException("packing would not reduce unique texture allocation");
                            foreach (var material in map.Values)
                                foreach (var property in properties)
                                {
                                    var texture = (Texture2D)material.GetTexture(property);
                                    var atlas = BuildIslandAtlas(texture, islands, size, settings.mipPadding);
                                    change.assets.Add(atlas);
                                    material.SetTexture(property, atlas);
                                }
                            foreach (var island in islands)
                                foreach (int vertex in island.vertices)
                                {
                                    var point = uv[vertex];
                                    uv[vertex] = new Vector2((island.destination.x + (point.x - island.source.x) / island.source.width * island.width) / size,
                                        (island.destination.y + (point.y - island.source.y) / island.source.height * island.height) / size);
                                }
                        }
                        change.replacements[slot] = map;
                        if (change.materials[slot] != null) change.materials[slot] = map[change.materials[slot]];
                    }
                    change.mesh.uv = uv;
                    changes.Add(change);
                }
                catch (Exception ex)
                {
                    foreach (var asset in change.assets) UnityEngine.Object.DestroyImmediate(asset);
                    log($"[AvatarOptimizer] Surface optimization skipped {renderer.name}: {ex.Message}");
                }
            }
            try
            {
                // Rebind before assigning meshes: failures do not leave UVs changed with old swap clips.
                rewrite(source =>
                {
                    AnimationClip copy = null;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(binding.propertyName, @"^m_Materials\.Array\.data\[(\d+)\]$");
                        if (!match.Success) continue;
                        int slot = int.Parse(match.Groups[1].Value);
                        var uses = clipUses.Where(u => u.clip == source).ToArray();
                        // One shared clip used at multiple roots must get the same material mapping in every context.
                        var maps = uses.Select(use => changes.FirstOrDefault(c => Target(use, binding) == c.renderer.transform))
                            .Select(c => c != null && c.replacements.TryGetValue(slot, out var map) ? map : null).ToArray();
                        if (maps.All(m => m == null)) continue;
                        if (maps.Any(m => m == null) || maps.Any(m => !ReferenceEquals(m, maps[0])))
                            throw new InvalidOperationException("shared clip has incompatible renderer-relative material mappings");
                        var keys = AnimationUtility.GetObjectReferenceCurve(source, binding);
                        bool changed = false;
                        for (int index = 0; index < keys.Length; index++)
                            if (keys[index].value is Material material && maps[0].TryGetValue(material, out var replacement))
                            { keys[index].value = replacement; changed = true; }
                        if (!changed) continue;
                        if (copy == null) { copy = UnityEngine.Object.Instantiate(source); copy.name = source.name + "_Surface"; }
                        AnimationUtility.SetObjectReferenceCurve(copy, binding, keys);
                    }
                    return copy;
                });
                foreach (var change in changes)
                {
                    change.renderer.sharedMesh = change.mesh;
                    change.renderer.sharedMaterials = change.materials;
                    ObjectRegistry.RegisterReplacedObject(change.original, change.mesh);
                    log($"[AvatarOptimizer] Applied fixed slot/island layout on {change.renderer.name}; all swap alternatives share the same coordinates.");
                }
            }
            catch
            {
                // Controller replacement can have committed earlier controllers. Fail the build, never serialize a partial result.
                throw;
            }
        }

        internal static bool SameMaterialState(Material a, Material b)
        {
            if (a.shader != b.shader || a.renderQueue != b.renderQueue || a.enableInstancing != b.enableInstancing ||
                a.doubleSidedGI != b.doubleSidedGI || a.globalIlluminationFlags != b.globalIlluminationFlags ||
                !a.shaderKeywords.OrderBy(k => k).SequenceEqual(b.shaderKeywords.OrderBy(k => k))) return false;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(a.shader); i++)
            {
                string name = ShaderUtil.GetPropertyName(a.shader, i);
                switch (ShaderUtil.GetPropertyType(a.shader, i))
                {
                    case ShaderUtil.ShaderPropertyType.Color: if (a.GetColor(name) != b.GetColor(name)) return false; break;
                    case ShaderUtil.ShaderPropertyType.Vector: if (a.GetVector(name) != b.GetVector(name)) return false; break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range: if (a.GetFloat(name) != b.GetFloat(name)) return false; break;
                }
            }
            return a.GetShaderPassEnabled("ShadowCaster") == b.GetShaderPassEnabled("ShadowCaster");
        }

        private static List<Island> BuildIslands(int[] triangles, Vector2[] uv, int width, int height)
        {
            var parent = Enumerable.Range(0, uv.Length).ToArray();
            Func<int,int> find = i =>
            {
                int root = i;
                while (parent[root] != root) root = parent[root];
                while (parent[i] != i) { int next = parent[i]; parent[i] = root; i = next; }
                return root;
            };
            for (int i = 0; i < triangles.Length; i += 3)
            { parent[find(triangles[i+1])] = find(triangles[i]); parent[find(triangles[i+2])] = find(triangles[i]); }
            var islands = new List<Island>();
            foreach (var group in triangles.Distinct().GroupBy(find))
            {
                var indices = group.ToArray();
                if (indices.Any(i => !Finite(uv[i].x) || !Finite(uv[i].y) || uv[i].x < 0 || uv[i].x > 1 || uv[i].y < 0 || uv[i].y > 1))
                    throw new InvalidOperationException("UVs outside the supported 0..1 clamp range");
                int x = Mathf.FloorToInt(indices.Min(i => uv[i].x) * width), y = Mathf.FloorToInt(indices.Min(i => uv[i].y) * height);
                int w = Math.Max(1, Mathf.CeilToInt(indices.Max(i => uv[i].x) * width) - x), h = Math.Max(1, Mathf.CeilToInt(indices.Max(i => uv[i].y) * height) - y);
                islands.Add(new Island { vertices = indices, source = new Rect((float)x/width, (float)y/height, (float)w/width, (float)h/height), width=w, height=h });
            }
            return islands;
        }

        private static int LayoutIslands(List<Island> islands, int maxSize, int padding)
        {
            for (int size = 32; size <= maxSize; size *= 2)
            {
                int x=padding,y=padding,row=0; bool fits=true;
                foreach (var island in islands.OrderByDescending(i => i.height))
                {
                    if (x+island.width+padding>size) { x=padding; y+=row+padding*2; row=0; }
                    if (island.width+padding*2>size || y+island.height+padding>size) {fits=false;break;}
                    island.destination=new RectInt(x,y,island.width,island.height);
                    x+=island.width+padding*2; row=Math.Max(row,island.height);
                }
                if (fits) return size;
            }
            return 0;
        }

        private static Texture2D BuildIslandAtlas(Texture2D source, List<Island> islands, int size, int padding)
        {
            bool linear = !source.isDataSRGB;
            var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous=RenderTexture.active;
            Texture2D readable=null;
            try
            {
                Graphics.Blit(source,rt); RenderTexture.active=rt;
                readable=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false,linear);
                readable.ReadPixels(new Rect(0,0,source.width,source.height),0,0); readable.Apply(false);
                var atlas=new Texture2D(size,size,TextureFormat.RGBA32,true,linear) { name=source.name+"_Islands", wrapMode=TextureWrapMode.Clamp, filterMode=source.filterMode, anisoLevel=source.anisoLevel };
                var pixels=new Color[size*size];
                foreach(var island in islands)
                    for(int y=-padding;y<island.height+padding;y++)
                        for(int x=-padding;x<island.width+padding;x++)
                        {
                            float u=island.source.x+(Mathf.Clamp(x,0,island.width-1)+0.5f)/island.width*island.source.width;
                            float v=island.source.y+(Mathf.Clamp(y,0,island.height-1)+0.5f)/island.height*island.source.height;
                            pixels[(island.destination.y+y)*size+island.destination.x+x]=readable.GetPixelBilinear(u,v);
                        }
                atlas.SetPixels(pixels); atlas.Apply(true,false); return atlas;
            }
            finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(rt); if(readable!=null) UnityEngine.Object.DestroyImmediate(readable); }
        }
    }

}
#endif
