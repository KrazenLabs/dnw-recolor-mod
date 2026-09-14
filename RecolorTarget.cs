using System.Collections.Generic;
using DnWModLoader.Config;
using UnityEngine;

namespace Recolor
{
    // Material slot to change
    internal sealed class MaterialBinding
    {
        public Renderer Renderer;
        public Material Material;
        public string Property;
        public Texture2D Original;
        public int SourceId;
        public string Description;
        public Texture Assigned; // null = original texture
    }

    internal sealed class RecolorTarget
    {
        public string Id;
        public string DisplayName;
        public ColorAdjustParams Params = ColorAdjustParams.Identity;

        public ConfigEntry<float> HueEntry;
        public ConfigEntry<float> SaturationEntry;
        public ConfigEntry<float> BrightnessEntry;

        // Material bindings by material instance id.
        public readonly Dictionary<int, MaterialBinding> Materials = new Dictionary<int, MaterialBinding>();
        // Vertex color bindings by mesh instance id.
        public readonly Dictionary<int, MeshBinding> Meshes = new Dictionary<int, MeshBinding>();
        // Full-resolution adjusted textures by source texture instance id.
        public readonly Dictionary<int, RecoloredTexture> Outputs = new Dictionary<int, RecoloredTexture>();
        // Preview textures by source texture instance id.
        public readonly Dictionary<int, RecoloredTexture> Previews = new Dictionary<int, RecoloredTexture>();

        public bool MeshesDirty;
        public bool PreviewDirty;
        public bool FullDirty;
        // Delay until full-res pass
        public float FullApplyAt;
        // Preview rate-limit
        public float NextPreviewAt;
        public float LastChangeAt;
        public bool InScene;
        public int JobsRunning;
        public string Status = "";
        // Description buffer
        public string LastDescription;

        public bool IsIdentity { get { return Params.IsIdentity; } }

        public bool HasBindings { get { return Materials.Count > 0 || Meshes.Count > 0; } }

        public void LoadFromConfig()
        {
            Params = new ColorAdjustParams(HueEntry.Value, SaturationEntry.Value, BrightnessEntry.Value).Clamped();
        }

        // Slider was moved
        public void MarkChanged(float now, float delay)
        {
            MeshesDirty = true;
            PreviewDirty = true;
            FullDirty = true;
            FullApplyAt = now + delay;
            LastChangeAt = now;
        }

        // Swap to full res
        public void RequestFull(float now)
        {
            FullDirty = true;
            if (FullApplyAt > now) FullApplyAt = now;
        }

        public bool FullOutputsCurrent()
        {
            foreach (var output in Outputs.Values)
                if (output.Output == null || !output.HasAppliedParams || !output.AppliedParams.Equals(Params)) return false;
            return true;
        }

        public bool IsOutputTexture(Texture texture)
        {
            if (texture == null) return false;
            foreach (var output in Outputs.Values) if (output.Output == texture) return true;
            foreach (var output in Previews.Values) if (output.Output == texture) return true;
            return false;
        }

        public void DisposeOutputs()
        {
            DisposeWhere(Outputs, null);
            DisposeWhere(Previews, null);
        }

        // Clean up unused textures
        public void DisposeUnusedOutputs()
        {
            if (Outputs.Count == 0 && Previews.Count == 0) return;
            var used = new HashSet<int>();
            foreach (var mb in Materials.Values) used.Add(mb.SourceId);
            DisposeWhere(Outputs, used);
            DisposeWhere(Previews, used);
        }

        private void DisposeWhere(Dictionary<int, RecoloredTexture> outputs, HashSet<int> keep)
        {
            if (outputs.Count == 0) return;
            var ids = new List<int>(outputs.Keys);
            foreach (int id in ids)
            {
                if (keep != null && keep.Contains(id)) continue;
                var output = outputs[id];
                Detach(output);
                output.Dispose();
                outputs.Remove(id);
            }
        }

        private void Detach(RecoloredTexture output)
        {
            if (output.Output == null) return;
            foreach (var mb in Materials.Values)
            {
                if (mb.Assigned != output.Output) continue;
                mb.Assigned = null;
                if (mb.Material != null && mb.Original != null) mb.Material.SetTexture(mb.Property, mb.Original);
            }
        }

        public void ClearBindings(bool restore)
        {
            if (restore)
            {
                foreach (var mb in Materials.Values)
                {
                    if (mb.Material != null && mb.Original != null) mb.Material.SetTexture(mb.Property, mb.Original);
                }
            }
            foreach (var mesh in Meshes.Values) mesh.Dispose();
            foreach (var output in Outputs.Values) output.Dispose();
            foreach (var output in Previews.Values) output.Dispose();
            Materials.Clear();
            Meshes.Clear();
            Outputs.Clear();
            Previews.Clear();
            JobsRunning = 0;
            MeshesDirty = false;
            PreviewDirty = false;
            FullDirty = false;
        }
    }
}
