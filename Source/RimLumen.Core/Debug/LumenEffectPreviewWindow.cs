using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using NumericVector3 = System.Numerics.Vector3;

namespace RimLumen.Core.Debug;

public sealed class LumenEffectPreviewWindow : Window
{
    private const float RowHeight = 40f;
    private const float ToolbarHeight = 30f;
    private const float DiagnosticsBoxHeight = 84f;

    private sealed class LogMessageReferenceComparer : IEqualityComparer<LogMessage>
    {
        public static readonly LogMessageReferenceComparer Instance = new();
        public bool Equals(LogMessage? x, LogMessage? y) => ReferenceEquals(x, y);
        public int GetHashCode(LogMessage obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly QuickSearchWidget searchWidget = new();
    private readonly List<ILumenEffectHandle> previewHandles = new();

    private IReadOnlyList<LumenEffectPreviewEntry> allEntries = Array.Empty<LumenEffectPreviewEntry>();
    private IReadOnlyList<LumenEffectPreviewEntry> filteredEntries = Array.Empty<LumenEffectPreviewEntry>();
    private LumenEffectPreviewEntry? selectedEntry;
    private string? selectedEffectId;
    private int lastCatalogCount = -1;

    private ILumenEffectHandle? currentHandle;
    private EffectAsset? currentHandleAsset;
    private int currentHandleMapId = -1;
    private string? currentStateName;
    private NumericVector3? lastPlayOrigin;
    private int lastPlayMapId = -1;
    private bool isPickingMapPosition;
    private string? errorText;

    private Vector2 listScrollPosition;
    private Vector2 detailScrollPosition;
    private Vector2 diagnosticsScrollPosition;
    private float lastDetailContentHeight = 200f;

    public LumenEffectPreviewWindow()
    {
        doCloseX = true;
        doCloseButton = false;
        draggable = true;
        resizeable = true;
        absorbInputAroundWindow = false;
        forcePause = false;
        preventCameraMotion = false;
        closeOnClickedOutside = false;
        closeOnAccept = false;
        closeOnCancel = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimLumen_PreviewWindowTitle".Translate();
        searchWidget.maxSearchTextLength = 256;
    }

    public override Vector2 InitialSize => new(700f, 540f);

    protected override void SetInitialSizeAndPosition()
    {
        Vector2 initialSize = InitialSize;
        float width = Mathf.Min(initialSize.x, Mathf.Max(420f, UI.screenWidth - 32f));
        float height = Mathf.Min(initialSize.y, Mathf.Max(320f, UI.screenHeight - 80f));
        windowRect = new Rect(16f, Mathf.Max(20f, (UI.screenHeight - height) * 0.5f), width, height);
    }

    public override void PreOpen()
    {
        base.PreOpen();
        LumenSceneBootstrap.EnsureInitialized();
        RefreshCatalogEntries(selectFirstIfEmpty: true);
    }

    public override void WindowUpdate()
    {
        base.WindowUpdate();
        PruneDeadHandles();
        if (isPickingMapPosition && !Find.Targeter.IsTargeting)
        {
            isPickingMapPosition = false;
        }
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap is null)
        {
            Close(doCloseSound: false);
        }
    }

    public override void PostClose()
    {
        StopAllPreviewInstances();
        base.PostClose();
    }

    public override void Notify_ClickOutsideWindow()
    {
        base.Notify_ClickOutsideWindow();
        searchWidget.Unfocus();
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap is not { } currentMap)
        {
            Close(doCloseSound: false);
            return;
        }

        if (LumenSceneBootstrap.Catalog.Count != lastCatalogCount)
        {
            RefreshCatalogEntries(selectFirstIfEmpty: false);
        }

        float curY = inRect.y;
        DrawToolbar(new Rect(inRect.x, curY, inRect.width, ToolbarHeight));
        curY += ToolbarHeight + 6f;

        if (!string.IsNullOrEmpty(errorText))
        {
            DrawDiagnosticsPanel(new Rect(inRect.x, curY, inRect.width, DiagnosticsBoxHeight), errorText!);
            curY += DiagnosticsBoxHeight + 6f;
        }

        float bodyHeight = Mathf.Max(120f, inRect.yMax - curY);
        const float gap = 10f;
        float leftWidth = Mathf.Floor((inRect.width - gap) * 0.50f);
        DrawEffectListColumn(new Rect(inRect.x, curY, leftWidth, bodyHeight));
        DrawDetailColumn(new Rect(inRect.x + leftWidth + gap, curY, inRect.width - gap - leftWidth, bodyHeight), currentMap);
    }

    private void DrawToolbar(Rect rect)
    {
        const float gap = 6f;
        float reloadWidth = Mathf.Min(128f, rect.width * 0.25f);
        float stopAllWidth = Mathf.Min(122f, rect.width * 0.24f);
        float searchWidth = Mathf.Max(60f, rect.width - reloadWidth - stopAllWidth - gap * 2f);

        Rect searchRect = new(rect.x, rect.y + Mathf.Max(0f, (rect.height - QuickSearchWidget.WidgetHeight) * 0.5f),
            searchWidth, QuickSearchWidget.WidgetHeight);
        searchWidget.OnGUI(searchRect, ApplyFilter);

        float x = searchRect.xMax + gap;
        if (Widgets.ButtonText(new Rect(x, rect.y, reloadWidth, rect.height), "RimLumen_PreviewReloadAssets".Translate()))
        {
            ReloadAssets();
        }

        x += reloadWidth + gap;
        PruneDeadHandles();
        if (Widgets.ButtonText(new Rect(x, rect.y, stopAllWidth, rect.height),
                "RimLumen_PreviewStopAll".Translate(previewHandles.Count),
                active: previewHandles.Count > 0 || isPickingMapPosition))
        {
            StopAllPreviewInstances();
        }
    }

    private void DrawDiagnosticsPanel(Rect rect, string message)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(6f);
        const float buttonWidth = 84f;
        const float gap = 6f;

        Rect textRect = new(inner.x, inner.y, Mathf.Max(80f, inner.width - buttonWidth - gap), inner.height);
        Color prevColor = GUI.color;
        GUI.color = new Color(1f, 0.45f, 0.45f);
        Widgets.LabelScrollable(textRect, message, ref diagnosticsScrollPosition, dontConsumeScrollEventsIfNoScrollbar: false);
        GUI.color = prevColor;

        if (Widgets.ButtonText(new Rect(textRect.xMax + gap, inner.y, buttonWidth, 28f), "RimLumen_CopyError".Translate()))
        {
            GUIUtility.systemCopyBuffer = message;
        }
    }

    private void DrawEffectListColumn(Rect rect)
    {
        const float headerHeight = 22f;
        Text.Font = GameFont.Small;
        Widgets.Label(
            new Rect(rect.x, rect.y, rect.width, headerHeight),
            "RimLumen_PreviewEffectListHeader".Translate(filteredEntries.Count, allEntries.Count));

        Rect listOuter = new(rect.x, rect.y + headerHeight + 2f, rect.width, Mathf.Max(60f, rect.height - headerHeight - 2f));
        Widgets.DrawMenuSection(listOuter);
        Rect inner = listOuter.ContractedBy(4f);

        if (filteredEntries.Count == 0)
        {
            TextAnchor prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(inner, "RimLumen_PreviewNoMatches".Translate());
            Text.Anchor = prevAnchor;
            return;
        }

        Rect viewRect = new(0f, 0f, inner.width - 16f, Mathf.Max(inner.height, filteredEntries.Count * RowHeight));
        Widgets.BeginScrollView(inner, ref listScrollPosition, viewRect);

        float curY = 0f;
        for (int i = 0; i < filteredEntries.Count; i++)
        {
            LumenEffectPreviewEntry entry = filteredEntries[i];
            Rect rowRect = new(0f, curY, viewRect.width, RowHeight);
            if (curY + RowHeight >= listScrollPosition.y - RowHeight && curY <= listScrollPosition.y + inner.height + RowHeight)
            {
                if (i % 2 == 1) Widgets.DrawAltRect(rowRect);
                if (string.Equals(entry.EffectId, selectedEffectId, StringComparison.Ordinal))
                    Widgets.DrawHighlightSelected(rowRect);
                else
                    Widgets.DrawHighlightIfMouseover(rowRect);

                Rect idRect = new(rowRect.x + 6f, rowRect.y + 3f, rowRect.width - 12f, 20f);
                Rect sourceRect = new(rowRect.x + 6f, rowRect.y + 21f, rowRect.width - 12f, 16f);

                Text.Font = GameFont.Small;
                Widgets.Label(idRect, Text.ClampTextWithEllipsis(idRect, entry.EffectId));

                Text.Font = GameFont.Tiny;
                Color prevColor = GUI.color;
                GUI.color = new Color(0.72f, 0.78f, 0.86f);
                string sourceSummary = FormatEntrySourceBadge(entry);
                Widgets.Label(sourceRect, Text.ClampTextWithEllipsis(sourceRect, sourceSummary));
                GUI.color = prevColor;
                Text.Font = GameFont.Small;

                string tipText = string.IsNullOrEmpty(entry.SourceDetail)
                    ? $"{entry.EffectId}\n{sourceSummary}"
                    : $"{entry.EffectId}\n{entry.SourceDetail}";
                TooltipHandler.TipRegion(rowRect, tipText);

                if (Widgets.ButtonInvisible(rowRect))
                {
                    SelectEntry(entry);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }
            }
            curY += RowHeight;
        }

        Widgets.EndScrollView();
    }

    private void DrawDetailColumn(Rect rect, Map currentMap)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);

        if (selectedEntry is null)
        {
            TextAnchor prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            string emptyText = string.IsNullOrEmpty(selectedEffectId)
                ? "RimLumen_PreviewSelectPrompt".Translate()
                : "RimLumen_PreviewMissingAfterReload".Translate(selectedEffectId);
            Widgets.Label(inner, emptyText);
            Text.Anchor = prevAnchor;
            return;
        }

        EffectAsset asset = selectedEntry.Asset;
        Rect viewRect = new(0f, 0f, inner.width - 16f, Mathf.Max(inner.height, lastDetailContentHeight));
        Widgets.BeginScrollView(inner, ref detailScrollPosition, viewRect);

        var listing = new Listing_Standard { maxOneColumn = true };
        listing.Begin(viewRect);

        bool prevWrap = Text.WordWrap;
        Text.WordWrap = true;
        Text.Font = GameFont.Medium;
        listing.Label(asset.EffectId);
        Text.Font = GameFont.Small;

        Color prevColor = GUI.color;
        GUI.color = new Color(0.78f, 0.84f, 0.90f);
        listing.Label("RimLumen_PreviewSourceLine".Translate(FormatEntrySourceDetail(selectedEntry)));
        GUI.color = prevColor;
        listing.GapLine(10f);

        const float gap = 6f;
        const float buttonHeight = 30f;
        Rect row1 = listing.GetRect(buttonHeight);
        float halfW = (row1.width - gap) * 0.5f;

        if (Widgets.ButtonText(new Rect(row1.x, row1.y, halfW, buttonHeight), "RimLumen_PreviewPlayAtCamera".Translate()))
        {
            NumericVector3 center = GetCameraCenterGround(currentMap);
            PlayAssetAt(asset, currentMap, center.X, center.Z, replaceCurrent: false);
        }

        string pickLabel = isPickingMapPosition
            ? "RimLumen_PreviewPickingMapPos".Translate()
            : "RimLumen_PreviewPickMapPos".Translate();
        if (Widgets.ButtonText(new Rect(row1.x + halfW + gap, row1.y, halfW, buttonHeight), pickLabel))
        {
            if (isPickingMapPosition) CancelMapPick();
            else BeginMapPositionPick(asset, currentMap);
        }

        listing.Gap(6f);
        if (listing.ButtonText("RimLumen_PreviewReplay".Translate()))
        {
            NumericVector3 origin = ResolveReplayGroundOrigin(currentMap);
            PlayAssetAt(asset, currentMap, origin.X, origin.Z, replaceCurrent: true);
        }

        if (asset.States.Length > 0)
        {
            listing.GapLine(12f);
            DrawStateMachineControls(listing, asset, currentMap);
        }

        Text.WordWrap = prevWrap;
        listing.End();
        lastDetailContentHeight = listing.CurHeight + 16f;
        Widgets.EndScrollView();
    }

    private void DrawStateMachineControls(Listing_Standard listing, EffectAsset asset, Map currentMap)
    {
        listing.Label("RimLumen_PreviewStatesHeader".Translate(asset.States.Length));
        listing.Gap(4f);

        bool hasLiveCurrent = IsCurrentHandleFor(asset, currentMap);
        const float btnHeight = 28f;
        const float gap = 6f;

        for (int i = 0; i < asset.States.Length; i += 2)
        {
            Rect row = listing.GetRect(btnHeight);
            if (i + 1 < asset.States.Length)
            {
                float halfW = (row.width - gap) * 0.5f;
                DrawStateButton(new Rect(row.x, row.y, halfW, btnHeight), asset, asset.States[i], currentMap, hasLiveCurrent);
                DrawStateButton(new Rect(row.x + halfW + gap, row.y, halfW, btnHeight), asset, asset.States[i + 1], currentMap, hasLiveCurrent);
            }
            else
            {
                DrawStateButton(row, asset, asset.States[i], currentMap, hasLiveCurrent);
            }
            listing.Gap(4f);
        }
    }

    private void DrawStateButton(Rect rect, EffectAsset asset, EffectStateSpec stateSpec, Map currentMap, bool hasLiveCurrent)
    {
        if (hasLiveCurrent && string.Equals(currentStateName, stateSpec.Name, StringComparison.Ordinal))
        {
            Widgets.DrawStrongHighlight(rect, new Color(0.35f, 0.72f, 0.45f, 0.35f));
        }

        if (Widgets.ButtonText(rect, stateSpec.Name))
        {
            ActivateState(asset, currentMap, stateSpec.Name);
        }
    }

    private void ActivateState(EffectAsset asset, Map currentMap, string stateName)
    {
        if (IsCurrentHandleFor(asset, currentMap))
        {
            currentHandle!.GoTo(stateName);
            return;
        }

        NumericVector3 origin = ResolveReplayGroundOrigin(currentMap);
        PlayAssetAt(asset, currentMap, origin.X, origin.Z, initialState: stateName, replaceCurrent: true);
    }

    private bool IsCurrentHandleFor(EffectAsset asset, Map map) =>
        currentHandle is { IsAlive: true }
        && ReferenceEquals(currentHandleAsset, asset)
        && currentHandleMapId == map.uniqueID;

    private void BeginMapPositionPick(EffectAsset asset, Map expectedMap)
    {
        isPickingMapPosition = true;
        Find.Targeter.BeginTargeting(
            TargetingParameters.ForCell(),
            action: target =>
            {
                isPickingMapPosition = false;
                if (Find.CurrentMap == expectedMap && target.Cell.InBounds(expectedMap))
                {
                    PlayAssetAt(asset, expectedMap, target.Cell.x + 0.5f, target.Cell.z + 0.5f, replaceCurrent: false);
                }
            },
            highlightAction: target =>
            {
                if (Find.CurrentMap == expectedMap && target.Cell.InBounds(expectedMap))
                {
                    GenDraw.DrawTargetHighlight(target);
                }
            },
            targetValidator: target => Find.CurrentMap == expectedMap && target.Cell.InBounds(expectedMap),
            caster: null,
            actionWhenFinished: () => isPickingMapPosition = false,
            mouseAttachment: null,
            playSoundOnAction: true,
            onGuiAction: _ => Widgets.MouseAttachedLabel("RimLumen_PreviewPickMapHint".Translate()),
            onUpdateAction: null);
    }

    private void CancelMapPick()
    {
        if (isPickingMapPosition && Find.Targeter.IsTargeting)
        {
            Find.Targeter.StopTargeting();
        }
        isPickingMapPosition = false;
    }

    private void PlayAssetAt(
        EffectAsset asset,
        Map map,
        float groundX,
        float groundZ,
        string? initialState = null,
        bool replaceCurrent = false)
    {
        if (replaceCurrent)
        {
            StopAndReleaseCurrentHandle();
        }

        lastPlayOrigin = new NumericVector3(groundX, 0f, groundZ);
        lastPlayMapId = map.uniqueID;
        errorText = null;

        LumenPlayRequest request = LumenEffectPreviewCatalog.BuildPlayRequest(
            asset,
            map.uniqueID,
            groundX,
            groundZ,
            initialState: initialState);

        ILumenEffectHandle handle;
        try
        {
            handle = RimLumenApi.Play(request);
        }
        catch (Exception ex)
        {
            Log.Error($"[RimLumen] Effect preview failed to play '{asset.EffectId}': {ex}");
            errorText = ex.Message;
            return;
        }

        if (!handle.IsAlive) return;

        DetachCurrentHandleEvents();
        previewHandles.Add(handle);
        currentHandle = handle;
        currentHandleAsset = asset;
        currentHandleMapId = map.uniqueID;
        currentStateName = string.IsNullOrEmpty(initialState) && asset.States.Length > 0
            ? (asset.InitialState ?? asset.States[0].Name)
            : initialState;
        handle.StateChanged += OnCurrentHandleStateChanged;
    }

    private void OnCurrentHandleStateChanged(string stateName) => currentStateName = stateName;

    private void DetachCurrentHandleEvents()
    {
        if (currentHandle is not null)
        {
            currentHandle.StateChanged -= OnCurrentHandleStateChanged;
        }
    }

    private void ClearCurrentHandleFields()
    {
        DetachCurrentHandleEvents();
        currentHandle = null;
        currentHandleAsset = null;
        currentHandleMapId = -1;
        currentStateName = null;
    }

    private void StopAndReleaseCurrentHandle()
    {
        if (currentHandle is null) return;
        ILumenEffectHandle old = currentHandle;
        ClearCurrentHandleFields();
        previewHandles.Remove(old);
        old.Stop(allowFadeOut: false);
        old.Dispose();
    }

    private void StopAllPreviewInstances()
    {
        CancelMapPick();
        ClearCurrentHandleFields();
        for (int i = 0; i < previewHandles.Count; i++)
        {
            previewHandles[i].Stop(allowFadeOut: false);
            previewHandles[i].Dispose();
        }
        previewHandles.Clear();
    }

    private void PruneDeadHandles()
    {
        for (int i = previewHandles.Count - 1; i >= 0; i--)
        {
            if (!previewHandles[i].IsAlive) previewHandles.RemoveAt(i);
        }

        if (currentHandle is not null && !currentHandle.IsAlive)
        {
            ClearCurrentHandleFields();
        }
    }

    private void ReloadAssets()
    {
        CancelMapPick();
        Dictionary<LogMessage, int> logBefore = SnapshotLogMessages();
        LumenAssetReloadResult result = LumenModAssetScanner.ReloadRunningModAssets(LumenSceneBootstrap.Catalog);

        // LumenAssetReloadResult reports only counts and alias changes; per-file validation details are written
        // to Verse.Log by DefaultJsonError/DefaultDefError, so scan only newly added/repeated Log.Messages.
        List<string> newErrors = CollectNewErrorMessages(logBefore);
        if (newErrors.Count > 0)
        {
            errorText = string.Join(Environment.NewLine, newErrors);
        }
        else if (result.Failed > 0)
        {
            errorText = "RimLumen_PreviewReloadFailedSeeLog".Translate(result.Failed);
        }
        else
        {
            errorText = null;
        }

        RefreshCatalogEntries(selectFirstIfEmpty: false);
        Messages.Message(
            "RimLumen_EffectAssetsReloaded".Translate(result.Loaded, result.Failed),
            result.Failed > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion,
            historical: false);
    }

    private void RefreshCatalogEntries(bool selectFirstIfEmpty)
    {
        InMemoryAssetCatalog catalog = LumenSceneBootstrap.Catalog;
        lastCatalogCount = catalog.Count;
        allEntries = LumenEffectPreviewCatalog.BuildEntries(catalog);
        ApplyFilter();

        LumenEffectPreviewEntry? matched = LumenEffectPreviewCatalog.SelectEntryAfterRefresh(
            allEntries,
            selectedEffectId,
            selectFirstIfEmpty);
        selectedEntry = matched;
        if (matched is not null)
        {
            selectedEffectId = matched.EffectId;
        }
    }

    private void ApplyFilter()
    {
        filteredEntries = LumenEffectPreviewCatalog.FilterEntries(allEntries, searchWidget.filter.Text);
        searchWidget.noResultsMatched = filteredEntries.Count == 0 && searchWidget.filter.Active;
    }

    private void SelectEntry(LumenEffectPreviewEntry entry)
    {
        if (!string.Equals(selectedEffectId, entry.EffectId, StringComparison.Ordinal))
        {
            CancelMapPick();
        }
        selectedEntry = entry;
        selectedEffectId = entry.EffectId;
    }

    private NumericVector3 ResolveReplayGroundOrigin(Map map)
        => lastPlayOrigin is { } origin && lastPlayMapId == map.uniqueID
            ? origin
            : GetCameraCenterGround(map);

    private static NumericVector3 GetCameraCenterGround(Map map)
    {
        Vector3 world = UI.UIToMapPosition(UI.screenWidth * 0.5f, UI.screenHeight * 0.5f);
        float maxX = Mathf.Max(0.5f, map.Size.x - 0.5f);
        float maxZ = Mathf.Max(0.5f, map.Size.z - 0.5f);
        return new NumericVector3(Mathf.Clamp(world.x, 0.5f, maxX), 0f, Mathf.Clamp(world.z, 0.5f, maxZ));
    }

    private static Dictionary<LogMessage, int> SnapshotLogMessages()
    {
        var snapshot = new Dictionary<LogMessage, int>(LogMessageReferenceComparer.Instance);
        foreach (LogMessage msg in Log.Messages)
        {
            snapshot[msg] = msg.repeats;
        }
        return snapshot;
    }

    private static List<string> CollectNewErrorMessages(Dictionary<LogMessage, int> before)
    {
        var errors = new List<string>();
        foreach (LogMessage msg in Log.Messages)
        {
            if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning) continue;
            if (!before.TryGetValue(msg, out int prevRepeats) || msg.repeats > prevRepeats)
            {
                errors.Add(msg.text);
            }
        }
        return errors;
    }

    private static string FormatEntrySourceBadge(LumenEffectPreviewEntry entry)
    {
        string shortDetail = LumenEffectPreviewCatalog.GetShortSourceDetail(entry);
        return entry.SourceKind switch
        {
            LumenEffectPreviewSourceKind.Json => string.IsNullOrEmpty(entry.PackageId)
                ? $"JSON · {shortDetail}"
                : $"JSON · {entry.PackageId} · {shortDetail}",
            LumenEffectPreviewSourceKind.XmlDef => string.IsNullOrEmpty(entry.PackageId)
                ? $"XML · {shortDetail}"
                : $"XML · {entry.PackageId} · {shortDetail}",
            LumenEffectPreviewSourceKind.Code => "RimLumen_PreviewSourceCode".Translate(),
            _ => entry.SourceDetail,
        };
    }

    private static string FormatEntrySourceDetail(LumenEffectPreviewEntry entry) => entry.SourceKind switch
    {
        LumenEffectPreviewSourceKind.Json => string.IsNullOrEmpty(entry.PackageId)
            ? $"JSON ({entry.SourceDetail})"
            : $"JSON · {entry.PackageId} ({entry.SourceDetail})",
        LumenEffectPreviewSourceKind.XmlDef => string.IsNullOrEmpty(entry.PackageId)
            ? $"XML ({entry.SourceDetail})"
            : $"XML · {entry.PackageId} ({entry.SourceDetail})",
        LumenEffectPreviewSourceKind.Code => "RimLumen_PreviewSourceCode".Translate(),
        _ => entry.SourceDetail,
    };
}
