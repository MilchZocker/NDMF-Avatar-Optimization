#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace MilchZocker.AvatarOptimizer
{
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
}
#endif
