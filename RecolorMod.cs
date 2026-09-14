using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DnWModLoader;
using DnWModLoader.Config;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.gatordragongames.washnwalk.tools;

namespace Recolor
{
    // Kobold colors: vertex colors
    // Dragon colors: textures
    public sealed class RecolorMod : Mod
    {
        public const string PlayerTargetId = "player";
        private static readonly string[] KnownDragons = { "Conrad", "Ryan", "Alexander" };
        private static readonly string[] BaseMapProperties = { "_BaseColorMap", "_BaseMap", "_MainTex" };
        private const string VertexTintProperty = "_UseVertexTinting";
        private const float IdleDisposeSeconds = 15f;
        // Full-resolution texture pass this long after a slider stops moving
        private const float ApplyDelaySeconds = 0.2f;
        // Size of the preview textures that follow a moving slider
        private const int PreviewSize = 512;
        // Larger textures are left alone
        private const int MaxTextureSize = 4096;
        private const float ScanIntervalSeconds = 1f;
        // Bandages, eyes, toys
        private static readonly string[] ExcludedMaterials = { "plaster", "Eye", "Onahole" };
        // The kobold's own body; held tools near the camera are not
        private static readonly string[] PlayerMaterials = { "FootMaterial", "plapper_material", "kobold" };
        // Preview rate-limit
        private const float PreviewRate = 50f;
        // Texture results reach the GPU in pieces of TextureReadback.PieceBytes; this much per output and frame.
        private const int UploadBytesPerFrame = 2 * 1024 * 1024;

        public static RecolorMod Instance { get; private set; }

        // Diagnostic stats
        public static int MeshApplies { get; private set; }
        public static int PreviewJobs { get; private set; }
        public static int FullJobs { get; private set; }

        private readonly Dictionary<string, RecolorTarget> _targets = new Dictionary<string, RecolorTarget>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly HashSet<int> _boundMaterialIds = new HashSet<int>();
        private readonly HashSet<int> _boundMeshIds = new HashSet<int>();
        private readonly HashSet<int> _unreadableTextures = new HashSet<int>();
        private readonly Dictionary<int, float> _meshChroma = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _textureChroma = new Dictionary<int, float>();
        private FieldInfo _descriptorNameField;
        private float _nextScan;

        public override bool AutoPatch { get { return false; } }

        public IEnumerable<string> TargetIds { get { return _targets.Keys.ToArray(); } }

        public override void OnInitialize()
        {
            Instance = this;

            GetOrCreateTarget(PlayerTargetId, "Player (Kobold)");
            foreach (var dragon in KnownDragons) GetOrCreateTarget(dragon.ToLowerInvariant(), dragon);
            // Any extra dragon sections added to the config file
            foreach (var entry in Config.Entries)
            {
                if (entry.Key == "Hue" && !_targets.ContainsKey(entry.Section)) GetOrCreateTarget(entry.Section, Capitalize(entry.Section));
            }

            _descriptorNameField = AccessTools.Field(typeof(WalkNWashDragonDescriptor), "name");
            if (_descriptorNameField == null) Logger.Warning("WalkNWashDragonDescriptor.name field not found; reading dragon names from prefabs.");
            Logger.Info("Initialized. Targets: " + string.Join(", ", _targets.Keys));
        }

        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
            {
                ResetBindings(restore: false);
            }
            _nextScan = 0f;
        }

        public override void OnUpdate()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); } catch (Exception e) { Logger.Exception(e, "Main-thread job failed"); }
            }

            float now = Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanIntervalSeconds;
                try { Scan(now); } catch (Exception e) { Logger.Exception(e, "Scan failed"); }
            }

            foreach (var target in _targets.Values)
            {
                StepUploads(target);
                if (target.MeshesDirty) ApplyMeshes(target);
                if (target.PreviewDirty || target.FullDirty) ApplyTextures(target, now);
            }
        }

        public void SetTarget(string idOrName, float hue, float saturation, float brightness)
        {
            var target = GetOrCreateTarget(idOrName, Capitalize(idOrName));
            var p = new ColorAdjustParams(hue, saturation, brightness).Clamped();
            target.HueEntry.Value = p.Hue;
            target.SaturationEntry.Value = p.Saturation;
            target.BrightnessEntry.Value = p.Brightness;
        }

        public bool TryGetTarget(string idOrName, out float hue, out float saturation, out float brightness)
        {
            hue = 0f; saturation = 1f; brightness = 1f;
            if (string.IsNullOrEmpty(idOrName) || !_targets.TryGetValue(NormalizeId(idOrName), out var target)) return false;
            hue = target.Params.Hue; saturation = target.Params.Saturation; brightness = target.Params.Brightness;
            return true;
        }

        public void ResetAll()
        {
            foreach (var target in _targets.Values)
            {
                target.HueEntry.Reset();
                target.SaturationEntry.Reset();
                target.BrightnessEntry.Reset();
            }
        }

        private static string NormalizeId(string idOrName)
        {
            return (idOrName ?? "").Trim().ToLowerInvariant();
        }

        private static string Capitalize(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            return char.ToUpperInvariant(id[0]) + id.Substring(1);
        }

        private RecolorTarget GetOrCreateTarget(string idOrName, string displayName)
        {
            string id = NormalizeId(idOrName);
            if (_targets.TryGetValue(id, out var existing))
            {
                if (!string.IsNullOrEmpty(displayName) && existing.DisplayName != displayName && id != PlayerTargetId && displayName != Capitalize(id))
                {
                    existing.DisplayName = displayName;
                    UpdateSectionInfo(existing);
                }
                return existing;
            }
            var target = new RecolorTarget { Id = id, DisplayName = string.IsNullOrEmpty(displayName) ? Capitalize(id) : displayName };
            target.HueEntry = Config.Bind(id, "Hue", 0f, "Hue shift.", new ConfigMeta { Min = -180, Max = 180, Step = 1, Order = 0 });
            target.SaturationEntry = Config.Bind(id, "Saturation", 1f, "Saturation (0 = grayscale, 1 = default).", new ConfigMeta { Min = 0, Max = 2, Step = 0.01, Order = 1 });
            target.BrightnessEntry = Config.Bind(id, "Brightness", 1f, "Brightness (1 = default).", new ConfigMeta { Min = 0, Max = 2, Step = 0.01, Order = 2 });
            target.LoadFromConfig();
            Action<float> onChanged = _ => OnTargetEntryChanged(target);
            target.HueEntry.Changed += onChanged;
            target.SaturationEntry.Changed += onChanged;
            target.BrightnessEntry.Changed += onChanged;
            _targets[id] = target;
            UpdateSectionInfo(target);
            if (!target.IsIdentity) target.MarkChanged(0f, 0f);
            return target;
        }

        private void OnTargetEntryChanged(RecolorTarget target)
        {
            var p = new ColorAdjustParams(target.HueEntry.Value, target.SaturationEntry.Value, target.BrightnessEntry.Value).Clamped();
            if (target.Params.Equals(p)) return;
            target.Params = p;
            target.MarkChanged(Time.realtimeSinceStartup, ApplyDelaySeconds);
            RefreshStatusText(target);
        }

        private void RefreshStatusText(RecolorTarget target)
        {
            string status;
            if (!target.HasBindings) status = "";
            else if (target.IsIdentity) status = "original colors";
            else
            {
                status = target.Params.ToString();
                bool settling = target.JobsRunning > 0 || target.FullDirty || target.PreviewDirty || (target.Outputs.Count > 0 && !target.FullOutputsCurrent());
                if (target.Materials.Count > 0 && settling) status += " (updating...)";
            }
            target.Status = status;
            UpdateSectionInfo(target);
        }

        private void UpdateSectionInfo(RecolorTarget target)
        {
            string status = target.InScene ? "Currently active." : "Not currently in the scene.";
            if (!string.IsNullOrEmpty(target.Status)) status += "  |  " + target.Status;
            int order = target.Id == PlayerTargetId ? 0 : target.InScene ? 1 : 2;
            string key = target.DisplayName + "|" + order + "|" + status;
            if (key == target.LastDescription) return;
            target.LastDescription = key;
            Config.DescribeSection(target.Id, target.DisplayName, status, order);
        }

        private void ResetBindings(bool restore)
        {
            foreach (var target in _targets.Values)
            {
                target.ClearBindings(restore);
                target.InScene = false;
                target.Status = "";
                UpdateSectionInfo(target);
            }
            _boundMaterialIds.Clear();
            _boundMeshIds.Clear();
        }

        private void Scan(float now)
        {
            var wasInScene = _targets.Values.ToDictionary(t => t.Id, t => t.InScene);
            foreach (var target in _targets.Values) target.InScene = false;

            // Look for dragon descriptors
            foreach (var descriptor in UnityEngine.Object.FindObjectsByType<WalkNWashDragonDescriptor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                string name = null;
                try { name = _descriptorNameField != null ? _descriptorNameField.GetValue(descriptor) as string : null; } catch { }
                if (string.IsNullOrEmpty(name)) name = CleanPrefabName(descriptor.gameObject.name);
                var target = GetOrCreateTarget(name, name);
                target.InScene = true;
                foreach (var renderer in descriptor.GetComponentsInChildren<Renderer>(true)) BindRenderer(target, renderer, playerRules: false, now);
            }

            // Player
            var player = _targets[PlayerTargetId];
            var roots = new List<Transform>();
            foreach (var pc in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) roots.Add(pc.transform.root);
            foreach (var hand in UnityEngine.Object.FindObjectsByType<PlapperHand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) roots.Add(hand.transform.root);
            if (Camera.main != null) roots.Add(Camera.main.transform.root);
            if (roots.Count > 0)
            {
                player.InScene = true;
                foreach (var root in roots.Distinct())
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) BindRenderer(player, renderer, playerRules: true, now);
            }

            // Sex-scenes: dragons have no descriptors, recognize by dragon name in textures.
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (renderer.GetComponentInParent<WalkNWashDragonDescriptor>() != null) continue;
                var target = TargetByTextureName(renderer);
                if (target == null) continue;
                target.InScene = true;
                BindRenderer(target, renderer, playerRules: false, now);
            }

            // Watchdog: re-apply when the game swapped a texture back, remove destroyed objects.
            foreach (var target in _targets.Values)
            {
                Verify(target, now);
                if (!wasInScene.TryGetValue(target.Id, out bool before) || before != target.InScene) RefreshStatusText(target);
            }
        }

        private RecolorTarget TargetByTextureName(Renderer renderer)
        {
            var shared = renderer.sharedMaterials;
            if (shared == null) return null;
            foreach (var material in shared)
            {
                if (material == null) continue;
                foreach (var prop in BaseMapProperties)
                {
                    if (!material.HasProperty(prop)) continue;
                    var texture = material.GetTexture(prop);
                    if (texture == null || string.IsNullOrEmpty(texture.name)) continue;
                    foreach (var target in _targets.Values)
                    {
                        if (target.Id == PlayerTargetId || target.Id.Length < 3) continue;
                        if (texture.name.IndexOf(target.Id, StringComparison.OrdinalIgnoreCase) >= 0) return target;
                    }
                }
            }
            return null;
        }

        private static string CleanPrefabName(string goName)
        {
            string n = goName.Replace("(Clone)", "").Trim();
            if (n.StartsWith("Dragon", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6);
            int cut = n.IndexOf('_');
            if (cut > 0) n = n.Substring(0, cut);
            return string.IsNullOrEmpty(n) ? goName : n;
        }

        // Held tools, tool anchors and UI are never recolored
        private static bool IsForeignObject(Renderer renderer)
        {
            try
            {
                return renderer.GetComponentInParent<ToolModel>(true) != null
                    || renderer.GetComponentInParent<Canvas>(true) != null
                    || renderer.GetComponentInParent<ToolTest>(true) != null;
            }
            catch { return false; }
        }

        private static bool IsPlayerMaterial(string materialName)
        {
            return NameContainsAny(materialName, PlayerMaterials);
        }

        private static bool IsExcluded(string materialName)
        {
            return NameContainsAny(materialName, ExcludedMaterials);
        }

        private static bool NameContainsAny(string name, string[] fragments)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var fragment in fragments)
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void BindRenderer(RecolorTarget target, Renderer renderer, bool playerRules, float now)
        {
            if (renderer == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) return;
            if (IsForeignObject(renderer)) return;
            var shared = renderer.sharedMaterials;
            if (shared == null || shared.Length == 0) return;
            Mesh mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : (renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null);
            bool meshHasColors = false;
            try { meshHasColors = mesh != null && mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color); } catch { }
            Material[] instances = null;
            bool changed = false;

            for (int i = 0; i < shared.Length; i++)
            {
                var material = shared[i];
                if (material == null || material.shader == null) continue;
                if (material.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                if (IsExcluded(material.name)) continue;

                bool vertexTint = material.HasProperty(VertexTintProperty) && material.GetFloat(VertexTintProperty) > 0.5f;
                string baseProperty = null;
                Texture2D baseMap = null;
                foreach (var prop in BaseMapProperties)
                {
                    if (!material.HasProperty(prop)) continue;
                    baseProperty = prop;
                    baseMap = material.GetTexture(prop) as Texture2D;
                    if (baseMap != null) break;
                }

                if (playerRules && !IsPlayerMaterial(material.name)) continue;

                bool useVertexColors = false;
                if (vertexTint && meshHasColors)
                {
                    if (baseMap == null) useVertexColors = true;
                    else
                    {
                        // In case both textures and vertex colors are used
                        float meshChroma = MeasureMeshChroma(mesh);
                        float textureChroma = MeasureTextureChroma(baseMap);
                        useVertexColors = meshChroma >= textureChroma;
                    }
                }

                if (useVertexColors)
                {
                    int meshId = mesh.GetInstanceID();
                    if (_boundMeshIds.Contains(meshId)) continue;
                    var binding = MeshBinding.Create(mesh);
                    _boundMeshIds.Add(meshId);
                    target.Meshes[meshId] = binding;
                    changed = true;
                    if (binding.Error != null) Logger.Warning(target.DisplayName + ": vertex colors of " + mesh.name + " (" + renderer.name + ") unavailable: " + binding.Error);
                    if (binding.IsValid && !target.IsIdentity) target.MeshesDirty = true;
                    continue;
                }

                if (baseMap == null) continue;
                if (instances == null) instances = renderer.materials;
                if (i >= instances.Length || instances[i] == null) continue;
                var instance = instances[i];
                int materialId = instance.GetInstanceID();
                if (_boundMaterialIds.Contains(materialId)) continue;
                var currentTexture = instance.GetTexture(baseProperty) as Texture2D ?? baseMap;
                _boundMaterialIds.Add(materialId);
                var materialBinding = new MaterialBinding
                {
                    Renderer = renderer,
                    Material = instance,
                    Property = baseProperty,
                    Original = currentTexture,
                    SourceId = currentTexture.GetInstanceID(),
                    Description = renderer.name + "/" + instance.name + " " + baseProperty + " = " + currentTexture.name + " " + currentTexture.width + "x" + currentTexture.height,
                };
                target.Materials[materialId] = materialBinding;
                changed = true;
                if (!target.IsIdentity) target.RequestFull(now);
            }
            if (changed) RefreshStatusText(target);
        }

        private float MeasureMeshChroma(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (_meshChroma.TryGetValue(id, out float cached)) return cached;
            float chroma = 0f;
            try
            {
                if (mesh.isReadable)
                {
                    var colors = mesh.colors32;
                    chroma = AverageChroma(colors, Mathf.Max(1, colors.Length / 512));
                }
                else
                {
                    var probe = MeshBinding.Create(mesh);
                    chroma = probe.IsValid ? probe.MeasureChroma() : 0f;
                    probe.DisposeWithoutRestore();
                }
            }
            catch (Exception e) { Logger.Debug("Mesh chroma of " + mesh.name + " unavailable: " + e.Message); }
            _meshChroma[id] = chroma;
            return chroma;
        }

        private float MeasureTextureChroma(Texture2D texture)
        {
            int id = texture.GetInstanceID();
            if (_textureChroma.TryGetValue(id, out float cached)) return cached;
            float chroma = 0f;
            if (TextureReadback.TryGet(texture, MaxTextureSize, out var source, out string error))
                chroma = AverageChroma(source.Pixels, Mathf.Max(1, source.Pixels.Length / 4096));
            else
                Logger.Warning(error);
            _textureChroma[id] = chroma;
            return chroma;
        }

        internal static float AverageChroma(Color32[] colors, int step)
        {
            if (colors == null || colors.Length == 0) return 0f;
            long sum = 0; int count = 0;
            for (int i = 0; i < colors.Length; i += step)
            {
                var c = colors[i];
                int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                sum += max - min;
                count++;
            }
            return count == 0 ? 0f : sum / (255f * count);
        }

        private void Verify(RecolorTarget target, float now)
        {
            var deadMaterials = new List<int>();
            foreach (var pair in target.Materials)
            {
                var mb = pair.Value;
                if (mb.Material == null || mb.Renderer == null) { deadMaterials.Add(pair.Key); continue; }
                var current = mb.Material.GetTexture(mb.Property);
                Texture expected = mb.Assigned != null ? mb.Assigned : mb.Original;
                if (current == expected || current == null) continue;
                if (mb.Assigned != null && current == mb.Original)
                {
                    // Reassign recolored texture
                    mb.Material.SetTexture(mb.Property, mb.Assigned);
                    continue;
                }
                if (target.IsOutputTexture(current)) continue;
                // The game switched to another texture (like a different dirt state), treat it as the new original
                var replaced = current as Texture2D;
                if (replaced == null) continue;
                mb.Original = replaced;
                mb.SourceId = replaced.GetInstanceID();
                mb.Assigned = null;
                if (!target.IsIdentity) target.RequestFull(now);
            }
            foreach (var id in deadMaterials) { target.Materials.Remove(id); _boundMaterialIds.Remove(id); }

            foreach (var output in target.Outputs.Values)
                if (output.Output != null && !output.Busy && !output.Output.IsCreated() && output.HasAppliedParams) { output.HasAppliedParams = false; if (!target.IsIdentity) target.RequestFull(now); }
            foreach (var output in target.Previews.Values)
                if (output.Output != null && !output.Busy && !output.Output.IsCreated() && output.HasAppliedParams) output.HasAppliedParams = false;

            if (target.IsIdentity && target.JobsRunning == 0 && now - target.LastChangeAt > IdleDisposeSeconds) target.DisposeOutputs();
            else target.DisposeUnusedOutputs();

            var deadMeshes = new List<int>();
            foreach (var pair in target.Meshes) if (pair.Value.Mesh == null) deadMeshes.Add(pair.Key);
            foreach (var id in deadMeshes) { target.Meshes.Remove(id); _boundMeshIds.Remove(id); }
        }

        // Rewrites the vertex colors of every bound mesh
        private void ApplyMeshes(RecolorTarget target)
        {
            target.MeshesDirty = false;
            if (target.Meshes.Count == 0) return;
            var p = target.Params;
            var matrix = p.IsIdentity ? null : new ColorMatrix(p);
            MeshApplies++;
            foreach (var mesh in target.Meshes.Values)
            {
                if (!mesh.IsValid) continue;
                if (matrix == null) mesh.Restore(); else mesh.Apply(matrix);
                if (mesh.Error != null) Logger.Warning(target.DisplayName + ": " + mesh.Name + ": " + mesh.Error);
            }
        }

        private void ApplyTextures(RecolorTarget target, float now)
        {
            if (target.Materials.Count == 0)
            {
                target.PreviewDirty = false;
                target.FullDirty = false;
                RefreshStatusText(target);
                return;
            }

            var p = target.Params;
            if (p.IsIdentity)
            {
                // Reset to original textures
                target.PreviewDirty = false;
                target.FullDirty = false;
                foreach (var mb in target.Materials.Values) Assign(mb, null);
                RefreshStatusText(target);
                return;
            }

            EnsureOutputs(target);
            if (target.PreviewDirty && now >= target.NextPreviewAt)
            {
                // Preview rate limit
                target.PreviewDirty = false;
                target.NextPreviewAt = now + 1f / PreviewRate;
                foreach (var preview in target.Previews.Values) StartTextureJob(target, preview, p);
            }
            if (target.FullDirty && now >= target.FullApplyAt)
            {
                target.FullDirty = false;
                foreach (var output in target.Outputs.Values) StartTextureJob(target, output, p);
            }
            RefreshStatusText(target);
        }

        private void EnsureOutputs(RecolorTarget target)
        {
            foreach (var mb in target.Materials.Values)
            {
                if (mb.Material == null || mb.Original == null || target.Outputs.ContainsKey(mb.SourceId)) continue;
                if (_unreadableTextures.Contains(mb.SourceId)) continue;
                if (!TextureReadback.TryGet(mb.Original, MaxTextureSize, out var source, out string error))
                {
                    _unreadableTextures.Add(mb.SourceId);
                    Logger.Warning(target.DisplayName + ": " + error);
                    continue;
                }
                target.Outputs[mb.SourceId] = new RecoloredTexture(source, false, 0);
            }
            foreach (var pair in target.Outputs)
            {
                var source = pair.Value.Source;
                if (target.Previews.ContainsKey(pair.Key) || !source.NeedsPreview(PreviewSize)) continue;
                target.Previews[pair.Key] = new RecoloredTexture(source, true, PreviewSize);
            }
        }

        private void StartTextureJob(RecolorTarget target, RecoloredTexture output, ColorAdjustParams p)
        {
            if (output.Busy) return;
            if (output.HasAppliedParams && output.AppliedParams.Equals(p) && output.Output != null) { AssignOutput(target, output); return; }

            output.Busy = true;
            int version = ++output.Version;
            target.JobsRunning++;
            if (output.IsPreview) PreviewJobs++; else FullJobs++;
            var matrix = new ColorMatrix(p);
            var source = output.Source;
            var work = output.Work;
            bool preview = output.IsPreview;
            int previewSize = output.PreviewSize;
            Task.Run(() =>
            {
                Exception error = null;
                bool skipped = false;
                try
                {
                    var pixels = preview ? source.EnsurePreview(previewSize) : source.Pixels;
                    if (pixels == null || work == null || pixels.Length != work.Length) skipped = true;
                    else matrix.Apply(pixels, work);
                }
                catch (Exception e) { error = e; }
                _mainThread.Enqueue(() => OnTextureJobDone(target, output, version, p, error, skipped));
            });
        }

        private void OnTextureJobDone(RecolorTarget target, RecoloredTexture output, int version, ColorAdjustParams p, Exception error, bool skipped)
        {
            bool alive = target.Outputs.ContainsValue(output) || target.Previews.ContainsValue(output);
            if (!alive) { FinishJob(target, output); output.Dispose(); return; }
            if (error != null)
            {
                FinishJob(target, output);
                Logger.Exception(error, "Recoloring " + output.Description + " failed");
                RefreshStatusText(target);
                return;
            }
            if (version != output.Version || skipped)
            {
                FinishJob(target, output);
                RefreshStatusText(target);
                return;
            }

            // The result goes to the GPU piece by piece over the next frames, the output stays busy until then.
            try
            {
                output.BeginUpload(p);
            }
            catch (Exception e)
            {
                output.AbortUpload();
                FinishJob(target, output);
                Logger.Exception(e, "Upload of " + output.Description + " failed");
                RefreshStatusText(target);
            }
        }

        private static void FinishJob(RecolorTarget target, RecoloredTexture output)
        {
            output.Busy = false;
            target.JobsRunning = Mathf.Max(0, target.JobsRunning - 1);
        }

        // Continues the partial uploads of finished texture jobs. Called once per frame
        private void StepUploads(RecolorTarget target)
        {
            if (target.Previews.Count > 0) StepUploads(target, target.Previews);
            if (target.Outputs.Count > 0) StepUploads(target, target.Outputs);
        }

        private void StepUploads(RecolorTarget target, Dictionary<int, RecoloredTexture> outputs)
        {
            foreach (var output in outputs.Values)
            {
                if (!output.Uploading) continue;
                bool complete;
                try
                {
                    complete = output.UploadStep(UploadBytesPerFrame);
                }
                catch (Exception e)
                {
                    output.AbortUpload();
                    FinishJob(target, output);
                    Logger.Exception(e, "Uploading " + output.Description + " failed");
                    RefreshStatusText(target);
                    continue;
                }
                if (complete) FinishUpload(target, output);
                else if (!output.Uploading) { FinishJob(target, output); RefreshStatusText(target); }
            }
        }

        private void FinishUpload(RecolorTarget target, RecoloredTexture output)
        {
            var p = output.PendingParams;
            output.AppliedParams = p;
            output.HasAppliedParams = true;
            FinishJob(target, output);
            if (!target.IsIdentity) AssignOutput(target, output);

            // The slider moved in the meantime, request another pass
            if (!target.IsIdentity && !target.Params.Equals(p))
            {
                if (output.IsPreview) target.PreviewDirty = true;
                else target.FullDirty = true;
            }
            RefreshStatusText(target);
        }

        private static void AssignOutput(RecolorTarget target, RecoloredTexture output)
        {
            if (output.Output == null) return;
            if (output.IsPreview && target.Outputs.TryGetValue(output.Source.InstanceId, out var full)
                && full.Output != null && full.HasAppliedParams && full.AppliedParams.Equals(target.Params)) return;
            foreach (var mb in target.Materials.Values)
            {
                if (mb.Material == null || mb.SourceId != output.Source.InstanceId) continue;
                Assign(mb, output.Output);
            }
        }

        private static void Assign(MaterialBinding mb, Texture texture)
        {
            if (mb.Material == null) return;
            Texture wanted = texture != null ? texture : mb.Original;
            if (wanted != null) mb.Material.SetTexture(mb.Property, wanted);
            mb.Assigned = texture;
        }
    }
}
