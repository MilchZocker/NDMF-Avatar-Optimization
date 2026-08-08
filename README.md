# NDMF Avatar Optimisation for ChilloutVR

NDMF Avatar Optimisation is a conservative, non-destructive optimization tool for ChilloutVR avatars built on top of NDMF. Its purpose is simple: take a complex avatar, inspect its rig, materials, controllers, blendshapes, and mesh data, then make the safest possible cleanup and reduction passes during the build pipeline without mutating the original source assets.

This package is designed to work especially well alongside NDMF Merge. If your avatar is being assembled from multiple outfits, accessories, or merged components, NDMF Merge handles the structural composition and NDMF Avatar Optimisation cleans up the resulting baked clone so it is lighter, simpler, and easier to maintain.

> Recommended workflow: use NDMF Merge for assembly and NDMF Avatar Optimisation for cleanup. The optimizer runs after merge-related steps in the same NDMF pipeline, so the two tools complement each other naturally.

---

## ✨ Key Features

### Non-destructive by design
The optimizer works on a cloned bake result rather than directly rewriting your source assets. Meshes, materials, and bones are inspected and modified in the build-time clone, which keeps your original avatar data intact and makes it much safer to iterate.

### Animation-aware safety
The tool inspects the avatar's controllers, override controllers, advanced avatar settings, and related references to understand which bones, blendshapes, and material properties are actually in use. This is critical for CVR avatars, where facial systems, toggles, and physics-related rig elements can be easy to accidentally break.

### Bone cleanup with protective checks
The processor can remove unused bone references and identify bones that are no longer needed. Before doing anything destructive, it checks for common safety cases such as:
- animated transforms
- cloth and physics components
- constraints
- named bone patterns that should be preserved
- children of used bones

This makes the bone cleanup system much more conservative than a generic mesh optimizer.

### Blendshape cleanup
Blendshapes are one of the most delicate parts of a CVR avatar. The tool can remove unused or near-empty blendshapes, but it is careful to preserve facial systems such as blink, viseme, eye-look, and face-tracking shapes unless you explicitly opt out of that protection.

### Mesh optimisation and safe merging
The optimizer can:
- merge nearby vertices
- delete loose vertices
- recalculate normals and tangents
- optionally combine compatible meshes
- reduce draw-call pressure where the mesh data is clearly safe to combine

The implementation is intentionally cautious, especially for skinned meshes, and it avoids unsafe merges when the rig or blendshape setup would be at risk.

### Texture atlas generation
The package can generate texture atlases for compatible materials, group them by shader and property usage, and apply compression controls. It also includes animation safety checks so materials used by controllers are not casually included in atlases and broken by shader-property changes.

### Analysis and reporting
The tool records findings, warnings, and summary information and can run in report-only mode so you can review what it would change before allowing destructive operations. This is especially useful for the first bake of a new avatar or when you are tuning a complex rig.

---

## 📦 Requirements

- Unity 2021.3 or newer
- ChilloutVR CCK imported into the project
- NDMF 1.4+ installed
- Chillaxins installed for the package to load correctly in the NDMF ecosystem
- Optional but strongly recommended: NDMF Merge for avatar assembly and outfit merging

---

## 🚀 Installation

### Option A: Install via Unity Package Manager (Git URL)
1. Open Window → Package Manager
2. Click the + button in the top-left corner
3. Choose Add package from git URL...
4. Enter:
   `https://github.com/MilchZocker/NDMF-Avatar-Optimisation.git#upm`
5. Click Add

### Option B: Manual installation
Copy the repository into your project's Packages directory so the package is available as a local Unity package.

Once installed, make sure that local packages are enabled in Project Settings → Package Manager.

---

## 🧩 Setup Guide

### 1. Add the component
Select your avatar root object, the object that already contains your CVR avatar setup, and add the component:
- NDMF Avatar Optimizer → Avatar Optimizer

This component is the main user-facing control surface for the optimizer.

### 2. Start conservatively
For the first run, keep the default settings and review the output carefully. The optimizer is designed to be safe, but avatars can vary a lot in how their bones, materials, and controllers are wired.

If you want a preview-first workflow, enable report-only mode before making broader changes.

### 3. Bake the avatar
Upload your avatar or use Manual Bake in NDMF. The plugin runs automatically during the NDMF build pipeline and applies optimization to the baked clone.

### 4. Review the result
After baking, inspect:
- cloth and physics-driven bones
- facial blendshape behavior
- material appearance and UVs
- mesh deformation or seams
- any log warnings or analysis findings

If something looks wrong, tune the settings rather than immediately turning everything on.

---

## 🛠️ How the optimizer works

The optimizer does not directly rewrite your source hierarchy in place. Instead, it runs in the NDMF build flow and works on the generated avatar clone created for the bake.

The general flow is:
1. NDMF prepares the avatar and build context
2. The plugin locates the Avatar Optimizer component
3. The processor analyzes the avatar's bones, controllers, mesh data, blendshapes, materials, and physics-related references
4. It applies the selected cleanup passes in a conservative order
5. It emits logs and statistics for review

The plugin is registered as an NDMF build step and is intentionally positioned after merge-related steps so it can clean up the final merged avatar rather than the pre-merge source structure.

---

## ⚙️ Detailed Configuration

The component exposes several groups of settings. They are implemented in the runtime component and consumed by the build processor. The main categories are described below.

### Bone optimisation
Bone cleanup is one of the most sensitive areas in an avatar pipeline. The optimizer can remove unused bone references and, in some cases, remove actual empty bones, but it does so with multiple safety rails.

Key settings include:
- Remove unused bone references
- Only remove zero-weight bones
- Minimum weight threshold
- Preserve animated bones
- Preserve bone name patterns
- Preserve children of used bones
- Preserve bones with constraints
- Physics checks for Magica Cloth, Dynamic Bones, and VRC PhysBones
- Manual confirmation mode for risky removals

Recommended starting point:
- Keep physics checks enabled
- Leave manual confirmation on while you are still learning your avatar’s rig
- Expand preserve name patterns for custom cloth, tail, breast, or accessory bones if needed

### Mesh optimisation
Mesh optimization is generally safe when it is conservative, but it can quickly become destructive if applied too aggressively to skinned avatars.

Key settings include:
- Merge vertices by distance
- Compare normals and UVs when merging
- Delete loose vertices
- Combine compatible meshes
- Recalculate normals/tangents
- Optimize mesh for rendering
- Apply mesh compression
- Name-based include/exclude filters

Recommended starting point:
- Keep vertex merging and loose-vertex cleanup enabled
- Leave mesh combining off until you have validated a few bakes
- Use mesh name filters to exclude delicate or special-case meshes

### Blendshape optimisation
Blendshapes are preserved by default where the tool can tell they are part of core CVR facial systems or user-defined facial behavior. The optimizer can also remove near-empty blendshapes to reduce data overhead.

Key settings include:
- Remove unused blendshapes
- Scan override controllers and advanced avatar settings
- Preserve blink, viseme, face-tracking, and eye-look shapes
- Remove zero-delta blendshapes
- Preserve/force-remove patterns by name
- Verbose logging for debugging

Recommended starting point:
- Leave facial preserve options enabled
- Use preserve patterns for custom face systems
- Only rely on forced removals for clearly dead or test shapes

### Texture atlas generation
The atlas system is meant to reduce texture draw-call pressure and improve material packing while trying to keep the avatar safe. It can group compatible materials and generate atlases using a shader/property-aware workflow.

Key settings include:
- Generate texture atlas
- Exclude animated materials
- Scan override controllers and advanced avatar settings for material animation
- Exclude material patterns
- Atlas size and padding
- Shader and property filters
- Compression settings
- Enhanced workflow and texture deduplication toggles

Recommended starting point:
- Leave atlasing off until you have a stable bake
- Enable it only for avatars with many materials that are clearly safe to group
- Tighten shader/property filters for special materials or UI-like shaders

### Analysis and reporting
The tool can run in a report-only mode where it explains what it would change without applying destructive changes. This is very useful when you want a safer onboarding experience or when you are adapting the package to a new avatar structure.

Recommended starting point:
- Review the analysis report before turning on stronger cleanup passes
- Use it to find candidate bones, blendshapes, and materials that deserve a second look

---

## ✅ Recommended workflow for most avatars

1. Add the Avatar Optimizer component to the avatar root
2. Leave the optimizer in a conservative state for the first bake
3. Review the logs and analysis findings
4. Enable more aggressive cleanup only after the avatar behaves correctly in the bake output
5. Repeat with small, testable changes rather than turning on every optimization at once

This workflow is especially helpful for avatars with:
- custom facial systems
- cloth or physics setups
- multiple materials and shader variants
- complex rigs or nonstandard bones

---

## 🔧 Troubleshooting

### My avatar deforms after baking
- Disable or reduce mesh combining
- Recheck bone removal decisions
- Review whether the affected mesh has blendshapes or special rig data
- Inspect the logs for preserved or removed bone references

### Cloth or physics looks wrong
- Keep physics checks enabled
- Review the candidate bone list before removing bones
- Use more conservative preserve patterns
- Prefer manual bake and inspect the result before upload

### Blendshapes are missing
- Confirm the controllers are assigned correctly
- Ensure preserve patterns include the relevant names
- Check whether the blendshape was actually used in the controller or CVR facial data

### Atlas generation looks broken
- Increase padding
- Reduce compression quality
- Exclude problematic materials or shaders
- Use tighter property filters

### The component does not appear
- Confirm that the package is installed correctly
- Verify local packages are enabled
- Make sure the project has the required NDMF and Chillaxins dependencies

---

## 🧠 Best Practices

- Start with the safest settings and only increase aggressiveness after validation
- Use Manual Bake first before uploading to a live avatar
- Keep cloth, physics, and facial systems under review during the first few bakes
- Prefer report-only analysis when first adapting the package to a new avatar
- Use name-based preserve patterns for custom rig elements that should never be removed
- Treat the optimizer as a cleanup and safety tool, not as a replacement for careful avatar authoring

---

## 🤝 Complementary use with NDMF Merge

NDMF Merge and NDMF Avatar Optimisation are meant to work together.

- NDMF Merge helps assemble and merge outfits, accessories, and armature structures into a single avatar build target
- NDMF Avatar Optimisation helps reduce and clean up the resulting baked clone so it is lighter, easier to maintain, and less likely to carry unnecessary overhead

This pairing is especially effective when you want a build pipeline that is both powerful and safe.

---

## 📄 License

This project is distributed under the MIT License. See the LICENSE file for details.
