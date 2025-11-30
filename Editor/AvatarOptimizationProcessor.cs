#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using nadena.dev.ndmf;

namespace MilchZocker.AvatarOptimizer
{
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

        private Type cvrAvatarType;
        private Type cvrFaceTrackingType;

        // ===== Buffered logging (chunked final log) =====
        private readonly StringBuilder logBuffer = new StringBuilder(8192);
        private int logIndent = 0;

        // Unity truncates huge Debug.Log messages. Keep chunks under ~12–14k chars.
        private const int MaxLogChunkSize = 12000;

        private void LogBuffered(string msg)
        {
            if (logIndent > 0) logBuffer.Append(' ', logIndent * 2);
            logBuffer.AppendLine(msg);
        }

        private void PushLogIndent() => logIndent++;
        private void PopLogIndent() { if (logIndent > 0) logIndent--; }

        private void FlushBufferedLog()
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

                // Try to split at a newline so we don't cut mid-line
                int lastNewline = full.LastIndexOf('\n', offset + len - 1, len);
                if (lastNewline > offset + 1000) // avoid tiny chunks
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

            avatar = ctx.AvatarRootObject.GetComponent(cvrAvatarType);

            if (avatar == null)
            {
                Debug.LogWarning("[AvatarOptimizer] CVRAvatar component not found! Optimizer will run but CVR-specific features will be skipped.");
            }
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

        public void Process()
        {
            var startTime = Time.realtimeSinceStartup;
            LogBuffered("[AvatarOptimizer] ========== Starting Optimization ==========");

            try
            {
                if (optimizer.boneSettings.preserveAnimatedBones)
                {
                    LogBuffered("[AvatarOptimizer] Preserve animated bones ON");
                    PushLogIndent();
                    CollectAnimatedBones();
                    CollectAnimatedTransforms();
                    PopLogIndent();
                }

                if (optimizer.blendshapeSettings.removeUnusedBlendshapes)
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

                if (optimizer.boneSettings.removeBonesWithoutWeights)
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

                if (optimizer.meshSettings.combineMeshes)
                {
                    LogBuffered("[AvatarOptimizer] Combine meshes ON");
                    PushLogIndent();
                    CombineMeshes();
                    PopLogIndent();
                }

                if (optimizer.atlasSettings.generateTextureAtlas)
                {
                    LogBuffered("[AvatarOptimizer] Generate texture atlases ON");
                    PushLogIndent();
                    GenerateTextureAtlases();
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

                LogBuffered($"[AvatarOptimizer] ========== Optimization Complete in {optimizer.stats.optimizationTimeSeconds:F2}s ==========");
                LogBuffered($"[AvatarOptimizer] Stats: {optimizer.stats.bonesRemoved} bones, {optimizer.stats.boneReferencesRemoved} bone refs, " +
                            $"{optimizer.stats.blendshapesRemoved} blendshapes, {optimizer.stats.verticesMerged} vertices merged, " +
                            $"{optimizer.stats.looseVerticesRemoved} loose vertices, {optimizer.stats.meshesCombined} meshes combined, " +
                            $"{optimizer.stats.atlasesGenerated} atlases");

                FlushBufferedLog();
            }
        }

        #region Reflection Helpers

        private object GetFieldValue(object obj, string fieldName)
        {
            if (obj == null) return null;
            var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        private object GetPropertyValue(object obj, string propertyName)
        {
            if (obj == null) return null;
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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

        #region Animated Bones Collection

        private void CollectAnimatedBones()
        {
            if (avatar == null) return;

            LogBuffered("[AvatarOptimizer] Collecting animated bones...");
            PushLogIndent();

            var overrides = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatar, "overrides");
            if (overrides != null)
            {
                CollectAnimatedBonesFromController(overrides);
            }

            var avatarUsesAdvancedSettings = GetFieldOrPropertyValue<bool>(avatar, "avatarUsesAdvancedSettings");
            if (avatarUsesAdvancedSettings)
            {
                var avatarSettings = GetFieldOrPropertyValue<object>(avatar, "avatarSettings");
                if (avatarSettings != null)
                {
                    var animator = GetFieldOrPropertyValue<AnimatorController>(avatarSettings, "animator");
                    if (animator != null)
                    {
                        CollectAnimatedBonesFromController(animator);
                    }

                    var baseController = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatarSettings, "baseController");
                    if (baseController != null)
                    {
                        CollectAnimatedBonesFromController(baseController);
                    }
                }
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
            }
        }

        private void CollectAnimatedTransforms()
        {
            if (avatar == null) return;

            LogBuffered("[AvatarOptimizer] Collecting animated transform properties...");
            PushLogIndent();

            var overrides = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatar, "overrides");
            if (overrides != null)
            {
                CollectAnimatedTransformPropertiesFromController(overrides);
            }

            var avatarUsesAdvancedSettings = GetFieldOrPropertyValue<bool>(avatar, "avatarUsesAdvancedSettings");
            if (avatarUsesAdvancedSettings)
            {
                var avatarSettings = GetFieldOrPropertyValue<object>(avatar, "avatarSettings");
                if (avatarSettings != null)
                {
                    var animator = GetFieldOrPropertyValue<AnimatorController>(avatarSettings, "animator");
                    if (animator != null)
                    {
                        CollectAnimatedTransformPropertiesFromController(animator);
                    }

                    var baseController = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatarSettings, "baseController");
                    if (baseController != null)
                    {
                        CollectAnimatedTransformPropertiesFromController(baseController);
                    }
                }
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

        // Unchanged from your base
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

            if (settings.scanAdvancedAvatarSettings && avatar != null)
            {
                var avatarUsesAdvancedSettings = GetFieldOrPropertyValue<bool>(avatar, "avatarUsesAdvancedSettings");
                if (avatarUsesAdvancedSettings)
                {
                    var avatarSettings = GetFieldOrPropertyValue<object>(avatar, "avatarSettings");
                    if (avatarSettings != null)
                    {
                        var animator = GetFieldOrPropertyValue<AnimatorController>(avatarSettings, "animator");
                        if (animator != null)
                        {
                            CollectBlendshapesFromAnimator(animator);
                        }

                        var baseController = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatarSettings, "baseController");
                        if (baseController is AnimatorController baseAnimator)
                        {
                            CollectBlendshapesFromAnimator(baseAnimator);
                        }
                    }
                }
            }

            if (settings.scanOverrideController && avatar != null)
            {
                var overrides = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatar, "overrides");
                if (overrides is AnimatorController overrideController)
                {
                    CollectBlendshapesFromAnimator(overrideController);
                }
                else if (overrides is AnimatorOverrideController overrideCtrl)
                {
                    CollectBlendshapesFromAnimator(overrideCtrl);
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

            if (controller is AnimatorController animController)
            {
                foreach (var layer in animController.layers)
                {
                    CollectClipsFromStateMachine(layer.stateMachine, clips);
                }
            }
            else if (controller is AnimatorOverrideController overrideController)
            {
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrideController.GetOverrides(overrides);

                foreach (var pair in overrides)
                {
                    if (pair.Value != null) clips.Add(pair.Value);
                }
            }

            return clips;
        }

        private void CollectClipsFromStateMachine(AnimatorStateMachine stateMachine, HashSet<AnimationClip> clips)
        {
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

                    if (!ShouldPreserveBlendshape(name) || IsZeroDeltaBlendshape(mesh, i))
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
                    var newMesh = CopyMeshWithoutBlendshapes(mesh, blendshapesToRemove);
                    smr.sharedMesh = newMesh;
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

        private Mesh CopyMeshWithoutBlendshapes(Mesh sourceMesh, List<int> blendshapesToRemove)
        {
            var newMesh = new Mesh();
            newMesh.name = sourceMesh.name + "_Optimized";
            newMesh.vertices = sourceMesh.vertices;
            newMesh.triangles = sourceMesh.triangles;
            newMesh.normals = sourceMesh.normals;
            newMesh.tangents = sourceMesh.tangents;
            newMesh.uv = sourceMesh.uv;
            newMesh.uv2 = sourceMesh.uv2;
            newMesh.uv3 = sourceMesh.uv3;
            newMesh.uv4 = sourceMesh.uv4;
            newMesh.colors = sourceMesh.colors;
            newMesh.boneWeights = sourceMesh.boneWeights;
            newMesh.bindposes = sourceMesh.bindposes;
            newMesh.subMeshCount = sourceMesh.subMeshCount;

            for (int i = 0; i < sourceMesh.subMeshCount; i++)
            {
                newMesh.SetSubMesh(i, sourceMesh.GetSubMesh(i));
            }

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

        #endregion

        #region Bone Optimization

        // Unchanged from your base
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

        // Unchanged from your base
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

            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.sharedMesh == null || !ShouldProcessMesh(smr)) continue;

                var mesh = GetOrCreateMeshCopy(smr, "_MeshOptimized");
                if (mesh == null) continue;

                bool modified = false;
                bool hasSkinnedData = mesh.boneWeights != null && mesh.boneWeights.Length > 0;

                if (settings.mergeVerticesByDistance)
                {
                    if (!hasSkinnedData)
                    {
                        int beforeCount = mesh.vertexCount;
                        MergeVertices(mesh, settings);
                        int merged = beforeCount - mesh.vertexCount;
                        totalVerticesMerged += merged;

                        if (merged > 0)
                        {
                            modified = true;
                            LogBuffered($"[AvatarOptimizer] Merged {merged} vertices in {smr.name}");
                        }
                    }
                    else
                    {
                        LogBuffered($"[AvatarOptimizer] Skipping vertex merge for {smr.name} (skinned mesh - would cause crash)");
                    }
                }

                if (settings.deleteLooseVertices)
                {
                    int removed = RemoveLooseVertices(mesh);
                    totalLooseRemoved += removed;

                    if (removed > 0)
                    {
                        modified = true;
                        LogBuffered($"[AvatarOptimizer] Removed {removed} loose vertices in {smr.name}");
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

                if (settings.optimizeMeshForRendering)
                {
                    mesh.Optimize();
                    modified = true;
                }

                if (settings.applyMeshCompression)
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

                if (modified && !hasSkinnedData)
                {
                    mesh.RecalculateBounds();
                }
            }

            optimizer.stats.verticesMerged = totalVerticesMerged;
            optimizer.stats.looseVerticesRemoved = totalLooseRemoved;

            PopLogIndent();
            LogBuffered("[AvatarOptimizer] --- OptimizeMeshes END ---");
        }

        private void MergeVertices(Mesh mesh, AvatarOptimizer.MeshOptimizationSettings settings)
        {
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

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var triangles = mesh.GetTriangles(i);
                for (int j = 0; j < triangles.Length; j++)
                {
                    triangles[j] = vertexMap[triangles[j]];
                }
                mesh.SetTriangles(triangles, i);
            }

            mesh.vertices = newVertices.ToArray();
            if (newNormals.Count > 0) mesh.normals = newNormals.ToArray();
            if (newTangents.Count > 0) mesh.tangents = newTangents.ToArray();
            if (newUV.Count > 0) mesh.uv = newUV.ToArray();
            if (newColors.Count > 0) mesh.colors = newColors.ToArray();
            if (newBoneWeights.Count > 0) mesh.boneWeights = newBoneWeights.ToArray();
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

                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var triangles = mesh.GetTriangles(i);
                    for (int j = 0; j < triangles.Length; j++)
                    {
                        triangles[j] = oldToNew[triangles[j]];
                    }
                    mesh.SetTriangles(triangles, i);
                }

                mesh.vertices = newVertices.ToArray();
                if (newNormals.Count > 0) mesh.normals = newNormals.ToArray();
                if (newTangents.Count > 0) mesh.tangents = newTangents.ToArray();
                if (newUV.Count > 0) mesh.uv = newUV.ToArray();
                if (newColors.Count > 0) mesh.colors = newColors.ToArray();
                if (newBoneWeights.Count > 0) mesh.boneWeights = newBoneWeights.ToArray();
            }

            return removed;
        }

        #endregion

        #region Mesh Combining

        // Unchanged from your base
        private void CombineMeshes()
        {
            LogBuffered("[AvatarOptimizer] --- CombineMeshes START ---");
            PushLogIndent();

            var skinnedMeshRenderers = context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var combinableGroups = GroupCombinableMeshes(skinnedMeshRenderers);

            int totalCombined = 0;

            foreach (var group in combinableGroups)
            {
                if (group.Count < 2) continue;

                LogBuffered($"[AvatarOptimizer] Combining {group.Count} meshes with material '{group[0].sharedMaterials[0].name}'");

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
            var groups = new List<List<SkinnedMeshRenderer>>();
            var processed = new HashSet<SkinnedMeshRenderer>();

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

                    if (CanCombineTogether(smr, other))
                    {
                        group.Add(other);
                        processed.Add(other);
                    }
                }

                if (group.Count >= 2)
                {
                    groups.Add(group);
                }
            }

            return groups;
        }

        private bool CanCombineMesh(SkinnedMeshRenderer smr)
        {
            if (animatedTransformProperties.ContainsKey(smr.transform))
            {
                var props = animatedTransformProperties[smr.transform];
                if (props.Any(p => !p.StartsWith("blendShape.")))
                {
                    return false;
                }
            }

            return true;
        }

        private bool CanCombineTogether(SkinnedMeshRenderer smr1, SkinnedMeshRenderer smr2)
        {
            if (smr1.sharedMaterials.Length != smr2.sharedMaterials.Length)
                return false;

            for (int i = 0; i < smr1.sharedMaterials.Length; i++)
            {
                if (smr1.sharedMaterials[i] != smr2.sharedMaterials[i])
                    return false;
            }

            if (smr1.rootBone != smr2.rootBone)
                return false;

            return true;
        }

        private void CombineMeshGroup(List<SkinnedMeshRenderer> group)
        {
            var firstRenderer = group[0];
            var combinedMesh = new Mesh();
            combinedMesh.name = "CombinedMesh_" + firstRenderer.name;

            var allBones = new List<Transform>();
            var boneToIndex = new Dictionary<Transform, int>();

            foreach (var smr in group)
            {
                if (smr.bones != null)
                {
                    foreach (var bone in smr.bones)
                    {
                        if (bone != null && !boneToIndex.ContainsKey(bone))
                        {
                            boneToIndex[bone] = allBones.Count;
                            allBones.Add(bone);
                        }
                    }
                }
            }

            var combineInstances = new List<CombineInstance>();
            var combinedBoneWeights = new List<BoneWeight>();

            foreach (var smr in group)
            {
                if (smr.sharedMesh == null) continue;

                var mesh = smr.sharedMesh;
                var localToWorld = smr.transform.localToWorldMatrix;

                var boneWeights = mesh.boneWeights;
                var remappedWeights = new BoneWeight[boneWeights.Length];

                for (int i = 0; i < boneWeights.Length; i++)
                {
                    var weight = boneWeights[i];
                    remappedWeights[i] = new BoneWeight
                    {
                        boneIndex0 = RemapBoneIndex(weight.boneIndex0, smr, boneToIndex),
                        boneIndex1 = RemapBoneIndex(weight.boneIndex1, smr, boneToIndex),
                        boneIndex2 = RemapBoneIndex(weight.boneIndex2, smr, boneToIndex),
                        boneIndex3 = RemapBoneIndex(weight.boneIndex3, smr, boneToIndex),
                        weight0 = weight.weight0,
                        weight1 = weight.weight1,
                        weight2 = weight.weight2,
                        weight3 = weight.weight3
                    };
                }

                combinedBoneWeights.AddRange(remappedWeights);

                var ci = new CombineInstance
                {
                    mesh = mesh,
                    transform = localToWorld
                };
                combineInstances.Add(ci);
            }

            combinedMesh.CombineMeshes(combineInstances.ToArray(), true, true);
            combinedMesh.boneWeights = combinedBoneWeights.ToArray();

            var bindposes = new Matrix4x4[allBones.Count];
            for (int i = 0; i < allBones.Count; i++)
            {
                bindposes[i] = allBones[i].worldToLocalMatrix * firstRenderer.transform.localToWorldMatrix;
            }
            combinedMesh.bindposes = bindposes;

            firstRenderer.sharedMesh = combinedMesh;
            firstRenderer.bones = allBones.ToArray();
            firstRenderer.rootBone = firstRenderer.rootBone;

            for (int i = 1; i < group.Count; i++)
            {
                group[i].gameObject.SetActive(false);
            }
        }

        private int RemapBoneIndex(int originalIndex, SkinnedMeshRenderer smr, Dictionary<Transform, int> boneToIndex)
        {
            if (originalIndex < 0 || originalIndex >= smr.bones.Length)
                return 0;

            var bone = smr.bones[originalIndex];
            if (bone == null || !boneToIndex.ContainsKey(bone))
                return 0;

            return boneToIndex[bone];
        }

        #endregion

        #region Texture Atlas Generation (UPDATED + DEDUPE)

        private void GenerateTextureAtlases()
        {
            LogBuffered("[AvatarOptimizer] --- GenerateTextureAtlases START ---");
            PushLogIndent();

            if (optimizer.atlasSettings.excludeAnimatedMaterials)
            {
                CollectAnimatedMaterials();
            }

            var renderers = context.AvatarRootTransform.GetComponentsInChildren<Renderer>(true);

            var materialsByShader = new Dictionary<string, List<Material>>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;

                    if (ShouldExcludeMaterialFromAtlas(mat))
                    {
                        if (optimizer.atlasSettings.verboseLogging)
                        {
                            LogBuffered($"[AvatarOptimizer] Excluding material '{mat.name}' from atlas (animated or pattern match)");
                        }
                        continue;
                    }

                    if (!ShouldIncludeShader(mat.shader))
                    {
                        if (optimizer.atlasSettings.verboseLogging)
                        {
                            LogBuffered($"[AvatarOptimizer] Excluding material '{mat.name}' - shader '{mat.shader.name}' is filtered out");
                        }
                        continue;
                    }

                    string shaderName = mat.shader.name;

                    if (!materialsByShader.ContainsKey(shaderName))
                        materialsByShader[shaderName] = new List<Material>();

                    if (!materialsByShader[shaderName].Contains(mat))
                        materialsByShader[shaderName].Add(mat);
                }
            }

            int atlasesCreated = 0;

            foreach (var kvp in materialsByShader)
            {
                if (kvp.Value.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
                {
                    if (optimizer.atlasSettings.verboseLogging)
                    {
                        LogBuffered($"[AvatarOptimizer] Skipping atlas for shader '{kvp.Key}' (only {kvp.Value.Count} materials, minimum is {optimizer.atlasSettings.minimumMaterialsForAtlas})");
                    }
                    continue;
                }

                LogBuffered($"[AvatarOptimizer] Analyzing shader: {kvp.Key} ({kvp.Value.Count} materials)");

                int generated;

                if (optimizer.atlasSettings.useEnhancedAtlasWorkflow)
                {
                    LogBuffered($"[AvatarOptimizer] Using enhanced atlas workflow for shader '{kvp.Key}'");
                    generated = GenerateAtlasWithEnhancedWorkflow(kvp.Key, kvp.Value);
                }
                else if (optimizer.atlasSettings.mergeIdenticalTextures)
                {
                    LogBuffered($"[AvatarOptimizer] Using texture merging mode for shader '{kvp.Key}'");
                    generated = GenerateAtlasWithTextureMerging(kvp.Key, kvp.Value);
                }
                else
                {
                    LogBuffered($"[AvatarOptimizer] Using standard atlas mode for shader '{kvp.Key}'");
                    generated = GenerateAtlasForShaderAutomatic(kvp.Key, kvp.Value);
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

            optimizer.stats.atlasesGenerated = atlasesCreated;
            LogBuffered($"[AvatarOptimizer] Texture atlas generation complete! Created {atlasesCreated} atlases");

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
                LogBuffered($"[AvatarOptimizer] No allowed texture properties found for shader '{shaderName}'");
                PopLogIndent();
                LogBuffered($"[AvatarOptimizer] --- GenerateAtlasWithEnhancedWorkflow END ({shaderName}) ---");
                return 0;
            }

            var atlaseableMats = materials.Where(m => IsMaterialAtlaseable(m, allowedProps)).ToList();
            if (atlaseableMats.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
            {
                LogBuffered($"[AvatarOptimizer] After filtering non-2D properties, shader '{shaderName}' has only {atlaseableMats.Count} atlaseable materials - skipping");
                PopLogIndent();
                LogBuffered($"[AvatarOptimizer] --- GenerateAtlasWithEnhancedWorkflow END ({shaderName}) ---");
                return 0;
            }

            int atlasesCreated = AtlasMaterialSubsetRecursive(shaderName, atlaseableMats, allowedProps, enhanced: true);

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] --- GenerateAtlasWithEnhancedWorkflow END ({shaderName}) ---");
            return atlasesCreated;
        }

        private int GenerateAtlasWithTextureMerging(string shaderName, List<Material> materials)
        {
            LogBuffered($"[AvatarOptimizer] Using texture merging mode for shader '{shaderName}'");

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

            LogBuffered($"[AvatarOptimizer] Found {textureHash.Count} unique texture combinations");

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

                if (allowPatterns.Contains("*"))
                {
                    return true;
                }

                bool matches = allowPatterns.Any(pattern =>
                    shaderName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                return matches;
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

                if (allowPatterns.Contains("*"))
                {
                    return true;
                }

                bool matches = allowPatterns.Any(pattern => MatchesWildcard(propertyName, pattern));

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
                return System.Text.RegularExpressions.Regex.IsMatch(text, "^" + regexPattern + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private int GenerateAtlasForShaderAutomatic(string shaderName, List<Material> materials)
        {
            LogBuffered($"[AvatarOptimizer] --- GenerateAtlasForShaderAutomatic START ({shaderName}) ---");
            PushLogIndent();

            var shader = materials[0].shader;
            var allowedProps = GetAllowedTexturePropertiesForShader(shader);

            if (allowedProps.Count == 0)
            {
                LogBuffered($"[AvatarOptimizer] No allowed texture properties found for shader '{shaderName}'");
                PopLogIndent();
                LogBuffered($"[AvatarOptimizer] --- GenerateAtlasForShaderAutomatic END ({shaderName}) ---");
                return 0;
            }

            var atlaseableMats = materials.Where(m => IsMaterialAtlaseable(m, allowedProps)).ToList();
            if (atlaseableMats.Count < optimizer.atlasSettings.minimumMaterialsForAtlas)
            {
                LogBuffered($"[AvatarOptimizer] After filtering non-2D properties, shader '{shaderName}' has only {atlaseableMats.Count} atlaseable materials - skipping");
                PopLogIndent();
                LogBuffered($"[AvatarOptimizer] --- GenerateAtlasForShaderAutomatic END ({shaderName}) ---");
                return 0;
            }

            int atlasesCreated = AtlasMaterialSubsetRecursive(shaderName, atlaseableMats, allowedProps, enhanced: false);

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] --- GenerateAtlasForShaderAutomatic END ({shaderName}) ---");

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

            LogBuffered($"[AvatarOptimizer] Allowed texture properties (shader-level): [{string.Join(", ", allowedProps)}]");
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
                    if (optimizer.atlasSettings.verboseLogging)
                        LogBuffered($"[AvatarOptimizer] Material '{mat.name}' has non-2D texture on '{prop}' ({tex.dimension}) - excluding");
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

            LogBuffered($"[AvatarOptimizer] Atlasing failed for subset ({mats.Count}) on '{shaderName}', splitting to {left.Count}+{right.Count}");

            return AtlasMaterialSubsetRecursive(shaderName, left, allowedProps, enhanced)
                 + AtlasMaterialSubsetRecursive(shaderName, right, allowedProps, enhanced);
        }

        // ---------- NEW: property dedupe by texture signature ----------
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

            if (optimizer.atlasSettings.verboseLogging && groups.Count < allowedProps.Count)
            {
                LogBuffered($"[AvatarOptimizer] Deduped properties: {allowedProps.Count} props -> {groups.Count} unique texture groups");
                foreach (var g in groups)
                    LogBuffered($"[AvatarOptimizer]   Group({g.Value.Count}): {string.Join(", ", g.Value)}");
            }

            return groups;
        }

        private bool IsNormalProperty(string propName)
        {
            return propName.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propName.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        // -------------------------------------------------------------

        private bool TryAtlasSubset(string shaderName, List<Material> mats, List<string> allowedProps, bool enhanced, out int atlasesCreated)
        {
            atlasesCreated = 0;

            Dictionary<Material, Vector2Int> refSizes = null;
            if (!enhanced)
            {
                refSizes = new Dictionary<Material, Vector2Int>();
                foreach (var mat in mats)
                {
                    int maxW = optimizer.atlasSettings.minimumTextureSize;
                    int maxH = optimizer.atlasSettings.minimumTextureSize;

                    foreach (var prop in allowedProps)
                    {
                        if (!mat.HasProperty(prop)) continue;
                        var t = mat.GetTexture(prop) as Texture2D;
                        if (t != null)
                        {
                            maxW = Mathf.Max(maxW, t.width);
                            maxH = Mathf.Max(maxH, t.height);
                        }
                    }
                    refSizes[mat] = new Vector2Int(maxW, maxH);
                }
            }

            // NEW: group properties sharing identical texture sets
            var propGroups = BuildPropertyGroups(mats, allowedProps);
            var representativeProps = allowedProps
                .Where(p => propGroups.Values.Any(list => list[0] == p))
                .ToList();

            if (representativeProps.Count == 0)
                return false;

            // Build textures only for representative properties
            var texturesByRepProp = new Dictionary<string, Texture2D[]>();

            foreach (var repProp in representativeProps)
            {
                var list = new List<Texture2D>(mats.Count);
                foreach (var mat in mats)
                {
                    Texture2D tex = null;
                    if (mat.HasProperty(repProp))
                        tex = mat.GetTexture(repProp) as Texture2D;

                    if (tex == null)
                    {
                        Vector2Int ps = enhanced
                            ? new Vector2Int(optimizer.atlasSettings.minimumTextureSize, optimizer.atlasSettings.minimumTextureSize)
                            : refSizes[mat];
                        tex = CreatePlaceholderTexture(repProp, ps.x, ps.y);
                    }

                    if (!enhanced)
                    {
                        var rs = refSizes[mat];
                        tex = EnsureSizeReadable(tex, rs.x, rs.y);
                    }
                    else
                    {
                        tex = EnsureReadable(tex);
                    }

                    list.Add(tex);
                }

                texturesByRepProp[repProp] = list.ToArray();
            }

            // Driver is first representative prop in shader order
            string driverProp = representativeProps[0];
            var driverTextures = texturesByRepProp[driverProp];

            var driverRes = ManagedAtlasPacker.PackTextures(
                driverTextures,
                (int)optimizer.atlasSettings.maxAtlasSize,
                optimizer.atlasSettings.atlasPadding,
                true);

            if (driverRes == null || driverRes.atlas == null)
            {
                LogBuffered($"[AvatarOptimizer] ✗ Failed to pack driver '{driverProp}' for subset ({mats.Count}) on '{shaderName}'");
                return false;
            }

            int atlasW = driverRes.width;
            int atlasH = driverRes.height;

            var atlasesByRep = new Dictionary<string, (Texture2D atlas, Rect[] rects)>();
            atlasesByRep[driverProp] = (driverRes.atlas, driverRes.uvRects);

            // Pack other representative props
            foreach (var repProp in representativeProps)
            {
                if (repProp == driverProp) continue;

                if (enhanced)
                {
                    var fixedAtlas = BuildAtlasFromFixedLayout(texturesByRepProp[repProp], driverRes.uvRects, atlasW, atlasH);
                    if (fixedAtlas == null)
                    {
                        LogBuffered($"[AvatarOptimizer] ✗ Failed to build fixed-layout atlas for '{repProp}' on '{shaderName}'");
                        return false;
                    }
                    atlasesByRep[repProp] = (fixedAtlas, driverRes.uvRects);
                }
                else
                {
                    var res = ManagedAtlasPacker.PackTextures(
                        texturesByRepProp[repProp],
                        (int)optimizer.atlasSettings.maxAtlasSize,
                        optimizer.atlasSettings.atlasPadding,
                        true);

                    if (res == null || res.atlas == null)
                    {
                        LogBuffered($"[AvatarOptimizer] ✗ Failed to pack atlas for '{repProp}' on '{shaderName}'");
                        return false;
                    }
                    atlasesByRep[repProp] = (res.atlas, res.uvRects);
                }
            }

            // Expand to all original props (deduped)
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

            atlasesCreated = atlasesByRep.Count; // UNIQUE atlases only
            LogBuffered($"[AvatarOptimizer] ✓ {(enhanced ? "Enhanced" : "Standard")} atlasing complete for '{shaderName}' subset ({mats.Count}) - {atlasesCreated} unique atlases ({allowedProps.Count} props)");
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

                bool multipleRects = rects.Select(r => $"{r.x:F4},{r.y:F4},{r.width:F4},{r.height:F4}").Distinct().Count() > 1;
                if (multipleRects)
                {
                    LogBuffered($"[AvatarOptimizer] Skipping enhanced baking for renderer '{renderer.name}' - multiple atlas rects across its materials.");
                    continue;
                }

                Rect rectForRenderer = rects[0];

                if (renderer is SkinnedMeshRenderer smr)
                {
                    foreach (var slot in slots)
                        BakeSubmeshUVsToRect(smr, slot, rectForRenderer);
                }
                else if (renderer is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null)
                        BakeMeshUVsToRect(mf, rectForRenderer);
                }

                for (int s = 0; s < slots.Count; s++)
                    mats[slots[s]] = masterMat;

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

        private Texture2D BuildAtlasFromFixedLayout(Texture2D[] textures, Rect[] rects, int atlasWidth, int atlasHeight)
        {
            if (textures == null || rects == null || textures.Length != rects.Length)
                return null;

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
            atlas.name = $"AtlasFixed_{atlasWidth}x{atlasHeight}";
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
                uv.x = rect.x + uv.x * rect.width;
                uv.y = rect.y + uv.y * rect.height;
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
            if (avatar == null) return;

            LogBuffered("[AvatarOptimizer] Collecting animated materials...");
            PushLogIndent();

            var settings = optimizer.atlasSettings;

            if (settings.scanOverrideController)
            {
                var overrides = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatar, "overrides");
                if (overrides != null)
                {
                    CollectAnimatedMaterialsFromController(overrides);
                }
            }

            if (settings.scanAdvancedAvatarSettings)
            {
                var avatarUsesAdvancedSettings = GetFieldOrPropertyValue<bool>(avatar, "avatarUsesAdvancedSettings");
                if (avatarUsesAdvancedSettings)
                {
                    var avatarSettings = GetFieldOrPropertyValue<object>(avatar, "avatarSettings");
                    if (avatarSettings != null)
                    {
                        var animator = GetFieldOrPropertyValue<AnimatorController>(avatarSettings, "animator");
                        if (animator != null)
                        {
                            CollectAnimatedMaterialsFromController(animator);
                        }

                        var baseController = GetFieldOrPropertyValue<RuntimeAnimatorController>(avatarSettings, "baseController");
                        if (baseController != null)
                        {
                            CollectAnimatedMaterialsFromController(baseController);
                        }
                    }
                }
            }

            PopLogIndent();
            LogBuffered($"[AvatarOptimizer] Found {animatedMaterials.Count} animated materials");

            if (settings.verboseLogging)
            {
                foreach (var mat in animatedMaterials)
                {
                    if (animatedMaterialProperties.TryGetValue(mat, out var properties))
                    {
                        LogBuffered($"[AvatarOptimizer] Animated material: '{mat.name}' - Properties: {string.Join(", ", properties)}");
                    }
                    else
                    {
                        LogBuffered($"[AvatarOptimizer] Animated material: '{mat.name}'");
                    }
                }
            }
        }

        private void CollectAnimatedMaterialsFromController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;

            foreach (var clip in GetAllAnimationClips(controller))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.propertyName.Contains("material.") || binding.propertyName.Contains("m_Materials"))
                    {
                        var target = context.AvatarRootTransform.Find(binding.path);
                        if (target != null)
                        {
                            var renderer = target.GetComponent<Renderer>();
                            if (renderer != null)
                            {
                                int materialIndex = 0;
                                if (binding.propertyName.Contains("Array.data["))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(
                                        binding.propertyName, @"Array\.data\[(\d+)\]");
                                    if (match.Success)
                                    {
                                        materialIndex = int.Parse(match.Groups[1].Value);
                                    }
                                }

                                if (materialIndex < renderer.sharedMaterials.Length)
                                {
                                    var material = renderer.sharedMaterials[materialIndex];
                                    if (material != null)
                                    {
                                        animatedMaterials.Add(material);

                                        string propertyName = ExtractPropertyName(binding.propertyName);
                                        if (!string.IsNullOrEmpty(propertyName))
                                        {
                                            if (!animatedMaterialProperties.ContainsKey(material))
                                            {
                                                animatedMaterialProperties[material] = new List<string>();
                                            }

                                            if (!animatedMaterialProperties[material].Contains(propertyName))
                                            {
                                                animatedMaterialProperties[material].Add(propertyName);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
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
            var settings = optimizer.atlasSettings;

            if (settings.excludeAnimatedMaterials && animatedMaterials.Contains(material))
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

        #endregion
    }
}
#endif
