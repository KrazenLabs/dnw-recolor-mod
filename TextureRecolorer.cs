using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Recolor
{
    // Original texture as pixel matrix for CPU
    internal sealed class TextureSource
    {
        public Texture2D Texture;
        public int InstanceId;
        public string Name;
        public int Width;
        public int Height;
        public bool SRGB;
        public Color32[] Pixels;
        public int Bytes { get { return Pixels != null ? Pixels.Length * 4 : 0; } }

        private readonly object _previewSync = new object();
        private Color32[] _previewPixels;
        private int _previewSize;

        public int PreviewFactor(int maxSize)
        {
            if (maxSize <= 0) return 1;
            int factor = 1;
            while (Mathf.Max(Width, Height) / factor > maxSize && factor < 64) factor *= 2;
            return factor;
        }

        public bool NeedsPreview(int maxSize)
        {
            return PreviewFactor(maxSize) > 1;
        }

        public void PreviewDimensions(int maxSize, out int width, out int height)
        {
            int factor = PreviewFactor(maxSize);
            width = Mathf.Max(1, Width / factor);
            height = Mathf.Max(1, Height / factor);
        }

        // Access preview or generate if needed
        public Color32[] EnsurePreview(int maxSize)
        {
            int factor = PreviewFactor(maxSize);
            if (factor <= 1 || Pixels == null) return null;
            lock (_previewSync)
            {
                if (_previewPixels != null && _previewSize == maxSize) return _previewPixels;
                int w = Mathf.Max(1, Width / factor), h = Mathf.Max(1, Height / factor);
                var src = Pixels;
                int srcWidth = Width;
                var dst = new Color32[w * h];
                int block = factor * factor;
                Parallel.For(0, h, y =>
                {
                    int row0 = y * factor;
                    for (int x = 0; x < w; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0;
                        int col0 = x * factor;
                        for (int yy = 0; yy < factor; yy++)
                        {
                            int at = (row0 + yy) * srcWidth + col0;
                            for (int xx = 0; xx < factor; xx++)
                            {
                                var c = src[at + xx];
                                r += c.r; g += c.g; b += c.b; a += c.a;
                            }
                        }
                        dst[y * w + x] = new Color32((byte)(r / block), (byte)(g / block), (byte)(b / block), (byte)(a / block));
                    }
                });
                _previewPixels = dst;
                _previewSize = maxSize;
                return dst;
            }
        }
    }

    internal static class TextureReadback
    {
        public const int PieceBytes = 512 * 1024;

        private static readonly Dictionary<int, TextureSource> Cache = new Dictionary<int, TextureSource>();

        public static int CachedBytes { get; private set; }
        public static int CachedCount { get { return Cache.Count; } }

        public static int RowsPerPiece(int width, int height)
        {
            int rows = Mathf.Clamp(PieceBytes / Mathf.Max(1, width * 4), 1, height);
            while (rows > 1 && height % rows != 0) rows--;
            return rows;
        }

        public static bool TryGet(Texture2D texture, int maxSize, out TextureSource source, out string error)
        {
            source = null;
            error = null;
            if (texture == null) { error = "texture is null"; return false; }
            int id = texture.GetInstanceID();
            if (Cache.TryGetValue(id, out source) && source.Pixels != null) return true;
            if (texture.width > maxSize || texture.height > maxSize)
            {
                error = "texture " + texture.name + " is " + texture.width + "x" + texture.height + ", above the configured maximum of " + maxSize;
                return false;
            }

            RenderTexture rt = null;
            try
            {
                bool srgb = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
                int w = texture.width, h = texture.height;
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, rt);
                var pixels = new Color32[w * h];
                int rows = RowsPerPiece(w, h);
                for (int y = 0; y < h; y += rows)
                {
                    var request = AsyncGPUReadback.Request(rt, 0, 0, w, y, rows, 0, 1, TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    if (request.hasError) throw new InvalidOperationException("GPU readback failed at row " + y);
                    NativeArray<Color32>.Copy(request.GetData<Color32>(), 0, pixels, y * w, w * rows);
                }
                source = new TextureSource
                {
                    Texture = texture,
                    InstanceId = id,
                    Name = texture.name,
                    Width = w,
                    Height = h,
                    SRGB = srgb,
                    Pixels = pixels,
                };
            }
            catch (Exception e)
            {
                error = "readback of " + texture.name + " failed: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }

            Cache[id] = source;
            CachedBytes += source.Bytes;
            return true;
        }

        public static void Clear()
        {
            Cache.Clear();
            CachedBytes = 0;
        }
    }

    internal sealed class RecoloredTexture : IDisposable
    {
        public readonly TextureSource Source;
        public readonly bool IsPreview;
        public readonly int PreviewSize;
        public readonly int Width;
        public readonly int Height;
        public RenderTexture Output;
        public Color32[] Work;

        public bool Busy;
        public int Version;
        public ColorAdjustParams AppliedParams;
        public bool HasAppliedParams;
        public ColorAdjustParams PendingParams;

        private int _uploadRow = -1;
        private readonly int _rowsPerPiece;
        private Texture2D _staging;
        private Color32[] _piece;

        public RecoloredTexture(TextureSource source, bool preview, int previewSize)
        {
            Source = source;
            IsPreview = preview;
            PreviewSize = previewSize;
            if (preview) source.PreviewDimensions(previewSize, out Width, out Height);
            else { Width = source.Width; Height = source.Height; }
            Work = new Color32[Width * Height];
            _rowsPerPiece = TextureReadback.RowsPerPiece(Width, Height);
        }

        public string Description
        {
            get { return Source.Name + " " + Width + "x" + Height + (IsPreview ? " (preview)" : ""); }
        }

        public bool Uploading { get { return _uploadRow >= 0; } }

        public void BeginUpload(ColorAdjustParams p)
        {
            PendingParams = p;
            EnsureOutput();
            _uploadRow = 0;
        }

        public void AbortUpload()
        {
            _uploadRow = -1;
        }

        public bool UploadStep(int maxBytes)
        {
            if (!Uploading) return false;
            if (Output == null || Work == null) { _uploadRow = -1; return false; }
            int pieces = Mathf.Max(1, maxBytes / (Width * 4 * _rowsPerPiece));
            while (pieces-- > 0 && _uploadRow < Height)
            {
                int rows = Mathf.Min(_rowsPerPiece, Height - _uploadRow);
                Array.Copy(Work, _uploadRow * Width, _piece, 0, rows * Width);
                _staging.SetPixels32(_piece);
                _staging.Apply(false, false);
                Graphics.CopyTexture(_staging, 0, 0, 0, 0, Width, rows, Output, 0, 0, 0, _uploadRow);
                _uploadRow += rows;
            }
            if (_uploadRow < Height) return false;
            _uploadRow = -1;
            Output.GenerateMips();
            return true;
        }

        private void EnsureOutput()
        {
            if (Output == null || !Output.IsCreated())
            {
                if (Output != null) { Output.Release(); UnityEngine.Object.Destroy(Output); }
                var format = Source.SRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                Output = new RenderTexture(Width, Height, 0, format)
                {
                    name = Source.Name + (IsPreview ? " (Recolor preview)" : " (Recolor)"),
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                };
                var original = Source.Texture;
                if (original != null)
                {
                    Output.wrapMode = original.wrapMode;
                    Output.filterMode = original.filterMode;
                    Output.anisoLevel = original.anisoLevel;
                }
                Output.Create();
            }
            if (_staging == null)
            {
                _staging = new Texture2D(Width, _rowsPerPiece, TextureFormat.RGBA32, false, !Source.SRGB)
                {
                    name = "Recolor staging " + Width + "x" + _rowsPerPiece,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _piece = new Color32[Width * _rowsPerPiece];
            }
        }

        public void Dispose()
        {
            _uploadRow = -1;
            if (Output != null)
            {
                Output.Release();
                UnityEngine.Object.Destroy(Output);
                Output = null;
            }
            if (_staging != null)
            {
                UnityEngine.Object.Destroy(_staging);
                _staging = null;
            }
            Work = null;
            _piece = null;
        }
    }
}
