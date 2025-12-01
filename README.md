# NDMF-Avatar-Optimization

NDMF-Avatar-Optimization is a Unity NDMF tool for ChilloutVR that automates avatar cleanup while staying **non-destructive**. The processor duplicates meshes/materials when needed and bakes changes into a clone at upload/manual bake time, so original assets stay untouched. Pair it with **NDMF-Merge** if you already use it in your NDMF stack—the optimizer runs after other NDMF steps in the same pipeline.

## What it does
- **Animation-aware safety:** Scans CVR controllers and advanced avatar settings for animated bones, transforms, blendshapes, and materials so referenced content is protected before pruning or atlasing.
- **Blendshape cleanup:** Removes unused or zero-delta blendshapes while preserving CVR blink/viseme/eye-look, face-tracking, and user-pattern matches.
- **Bone cleanup:** Prunes unused bone references and optionally removes empty bones, with checks for Magica Cloth, Dynamic Bones, VRC PhysBones, constraints, child preservation, and name patterns.
- **Mesh tuning:** Merges close vertices, deletes loose vertices, and can optionally combine compatible meshes, recalc normals/tangents, and apply compression.
- **Atlas generation:** Builds texture atlases per shader/property group with animation-aware exclusions, shader/property filtering, enhanced workflow support, and compression control.
- **Buffered reporting:** Buffers logs to avoid Unity truncation and prints chunked messages plus statistics (bones/blendshapes/vertices/meshes/atlases/time) every run.

## What it doesn’t do
- **Does not alter originals:** All changes are applied to baked clones; original meshes/materials remain intact.
- **Does not cover custom runtimes automatically:** Script-driven animations or bespoke systems are only preserved if they show up in controller/face-tracking data or your manual preserve patterns.
- **Does not fully protect Magica Cloth 1:** Mesh/bone edits can still disrupt Magica Cloth 1 data (see Known limitations).

## Requirements & installation
- Unity **2019.3+** with a compatible ChilloutVR CCK.
- **NDMF 1.4+** and **Chillaxins** installed.
- Clone or add this repository into your project (e.g., under `Packages/NDMF-Avatar-Optimization`).
- Enable local packages in **Project Settings → Package Manager** and verify the **Avatar Optimizer** component is available.

## Workflow
1. Add the **Avatar Optimizer** component to your avatar root.
2. Adjust settings (start with defaults; recommendations below).
3. Upload or **Manual Bake** the avatar. The optimizer runs automatically in the NDMF pipeline—after other NDMF tools such as **NDMF-Merge**—to generate a clone, apply changes, buffer logs, and record stats.
4. Review the baked clone—especially cloth/physics-heavy meshes—and iterate settings as needed.

## Detailed settings and recommendations
Settings map directly to serialized fields in `AvatarOptimizer` and behaviors implemented in `AvatarOptimizationProcessor.cs`.

### Bone Optimization
- **Remove unused bone references** (`removeUnusedBoneReferences`, default **on**): Rewrites skinning arrays to drop unreferenced bones per SkinnedMeshRenderer. Safe to keep enabled for most avatars.
- **Only remove zero-weight bones** (`onlyRemoveZeroWeightBones`, default **on**) with **Minimum bone weight threshold** (`minimumBoneWeightThreshold`, default **0.0001**): Treats bones with weights above the threshold as used. Raise slightly if stray weights remain; lower for maximum caution.
- **Remove bones without weights** (`removeBonesWithoutWeights`, default **off**): Identifies transforms not referenced by any mesh and reports candidates. With physics checks enabled, the processor logs Magica Cloth, Magica Cloth 2, Dynamic Bones, or VRC PhysBones before allowing removal.
- **Checks for physics components** (`checkForMagicaCloth`, `checkForDynamicBones`, `checkForVRCPhysBones`, defaults **on**): Detects common physics systems and warns when manual confirmation is required.
- **Manual confirmation per bone** (`manualConfirmationPerBone`, default **on**): Lists candidate bones with paths instead of removing automatically when physics is present. Disable once you trust your profile.
- **Preserve animated bones** (`preserveAnimatedBones`, default **on**): Collects animated transforms from override/base controllers and advanced settings before pruning.
- **Preserve bone name patterns** (`preserveBoneNamePatterns`, default `Hair,Skirt,Cloth,Breast,Tail`): Keeps bones containing these tokens; add your rig-specific patterns to avoid cloth/facial loss.
- **Preserve children of used bones** (`preserveChildrenOfUsedBones`, default **on**): Retains descendants of kept bones to maintain hierarchies.
- **Preserve bones with constraints** (`preserveBonesWithConstraints`, default **on**): Skips removal if any `*Constraint` component is attached.

**Recommendations:** Keep all physics checks on. Turn on **Remove bones without weights** after verifying cloth/physics rigs, and expand name patterns for custom tails, skirts, or props.

### Mesh Optimization
- **Merge vertices by distance** (`mergeVerticesByDistance`, default **on**) with **Merge distance** (`mergeDistance`, default **0.0001**) plus optional **Compare normals** (`compareNormals`, default **on** / **Normal angle threshold** default **5°**) and **Compare UVs** (`compareUVs`, default **off** / **UV distance threshold** default **0.01**): Collapses near-duplicate vertices while respecting shading and (optionally) UV layout.
- **Delete loose vertices** (`deleteLooseVertices`, default **on**): Removes vertices unused by any triangle—safe baseline cleanup.
- **Combine meshes** (`combineMeshes`, default **off**): Groups meshes with compatible materials, skips those with animated transforms (except blendshape-only), and merges to reduce draw calls.
- **Recalculate normals/tangents** (`recalculateNormals`, `recalculateTangents`, defaults **off**): Rebuilds shading data post-merge; enable if you see shading seams after vertex/mesh merges.
- **Optimize mesh for rendering** (`optimizeMeshForRendering`, default **on**): Runs Unity’s mesh optimizer—leave enabled unless debugging.
- **Apply mesh compression** (`applyMeshCompression`, default **off**) with **Compression level** (`Low/Medium/High`, default **Medium**): Reduces mesh memory; verify hero assets for quality.
- **Mesh name filter/exclude** (`meshNameFilter`, `meshNameExclude`): Comma-separated includes/excludes so you can isolate or skip specific renderers.

**Recommendations:** Keep vertex merging and loose-vertex cleanup on. Combine meshes selectively for performance-heavy avatars. Add filters to skip FX or delicate cloth meshes.

### Blendshape Optimization
- **Remove unused blendshapes** (`removeUnusedBlendshapes`, default **on**): Scans animations, CVR blink/viseme/eye-look, and CVRFaceTracking to mark shapes as used before removal.
- **Scan override/advanced controllers** (`scanOverrideController`, `scanAdvancedAvatarSettings`, defaults **on**): Searches Animator/Override controllers referenced by CVR advanced settings to preserve animated blendshapes.
- **Preserve blink/viseme/face-tracking/eye-look** (`preserveBlinkBlendshapes`, `preserveVisemeBlendshapes`, `preserveFaceTrackingBlendshapes`, `preserveEyeLookBlendshapes`, defaults **on**): Retains the core facial feature set.
- **Remove zero-delta blendshapes** (`removeZeroDeltaBlendshapes`, default **on**) with **Zero-delta threshold** (`zeroDeltaThreshold`, default **0.00001m**): Deletes shapes whose vertex deltas fall below the threshold.
- **Preserve/force-remove patterns** (`preserveBlendshapePatterns`, `forceRemoveBlendshapePatterns`): Comma-separated patterns applied after scans. Patterns are logged when used.
- **Verbose logging** (`verboseLogging`, default **off**): Prints each preserved/removed blendshape during scans.

**Recommendations:** Leave all scans and facial preserves on. Use preserve patterns for custom visemes/ARKit sets; force-remove for known dead/test shapes.

### Texture Atlas Generation
- **Generate texture atlas** (`generateTextureAtlas`, default **off**): Duplicates materials and atlases by shader group; renderer slots are swapped to the copies so originals stay intact.
- **Exclude animated materials** (`excludeAnimatedMaterials`, default **on**) with **Scan override/advanced controllers** (`scanOverrideController`, `scanAdvancedAvatarSettings`, defaults **on**): Gathers animated material properties and skips those materials to avoid breaking swaps.
- **Exclude material patterns** (`excludeMaterialPatterns`): Comma-separated names to skip (FX/UI/etc.).
- **Atlas generation modes:**
  - **Enhanced workflow** (`useEnhancedAtlasWorkflow`, default **off**): Groups properties by texture signature, filters non-2D slots, and recursively packs subsets for better results without external tools.
  - **Merge identical textures** (`mergeIdenticalTextures`, default **off**): Experimental mode that groups materials sharing texture sets before atlasing.
  - **Standard automatic**: Default when neither enhanced nor merge mode is selected.
- **Basic atlas settings:** **Max atlas size** (`maxAtlasSize`, default **2048**), **Atlas padding** (`atlasPadding`, default **2px**).
- **Advanced filters:** **Minimum materials for atlas** (`minimumMaterialsForAtlas`, default **2**), **Allowed/excluded texture properties** (`allowedTextureProperties` default `*`, `excludedTextureProperties`), **Minimum texture size** (`minimumTextureSize`, default **32px**).
- **Shader filtering:** **Allowed shader names** (`allowedShaderNames`) and **Excluded shader names** (`excludedShaderNames`, default `Hidden,UI,Unlit/Transparent`).
- **Compression:** **Compress atlases** (`compressAtlases`, default **on**) with **Compression format** (`compressionFormat`, default **Automatic**, options DXT1/DXT5/BC7/ASTC/Uncompressed).
- **Verbose logging** (`verboseLogging`, default **off**): Logs inclusion/exclusion decisions and atlas grouping.

**Recommendations:** Keep animation scans on. Start with enhanced workflow + compression. Tighten shader/property filters if certain materials must remain separate.

## Known limitations and considerations
- **Magica Cloth 1 interference:** Mesh edits (vertex merges, atlas UV changes) and bone removals can disrupt Magica Cloth 1 or other components that rely on original mesh data. There is detection/guard logic, but it is not fully reliable yet; you may need to rebuild cloth mesh data manually after processing. This also means running automatically on Play/Bundle may not suit Magica Cloth 1 setups—prefer Manual Bake and review.
- **Controller coverage:** The processor inspects CVR controllers and advanced settings. Scripted or runtime-only animation changes won’t be detected unless you mirror them in controllers or add preserve patterns.
- **Compression quality:** High mesh/texture compression can visibly degrade hero assets; test critical looks.

## Troubleshooting
- **Unexpected cloth or physics breaks:** Disable bone removal or vertex merging for affected meshes; keep Magica/Dynamic/PhysBone checks on.
- **Missing animations or blendshapes:** Ensure controllers are assigned before building and that preserve patterns cover custom shapes.
- **Atlas artifacts:** Increase padding, reduce compression, or exclude problematic shaders/materials.
- **Tool not visible:** Confirm the package path exists under `Packages/` and local packages are enabled in Package Manager settings.

## Logging and statistics
Logs are buffered to avoid truncation and emitted in numbered chunks with start/end banners. Stats reported per run: bones removed, bone references removed, blendshapes removed, vertices merged, loose vertices removed, meshes combined, atlases generated, and total optimization time.

## Complementary usage with NDMF-Merge
NDMF-Avatar-Optimization and NDMF-Merge are tuned to work together inside the NDMF pipeline. If your project includes NDMF-Merge, it runs first on the base avatar and the optimizer follows automatically, so both tools contribute to a consistent baked clone without manual reordering.
