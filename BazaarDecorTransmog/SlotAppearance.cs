using System.Reflection;
using System.Text.Json;
using BokuMono;
using BokuMono.Data;
using HarmonyLib;
using UnityEngine;
using Category = BokuMono.BazaarCustomItemData.PartsCategory;
using UObject = UnityEngine.Object;

namespace BazaarDecorTransmog;

internal sealed class SlotAppearance
{
    private bool Enabled => AppearanceSession.Enabled;
    private readonly int slot;
    internal readonly Category PartCategory;
    internal int Index => slot;
    private VisualSlot binding;
    internal SlotAppearance(int index, Category category = Category.OrnamentS) { slot = index; PartCategory = category; }
    internal void Bind(VisualSlot value)
    {
        if (SameBinding(binding, value)) return;
        generation++;
        pending = false;
        decisionFeedbackPending = false;
        focusAfterVisualId = 0;
        binding = value;
        missingVisualIdentity = false;
        attemptedInstance = 0;
        attemptedVisualId = 0;
        // Keep the current replacement visible while another replacement is loading. This
        // makes list and tab navigation seamless. A vanilla binding still restores at once.
        if (value == null || !value.IsReplacement) Restore("binding cleared or non-replacement mode");
    }

    private static bool SameBinding(VisualSlot left, VisualSlot right) =>
        ReferenceEquals(left, right) || (left != null && right != null &&
        left.Index == right.Index && left.Category == right.Category &&
        left.ItemId == right.ItemId && left.ModelName == right.ModelName && left.Mode == right.Mode);
    private BazaarMyShop shop;
    private GameObject applied;
    private GameObject visualObject;
    private readonly List<(Renderer Renderer, bool Enabled)> originalRenderers = new();
    private GameObject maskedSource;
    private readonly List<(Renderer Renderer, bool Enabled)> maskedRenderers = new();
    private MeshFilter shelfFilter;
    private MeshRenderer shelfRenderer;
    private Mesh shelfMesh;
    private Material[] shelfMaterials;
    private uint appliedVisualId;
    private uint focusAfterVisualId;
    private bool focused, decisionFeedbackPending;
    private Transform liftedTransform;
    private Vector3 liftedBasePosition;
    private Vector3 liftAnimationFrom, liftAnimationTo;
    private float liftAnimationElapsed;
    private int liftAnimationFrame;
    private bool liftAnimating, clearLiftAfterAnimation;
    private const float FocusLift = 0.45f;
    private const float EditorFocusLift = 0.5f;
    private const float FocusLiftDuration = 0.14f;
    private const float MaxFocusAnimationStep = 1f / 30f;
    private const float DecisionDropDuration = 0.10f;
    private Transform editorFocusSource;
    private float editorGroundWorldY;
    private float nativeTransitionOverrideUntil;
    private Transform decisionSettleSource;
    private float decisionDropStarted;
    private float decisionDropInitialLift;
    private int generation, attemptedInstance;
    private uint attemptedVisualId;
    private bool pending;
    private bool missingVisualIdentity;
    private float nextCheck;
    private float requestStarted;

    internal void Observe(BazaarMyShop instance)
    {
        if (shop == instance) return;
        Restore("shop changed");
        shop = instance;
        attemptedInstance = 0;
        attemptedVisualId = 0;
    }

    internal void Suspend()
    {
        SetFocused(false);
        Restore("vanilla editor");
        attemptedInstance = 0;
        attemptedVisualId = 0;
    }

    // Leaving the appearance editor destroys its temporary visual without replacing the
    // native slot model. A later entry can therefore see the same model instance and the
    // same visual ID; its old "already requested" record must not suppress a new preview.
    internal void ResetPreviewAttempt()
    {
        attemptedInstance = 0;
        attemptedVisualId = 0;
        nextCheck = 0f;
    }

    internal void InvalidateActualModel(string reason)
    {
        Restore(reason);
        attemptedInstance = 0;
        attemptedVisualId = 0;
        nextCheck = 0f;
    }

    internal void SetFocused(bool value)
    {
        decisionFeedbackPending = false;
        focused = value;
        if (!value)
        {
            focusAfterVisualId = 0;
            nativeTransitionOverrideUntil = 0f;
            AnimateFocusReturn();
        }
        else ApplyFocusOffset();
    }

    internal bool IsEditorHovered => focused;

    internal void SetEditorPoseImmediate(bool hovered)
    {
        decisionFeedbackPending = false;
        focused = hovered;
        focusAfterVisualId = 0;
        nativeTransitionOverrideUntil = 0f;
        if (decisionSettleSource != null)
        {
            try { decisionSettleSource.localPosition = EditorPositionAtHeight(decisionSettleSource, 0f); }
            catch (Exception) { }
            decisionSettleSource = null;
        }
        RestoreFocusOffsetImmediate();
        if (editorFocusSource != null)
        {
            try
            {
                editorFocusSource.localPosition = EditorPositionAtHeight(editorFocusSource,
                    hovered ? EditorFocusLift : 0f);
                if (hovered)
                {
                    liftedTransform = editorFocusSource;
                    liftedBasePosition = EditorPositionAtHeight(editorFocusSource, 0f);
                }
            }
            catch (Exception) { }
        }
    }

    internal void SetFocusedAfterVisual(uint visualId)
    {
        decisionFeedbackPending = false;
        // While browsing, the slot is already hovering. Keep that height through the
        // visual swap; only a model that was grounded by a decision should lift after
        // the incoming appearance has replaced it.
        if (focused)
        {
            focusAfterVisualId = 0;
            return;
        }
        if (applied != null && appliedVisualId == visualId &&
            (PartCategory == Category.Shelf ? shelfFilter != null : visualObject != null))
        {
            SetFocused(true);
            return;
        }
        // Keep the outgoing appearance grounded while the incoming model loads.
        // Applying the new mesh/clone will start the hover after the visual swap.
        focused = false;
        focusAfterVisualId = visualId;
        RestoreFocusOffsetImmediate();
    }

    internal void BeginEditorFocus(bool nativeRaised)
    {
        RestoreFocusOffsetImmediate();
        editorFocusSource = null;
        CaptureEditorGroundPosition();
        if (nativeRaised && editorFocusSource != null &&
            Mathf.Abs(editorFocusSource.position.y - (editorGroundWorldY + EditorFocusLift)) > 0.05f)
        {
            editorFocusSource.localPosition = EditorPositionAtHeight(editorFocusSource, EditorFocusLift);
            nativeTransitionOverrideUntil = Time.unscaledTime + 0.6f;
        }
    }

    private void CaptureEditorGroundPosition()
    {
        Transform source;
        try { source = CurrentModel()?.transform; }
        catch (Exception) { return; } // A stock tab change can destroy the old IL2CPP root.
        if (source == null || editorFocusSource == source) return;
        editorFocusSource = source;
        // The stock hover moves a parent of the model. Its local Y can still be zero
        // while its world Y is already raised by 0.5, so local Y alone is insufficient.
        // Bazaar edit slots sit on the same Y=0 plane. The native focus parent can be
        // raised, grounded, or mid-animation when appearance mode is entered; its current
        // height is therefore never a reliable baseline.
        editorGroundWorldY = 0f;
    }

    internal void PlayDecisionFeedback(bool includeActual = false)
    {
        focusAfterVisualId = 0;
        focused = false;
        nativeTransitionOverrideUntil = 0f;
        bool followsActual = binding == null || binding.IsActual ||
            binding.IsHidden && !AppearanceSession.AllowsHidden(PartCategory);
        if (missingVisualIdentity || !(binding?.IsReplacement == true || includeActual && followsActual))
        {
            // Individual Actual/Hidden selection keeps its existing behavior. Preset
            // loads may animate visible native roots, but never hidden geometry.
            decisionFeedbackPending = false;
            decisionSettleSource = null;
            RestoreFocusOffsetImmediate();
            return;
        }
        RestoreFocusOffsetImmediate();
        decisionFeedbackPending = true;
        TryPlayDecisionFeedback();
    }

    internal void Tick(bool immediate = false)
    {
        UpdateDecisionDrop();
        UpdateFocusAnimation();
        // New field roots can arrive between two periodic checks. Check an unbound source
        // every frame so its native renderer is masked before the next rendered frame.
        if (!immediate && Time.unscaledTime < nextCheck && (applied != null || pending || !AppearanceSession.Enabled)) return;
        nextCheck = Time.unscaledTime + 0.5f;
        if (!AppearanceSession.Enabled || shop == null || binding == null)
        {
            if (applied != null || pending || visualObject != null) Restore("disabled or shop unloaded");
            attemptedInstance = 0;
            attemptedVisualId = 0;
            return;
        }
        var manager = shop.BM;
        if (manager == null) return;
        if (manager.IsCustomMode && !AppearanceSession.EditorPreview)
        {
            if (applied != null || pending) Suspend();
            return;
        }
        // A loaded master cannot resolve this binding. Keep vanilla visible and
        // retry only when the appearance changes (Bind) or the Mod restarts.
        if (missingVisualIdentity) return;
        if (pending && Time.unscaledTime - requestStarted > 20f)
        {
            generation++;
            pending = false;
            UnmaskSource();
            Reject("Visual request timed out");
        }
        var model = CurrentModel();
        if (maskedSource != null && maskedSource != model) UnmaskSource();
        if (visualObject != null && (applied == null || applied != model)) Restore("source model removed");
        if (model == null || !model.activeInHierarchy)
        {
            if (applied != null || pending) Restore("model inactive");
            attemptedInstance = 0;
            attemptedVisualId = 0;
            return;
        }
        if (applied != null && applied != model) Restore("model replaced");
        if (binding.IsActual || binding.IsHidden && !AppearanceSession.AllowsHidden(PartCategory))
        {
            if (maskedSource != null) UnmaskSource();
            return;
        }
        if (binding.IsHidden)
        {
            if (applied != null || visualObject != null) Restore("hidden appearance mode");
            MaskSource(model);
            return;
        }
        uint resolved = ResolveVisual();
        if (resolved == 0)
        {
            // Master initialization is temporary, not evidence of invalid data.
            var parts = BokuMono.API.Bazaar.MDM?.CustomPartsMaster?.list;
            if (parts == null || parts.Count == 0) return;
            Restore("visual identity unavailable");
            missingVisualIdentity = true;
            Reject("Visual identity missing or ambiguous");
            return;
        }
        if ((applied == model && appliedVisualId == resolved) || pending ||
            (attemptedInstance == model.GetInstanceID() && attemptedVisualId == resolved)) return;
        if (manager.customData == null || manager.buffParam?.CustomPartsBuff == null) return;
        var mdm = BokuMono.API.Bazaar.MDM;
        uint actualId = ActualId(manager);
        var actualPart = mdm.CustomPartsMaster.GetMasterData(actualId);
        if (actualPart == null || actualPart.Category != PartCategory) return;
        // Editor exit may return before its asynchronous model restoration finishes.
        if (model.name != actualPart.modelName && model.name != actualPart.modelName + "(Clone)")
        {
            return;
        }
        attemptedInstance = model.GetInstanceID();
        attemptedVisualId = resolved;
        var target = mdm.CustomPartsMaster.GetMasterData(resolved);
        if (actualId == 0) { Reject("Empty slot unsupported in prototype"); return; }
        if (!HasVisualGeometry(model)) { Reject("Actual model has no supported render geometry"); return; }
        if (!manager.IsCustomMode) MaskSource(model);
        pending = true;
        requestStarted = Time.unscaledTime;
        int ticket = ++generation;
        try
        {
            shop.RM.GetLoadCustomPartsModelData(target.modelName, (Il2CppSystem.Action<bool, GameObject>)((ok, prefab) =>
            {
                Plugin.Guard("transmog-callback", () =>
                {
                    if (ticket != generation) return;
                    pending = false;
                    if (!AppearanceSession.Enabled || shop == null || (shop.BM.IsCustomMode && !AppearanceSession.EditorPreview) || CurrentModel() != model || !model.activeInHierarchy ||
                        ActualId(shop.BM) != actualId) { UnmaskSource(); return; }
                    bool supported = ok && prefab != null &&
                        (PartCategory == Category.Shelf ? ShelfVisualRoot(prefab) : StaticVisualTree(prefab));
                    if (!supported) { UnmaskSource(); Reject("Visual load failed or unsupported hierarchy"); return; }
                    try { Apply(model, prefab, shop.BM, actualId, resolved); }
                    catch { UnmaskSource(); Reject("Apply failed; see DiagnosticError"); throw; }
                }, () => Restore("visual callback failed"));
                if (ticket == generation && pending) { pending = false; UnmaskSource(); Reject("Callback failed; see DiagnosticError"); }
            }), CacheLevel.Permanent);
        }
        catch { pending = false; UnmaskSource(); throw; }
    }

    private void MaskSource(GameObject model)
    {
        if (maskedSource == model) return;
        UnmaskSource();
        maskedSource = model;
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            // Shelf child renderers include the products, which remain native.
            if (PartCategory == Category.Shelf && renderer != ShelfVisualNode(model)?.GetComponent<MeshRenderer>()) continue;
            maskedRenderers.Add((renderer, renderer.enabled));
            renderer.enabled = false;
        }
    }

    private void UnmaskSource()
    {
        if (maskedRenderers.Count == 0) { maskedSource = null; return; }
        Recovery.Run(maskedRenderers.ToArray().Select(original => (Action)(() =>
        {
            if (original.Renderer != null) original.Renderer.enabled = original.Enabled;
            maskedRenderers.Remove(original);
        })));
        maskedSource = null;
    }

    private GameObject CurrentModel()
    {
        var models = shop?.putPartsModelDic;
        return models != null && models.TryGetValue(PartCategory, out var slots) && slots != null &&
            slot >= 0 && slot < slots.Length ? slots[slot] : null;
    }

    private uint ResolveVisual() => ResolveVisual(binding, PartCategory);

    internal static uint ResolveVisual(VisualSlot value, Category category)
    {
        if (value == null || !value.IsReplacement) return 0;
        var parts = BokuMono.API.Bazaar.MDM?.CustomPartsMaster?.list;
        if (parts == null) return 0;
        if (string.IsNullOrWhiteSpace(value.ModelName)) return 0;
        var match = new VisualIdentity.Match(value.ItemId, value.ModelName);
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Category == category) match.Consider(part.Id, part.modelName);
        }
        return match.Result;
    }

    internal uint ActualVisualId() => shop?.BM == null ? 0 : ActualId(shop.BM);

    private BazaarManager.CustomData CurrentData(BazaarManager manager) =>
        manager == null ? null : manager.IsCustomMode ? manager.editCustomData : manager.customData;

    private uint ActualId(BazaarManager manager)
    {
        var groups = CurrentData(manager)?.PutPartsDataDic;
        if (groups == null) return 0;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group != null && group.Category == PartCategory && group.DataDic != null && slot < group.DataDic.Count)
                return group.DataDic[slot];
        }
        return 0;
    }

    internal static bool HasVisualGeometry(GameObject model)
    {
        foreach (var node in model.GetComponentsInChildren<Transform>(true))
        {
            var meshRenderer = node.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                var filter = node.GetComponent<MeshFilter>();
                if (filter?.sharedMesh != null && filter.sharedMesh.vertexCount > 0) return true;
            }
            var skinned = node.GetComponent<SkinnedMeshRenderer>();
            if (skinned?.sharedMesh != null && skinned.sharedMesh.vertexCount > 0) return true;
        }
        return false;
    }

    internal static bool StaticVisualTree(GameObject model)
    {
        var nodes = model.GetComponentsInChildren<Transform>(true);
        if (nodes.Length > 128) return false;
        int renderers = 0;
        foreach (var node in nodes)
        {
            foreach (var component in node.gameObject.GetComponents<Component>())
            {
                if (component == null) return false;
                string type = component.GetIl2CppType().FullName;
                if (type != "UnityEngine.Transform" && type != "UnityEngine.MeshFilter" && type != "UnityEngine.MeshRenderer" &&
                    type != "UnityEngine.SkinnedMeshRenderer" && type != "UnityEngine.Animator" &&
                    type != "UnityEngine.BoxCollider" && type != "UnityEngine.SphereCollider" &&
                    type != "UnityEngine.CapsuleCollider" && type != "UnityEngine.MeshCollider") return false;
            }
            var renderer = node.GetComponent<Renderer>();
            var filter = node.GetComponent<MeshFilter>();
            var skinned = node.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null && filter == null) continue; // Container, animator, or collider-only node.
            var mesh = filter?.sharedMesh ?? skinned?.sharedMesh;
            if (renderer == null) return false;
            // Animated decorations can place an empty MeshRenderer on their root while
            // visible geometry is supplied by a child SkinnedMeshRenderer. The root is a
            // container, not a malformed draw call; the final renderer count below still
            // requires at least one fully valid visible mesh.
            if (mesh == null || mesh.vertexCount == 0) continue;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            foreach (var material in materials) if (material == null) return false;
            renderers++;
        }
        return renderers > 0;
    }

    internal static bool ShelfVisualRoot(GameObject model)
    {
        return ShelfVisualNode(model) != null;
    }

    internal static GameObject ShelfVisualNode(GameObject model)
    {
        if (model == null) return null;
        if (ValidShelfMeshNode(model)) return model;
        string expected = model.name.EndsWith("(Clone)", StringComparison.Ordinal)
            ? model.name.Substring(0, model.name.Length - "(Clone)".Length) : model.name;
        for (int i = 0; i < model.transform.childCount; i++)
        {
            var child = model.transform.GetChild(i)?.gameObject;
            if (child != null && child.name == expected && ValidShelfMeshNode(child)) return child;
        }
        return null;
    }

    private static bool ValidShelfMeshNode(GameObject model)
    {
        var filter = model.GetComponent<MeshFilter>();
        var renderer = model.GetComponent<MeshRenderer>();
        if (filter?.sharedMesh == null || filter.sharedMesh.vertexCount == 0 || renderer == null) return false;
        var materials = renderer.sharedMaterials;
        if (materials.Length == 0) return false;
        foreach (var material in materials) if (material == null) return false;
        return true;
    }

    private void Apply(GameObject model, GameObject prefab, BazaarManager manager, uint actualId, uint visualId)
    {
        UnmaskSource();
        if (PartCategory == Category.Shelf)
        {
            ApplyShelf(model, prefab, manager, actualId, visualId);
            return;
        }
        string before = Evidence(manager);
        bool replacing = applied == model && visualObject != null;
        if (!replacing)
        {
            originalRenderers.Clear();
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                originalRenderers.Add((renderer, renderer.enabled));
            applied = model;
        }
        GameObject replacement = null;
        try
        {
            // Both objects passed the strict visual-only component check. Keep the original
            // mesh/renderer intact, including native batching and per-renderer properties.
            // Keep the replacement below the native slot transform. The stock editor's
            // hover/drop animation now moves both models together, rather than having a
            // second transmog-only lift added on top of the native lift.
            replacement = UObject.Instantiate(prefab, model.transform, false);
            replacement.name = "BDT_Visual_" + visualId;
            foreach (var node in replacement.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = model.layer;
            replacement.transform.localPosition = Vector3.zero;
            replacement.transform.localRotation = prefab.transform.localRotation;
            replacement.transform.localScale = prefab.transform.localScale;
            replacement.SetActive(true);
            foreach (var collider in replacement.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            int visibleRenderers = 0;
            foreach (var visualRenderer in replacement.GetComponentsInChildren<Renderer>(true))
            {
                // Keep intentionally disabled child meshes disabled.
                visualRenderer.forceRenderingOff = false;
                if (visualRenderer.enabled && visualRenderer.gameObject.activeInHierarchy) visibleRenderers++;
            }
            if (visibleRenderers == 0) throw new InvalidOperationException("Visual has no active renderers");
            foreach (var original in originalRenderers) original.Renderer.enabled = false;
            var previous = visualObject;
            visualObject = replacement;
            replacement = null;
            appliedVisualId = visualId;
            if (previous != null) { previous.SetActive(false); UObject.Destroy(previous); }
            if (focusAfterVisualId == visualId) { focusAfterVisualId = 0; focused = true; }
            ApplyFocusOffset();
            string after = Evidence(manager);
            bool unchanged = before == after;
            if (!unchanged) { Restore("verification mismatch"); Reject("Verification failed; restored original"); return; }
            TryPlayDecisionFeedback();
        }
        catch
        {
            if (replacement != null) { replacement.SetActive(false); UObject.Destroy(replacement); }
            if (!replacing) Restore("apply failed");
            throw;
        }
    }

    private void ApplyShelf(GameObject model, GameObject prefab, BazaarManager manager, uint actualId, uint visualId)
    {
        string before = Evidence(manager);
        var sourceNode = ShelfVisualNode(model);
        var targetNode = ShelfVisualNode(prefab);
        var sourceFilter = sourceNode?.GetComponent<MeshFilter>();
        var sourceRenderer = sourceNode?.GetComponent<MeshRenderer>();
        var targetFilter = targetNode?.GetComponent<MeshFilter>();
        var targetRenderer = targetNode?.GetComponent<MeshRenderer>();
        if (sourceFilter?.sharedMesh == null || sourceRenderer == null || targetFilter?.sharedMesh == null || targetRenderer == null)
            throw new InvalidOperationException("Shelf root mesh is unavailable");
        bool replacing = applied == model && shelfFilter == sourceFilter;
        if (!replacing)
        {
            applied = model;
            shelfFilter = sourceFilter;
            shelfRenderer = sourceRenderer;
            shelfMesh = sourceFilter.sharedMesh;
            shelfMaterials = sourceRenderer.sharedMaterials.ToArray();
        }
        try
        {
            sourceFilter.sharedMesh = targetFilter.sharedMesh;
            sourceRenderer.sharedMaterials = targetRenderer.sharedMaterials;
            appliedVisualId = visualId;
            if (focusAfterVisualId == visualId) { focusAfterVisualId = 0; focused = true; }
            ApplyFocusOffset();
            string after = Evidence(manager);
            bool unchanged = before == after;
            if (!unchanged) { Restore("verification mismatch"); Reject("Verification failed; restored original"); return; }
            TryPlayDecisionFeedback();
        }
        catch
        {
            if (!replacing) Restore("shelf apply failed");
            throw;
        }
    }

    private string Evidence(BazaarManager manager)
    {
        var slots = new List<object>();
        var data = manager.customData.PutPartsDataDic;
        for (int g = 0; g < data.Count; g++)
            for (int i = 0; i < data[g].DataDic.Count; i++)
                slots.Add(new { category = data[g].Category.ToString(), index = i, id = data[g].DataDic[i] });
        var buff = manager.buffParam.CustomPartsBuff;
        var values = new List<object>();
        foreach (SeriesCategory series in Enum.GetValues<SeriesCategory>().Where(s => s != SeriesCategory.Max && s != SeriesCategory.All && s != SeriesCategory.None))
            foreach (ExtraSeriesCategory extra in Enum.GetValues<ExtraSeriesCategory>().Where(e => e != ExtraSeriesCategory.All))
                values.Add(new
                {
                    series = series.ToString(),
                    extra = extra.ToString(),
                    buy = buff.GetBuyCountValue(series, extra),
                    price = buff.GetPriceValue(series, extra),
                    happy = buff.GetHappyEnergyValue(series, extra),
                    freshness = buff.GetFreshnessUpValue(series, extra),
                    quality = buff.GetQualityUpValue(series, extra),
                    gauge = buff.GetBonusGaugeValue(series, extra)
                });
        var passives = new List<uint>();
        var active = buff.m_ActivePassiveParam;
        for (int i = 0; i < active.Count; i++) passives.Add(active[i].Id);
        var seriesIds = new List<uint>();
        var activeSeries = buff.m_ActiveSeriesParam;
        for (int i = 0; i < activeSeries.Count; i++) seriesIds.Add(activeSeries[i].Id);
        var edit = new List<object>();
        var editGroups = manager.editCustomData?.PutPartsDataDic;
        if (editGroups != null)
            for (int g = 0; g < editGroups.Count; g++)
                for (int i = 0; i < editGroups[g].DataDic.Count; i++)
                    edit.Add(new { category = editGroups[g].Category.ToString(), index = i, id = editGroups[g].DataDic[i] });
        return JsonSerializer.Serialize(new
        {
            slots,
            edit,
            passives,
            seriesIds,
            values,
            rich = buff.GetAddPopRichManValue(),
            trend = buff.GetUpgradeTrendValue()
        });
    }

    internal void Restore(string reason)
    {
        // Invalidate callbacks before any Unity access which can throw.
        generation++;
        pending = false;
        decisionFeedbackPending = false;
        focusAfterVisualId = 0;
        Recovery.Run(() =>
        {
            if (decisionSettleSource != null)
            {
                decisionSettleSource.localPosition = EditorPositionAtHeight(decisionSettleSource, 0f);
                decisionSettleSource = null;
            }
        }, RestoreFocusOffsetImmediate, UnmaskSource,
        () => Recovery.Run(originalRenderers.ToArray().Select(original => (Action)(() =>
        {
            if (original.Renderer != null) original.Renderer.enabled = original.Enabled;
            originalRenderers.Remove(original);
        }))), () =>
        {
            if (shelfFilter != null && shelfMesh != null) shelfFilter.sharedMesh = shelfMesh;
            shelfFilter = null;
            shelfMesh = null;
        }, () =>
        {
            if (shelfRenderer != null && shelfMaterials != null) shelfRenderer.sharedMaterials = shelfMaterials;
            shelfRenderer = null;
            shelfMaterials = null;
        }, () =>
        {
            if (visualObject != null) visualObject.SetActive(false);
        }, () =>
        {
            if (visualObject != null) UObject.Destroy(visualObject);
            visualObject = null;
        });
        applied = null;
        appliedVisualId = 0;
    }

    private Transform FocusTransform()
    {
        if (PartCategory == Category.Shelf) return CurrentModel()?.transform;
        return visualObject?.transform;
    }

    private void ApplyFocusOffset()
    {
        if (!focused) return;
        if (decisionSettleSource != null)
        {
            try { decisionSettleSource.localPosition = EditorPositionAtHeight(decisionSettleSource, 0f); }
            catch (Exception) { }
            decisionSettleSource = null;
        }
        if (AppearanceSession.EditorPreview) CaptureEditorGroundPosition();
        // Move the stock model root in the editor: the appearance is its child, so both
        // follow one hover height. The stock tab transition itself is suppressed because
        // it reloads the effect model for a frame before showing the next appearance.
        var target = AppearanceSession.EditorPreview ? editorFocusSource : FocusTransform();
        if (target == null) return;
        var ground = AppearanceSession.EditorPreview ? EditorPositionAtHeight(target, 0f) : target.localPosition;
        float height = AppearanceSession.EditorPreview ? EditorFocusLift : FocusLift;
        var hover = AppearanceSession.EditorPreview ? EditorPositionAtHeight(target, height) : ground + Vector3.up * height;
        if (liftedTransform == target)
        {
            if (clearLiftAfterAnimation)
                StartFocusAnimation(hover, false);
            return;
        }
        RestoreFocusOffsetImmediate();
        liftedTransform = target;
        liftedBasePosition = ground;
        StartFocusAnimation(hover, false);
    }

    private void AnimateFocusReturn()
    {
        if (liftedTransform == null) return;
        StartFocusAnimation(AppearanceSession.EditorPreview && liftedTransform == editorFocusSource
            ? EditorPositionAtHeight(liftedTransform, 0f) : liftedBasePosition, true);
    }

    private void StartFocusAnimation(Vector3 destination, bool clearAfter)
    {
        if (liftedTransform == null) return;
        liftAnimationFrom = liftedTransform.localPosition;
        liftAnimationTo = destination;
        liftAnimationElapsed = 0f;
        liftAnimationFrame = Time.frameCount;
        liftAnimating = true;
        clearLiftAfterAnimation = clearAfter;
    }

    private void UpdateFocusAnimation()
    {
        if (!liftAnimating) return;
        if (liftedTransform == null)
        {
            liftAnimating = false;
            clearLiftAfterAnimation = false;
            return;
        }
        // A first-use model/UI load can consume the whole hover duration before
        // another frame is drawn. Do not count that stall as visible animation,
        // or advance twice when an immediate slot check runs in the same frame.
        if (liftAnimationFrame == Time.frameCount) return;
        liftAnimationFrame = Time.frameCount;
        liftAnimationElapsed += Mathf.Min(Time.unscaledDeltaTime, MaxFocusAnimationStep);
        float progress = Mathf.Clamp01(liftAnimationElapsed / FocusLiftDuration);
        // SmoothStep gives the same soft acceleration/deceleration feel as the editor UI.
        float eased = progress * progress * (3f - 2f * progress);
        if (AppearanceSession.EditorPreview && liftedTransform == editorFocusSource)
            liftAnimationTo = EditorPositionAtHeight(liftedTransform, clearLiftAfterAnimation ? 0f : EditorFocusLift);
        liftedTransform.localPosition = Vector3.Lerp(liftAnimationFrom, liftAnimationTo, eased);
        if (progress < 1f) return;
        liftAnimating = false;
        if (!clearLiftAfterAnimation) return;
        liftedTransform = null;
        clearLiftAfterAnimation = false;
    }

    private void RestoreFocusOffsetImmediate()
    {
        if (liftedTransform != null)
            try
            {
                liftedTransform.localPosition = AppearanceSession.EditorPreview && liftedTransform == editorFocusSource
                ? EditorPositionAtHeight(liftedTransform, 0f) : liftedBasePosition;
            }
            catch (Exception) { }
        liftedTransform = null;
        liftAnimating = false;
        clearLiftAfterAnimation = false;
    }

    internal void UpdateDecisionDrop()
    {
        if (decisionSettleSource == null || focused) return;
        // Keep the landing at an absolute slot height even when PlayCustomAnim animates
        // the model locally or the stock focus parent changes height during the drop.
        float progress = Mathf.Clamp01((Time.unscaledTime - decisionDropStarted) / DecisionDropDuration);
        float lift = decisionDropInitialLift * (1f - progress * progress);
        try { decisionSettleSource.localPosition = EditorPositionAtHeight(decisionSettleSource, lift); }
        catch (Exception) { }
    }

    internal void HoldNativeTransitionFocus()
    {
        if (!focused || editorFocusSource == null || Time.unscaledTime >= nativeTransitionOverrideUntil) return;
        try { editorFocusSource.localPosition = EditorPositionAtHeight(editorFocusSource, EditorFocusLift); }
        catch (Exception) { }
    }

    private Vector3 EditorPositionAtHeight(Transform source, float lift)
    {
        var world = source.position;
        world.y = editorGroundWorldY + lift;
        var local = source.parent != null ? source.parent.InverseTransformPoint(world) : world;
        local.x = source.localPosition.x;
        local.z = source.localPosition.z;
        return local;
    }

    private void TryPlayDecisionFeedback()
    {
        if (!decisionFeedbackPending || shop == null) return;
        if (binding?.IsReplacement == true &&
            (applied == null || applied != CurrentModel() || appliedVisualId != ResolveVisual())) return;
        if (AppearanceSession.EditorPreview) CaptureEditorGroundPosition();
        // During editor preview the replacement is a child of the native model. Animate
        // that native root so the replacement follows the game's landing animation.
        var target = AppearanceSession.EditorPreview ? editorFocusSource : FocusTransform();
        if (target == null || !target.gameObject.activeInHierarchy || ActualId(shop.BM) == 0) return;
        decisionFeedbackPending = false;
        // The same guard covers synchronous and resource-callback entry points; a
        // failed cosmetic effect must not undo a successfully applied visual.
        Plugin.Guard("slot-landing:" + PartCategory + ":" + slot, () =>
        {
            if (AppearanceSession.EditorPreview)
            {
                // Start the stock effect and absolute-height correction only after the
                // incoming visual is ready. Starting the clock before an async load can
                // finish the drop before the new model is ever visible.
                liftedTransform = null;
                liftAnimating = false;
                clearLiftAfterAnimation = false;
                target.localPosition = EditorPositionAtHeight(target, EditorFocusLift);
                decisionSettleSource = target;
                decisionDropStarted = Time.unscaledTime;
                decisionDropInitialLift = EditorFocusLift;
            }
            shop.PlayCustomAnim(PartCategory, slot, target);
        }, () => SetEditorPoseImmediate(false));
    }

    private void Reject(string reason)
    {
        Plugin.Warn("TransmogSkipped", new { slot, reason });
    }

}
