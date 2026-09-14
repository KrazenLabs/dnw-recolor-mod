using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Recolor
{
    internal sealed class MeshBinding : IDisposable
    {
        public Mesh Mesh;
        public string Name;
        public string Error;
        public bool GpuPath;
        // For logging
        public string Details = "";

        // Readable path
        private Color32[] _originalColors;
        private Color32[] _work;

        // GPU path
        private GraphicsBuffer _buffer;
        private byte[] _originalBytes;
        private byte[] _workBytes;
        private int _stride, _offset, _dimension, _count;
        private VertexAttributeFormat _format;

        public bool IsValid { get { return Mesh != null && Error == null; } }

        public static MeshBinding Create(Mesh mesh)
        {
            var binding = new MeshBinding { Mesh = mesh, Name = mesh != null ? mesh.name : "null" };
            if (mesh == null) { binding.Error = "mesh is null"; return binding; }
            try
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.Color)) { binding.Error = "mesh has no vertex colors"; return binding; }
                if (mesh.isReadable)
                {
                    var colors = mesh.colors32;
                    if (colors == null || colors.Length != mesh.vertexCount) { binding.Error = "vertex colors could not be read"; return binding; }
                    binding._originalColors = colors;
                    binding._work = new Color32[colors.Length];
                    binding.Details = "readable mesh, " + colors.Length + " vertices";
                    return binding;
                }
                binding.InitializeGpuPath();
            }
            catch (Exception e)
            {
                binding.Error = e.GetType().Name + ": " + e.Message;
            }
            return binding;
        }

        private void InitializeGpuPath()
        {
            int stream = Mesh.GetVertexAttributeStream(VertexAttribute.Color);
            if (stream < 0) { Error = "color attribute has no vertex stream"; return; }
            _format = Mesh.GetVertexAttributeFormat(VertexAttribute.Color);
            _dimension = Mesh.GetVertexAttributeDimension(VertexAttribute.Color);
            _offset = Mesh.GetVertexAttributeOffset(VertexAttribute.Color);
            if (_format != VertexAttributeFormat.UNorm8 && _format != VertexAttributeFormat.Float32 && _format != VertexAttributeFormat.UNorm16)
            {
                Error = "unsupported vertex color format " + _format;
                return;
            }
            if (_dimension < 3) { Error = "vertex color has only " + _dimension + " components"; return; }

            // Do NOT set Mesh.vertexBufferTarget.
            _buffer = Mesh.GetVertexBuffer(stream);
            if (_buffer == null) { Error = "vertex buffer unavailable"; return; }
            _stride = _buffer.stride;
            _count = _buffer.count;
            if (_stride <= 0 || _count <= 0) { Error = "vertex buffer is empty"; DisposeWithoutRestore(); return; }
            if (_offset + BytesPerComponent(_format) * _dimension > _stride) { Error = "color attribute does not fit the vertex stride"; DisposeWithoutRestore(); return; }

            _originalBytes = new byte[_stride * _count];
            // Async readback path, synchronous GetData is a fallback
            string how = null;
            string readbackProblem = null;
            try
            {
                var request = AsyncGPUReadback.Request(_buffer);
                request.WaitForCompletion();
                if (request.hasError) readbackProblem = "AsyncGPUReadback reported an error";
                else
                {
                    request.GetData<byte>().CopyTo(_originalBytes);
                    how = "AsyncGPUReadback";
                }
            }
            catch (Exception readbackError)
            {
                readbackProblem = readbackError.Message;
            }
            if (how == null)
            {
                try
                {
                    _buffer.GetData(_originalBytes);
                    how = "GetData (after " + readbackProblem + ")";
                }
                catch (Exception getDataError)
                {
                    Error = "could not read the vertex buffer (" + readbackProblem + "; " + getDataError.Message + ")";
                    DisposeWithoutRestore();
                    return;
                }
            }

            // All zeros cannot be the original mesh data, leave such meshes alone
            bool anyData = false;
            for (int i = 0; i < _originalBytes.Length && !anyData; i++) anyData = _originalBytes[i] != 0;
            if (!anyData) { Error = "vertex buffer read back empty"; DisposeWithoutRestore(); return; }

            _workBytes = new byte[_originalBytes.Length];
            GpuPath = true;
            Details = "GPU buffer stream " + stream + ", " + _count + " vertices, stride " + _stride + ", colors " + _format + "x" + _dimension + " at +" + _offset + ", read via " + how + ", target " + _buffer.target;
        }

        private static int BytesPerComponent(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.UNorm8: return 1;
                case VertexAttributeFormat.UNorm16: return 2;
                default: return 4;
            }
        }

        public void Apply(ColorMatrix matrix)
        {
            if (!IsValid) return;
            try
            {
                if (!GpuPath)
                {
                    matrix.Apply(_originalColors, _work);
                    Mesh.colors32 = _work;
                    return;
                }
                Buffer.BlockCopy(_originalBytes, 0, _workBytes, 0, _originalBytes.Length);
                for (int v = 0; v < _count; v++)
                {
                    int at = v * _stride + _offset;
                    switch (_format)
                    {
                        case VertexAttributeFormat.UNorm8:
                            {
                                var c = matrix.Apply(new Color32(_workBytes[at], _workBytes[at + 1], _workBytes[at + 2], 255));
                                _workBytes[at] = c.r; _workBytes[at + 1] = c.g; _workBytes[at + 2] = c.b;
                                break;
                            }
                        case VertexAttributeFormat.Float32:
                            {
                                float r = BitConverter.ToSingle(_workBytes, at), g = BitConverter.ToSingle(_workBytes, at + 4), b = BitConverter.ToSingle(_workBytes, at + 8);
                                matrix.Apply(ref r, ref g, ref b);
                                Buffer.BlockCopy(BitConverter.GetBytes(r), 0, _workBytes, at, 4);
                                Buffer.BlockCopy(BitConverter.GetBytes(g), 0, _workBytes, at + 4, 4);
                                Buffer.BlockCopy(BitConverter.GetBytes(b), 0, _workBytes, at + 8, 4);
                                break;
                            }
                        case VertexAttributeFormat.UNorm16:
                            {
                                float r = BitConverter.ToUInt16(_workBytes, at) / 65535f, g = BitConverter.ToUInt16(_workBytes, at + 2) / 65535f, b = BitConverter.ToUInt16(_workBytes, at + 4) / 65535f;
                                matrix.Apply(ref r, ref g, ref b);
                                Buffer.BlockCopy(BitConverter.GetBytes((ushort)Mathf.RoundToInt(r * 65535f)), 0, _workBytes, at, 2);
                                Buffer.BlockCopy(BitConverter.GetBytes((ushort)Mathf.RoundToInt(g * 65535f)), 0, _workBytes, at + 2, 2);
                                Buffer.BlockCopy(BitConverter.GetBytes((ushort)Mathf.RoundToInt(b * 65535f)), 0, _workBytes, at + 4, 2);
                                break;
                            }
                    }
                }
                _buffer.SetData(_workBytes);
            }
            catch (Exception e)
            {
                Error = "apply failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public void Restore()
        {
            if (Mesh == null) return;
            try
            {
                if (!GpuPath) { if (_originalColors != null) Mesh.colors32 = _originalColors; }
                else if (_buffer != null && _originalBytes != null && _buffer.IsValid()) _buffer.SetData(_originalBytes);
            }
            catch (Exception e)
            {
                Error = "restore failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public float MeasureChroma()
        {
            if (!IsValid) return 0f;
            if (!GpuPath) return RecolorMod.AverageChroma(_originalColors, Mathf.Max(1, _originalColors.Length / 512));
            if (_format != VertexAttributeFormat.UNorm8) return 0.5f;
            long sum = 0; int count = 0;
            int step = Mathf.Max(1, _count / 512);
            for (int v = 0; v < _count; v += step)
            {
                int at = v * _stride + _offset;
                int r = _originalBytes[at], g = _originalBytes[at + 1], b = _originalBytes[at + 2];
                sum += Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b));
                count++;
            }
            return count == 0 ? 0f : sum / (255f * count);
        }

        public void Dispose()
        {
            Restore();
            DisposeWithoutRestore();
        }

        public void DisposeWithoutRestore()
        {
            if (_buffer != null) { try { _buffer.Dispose(); } catch { } _buffer = null; }
        }
    }
}
