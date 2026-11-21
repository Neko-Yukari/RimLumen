using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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
    private const float RowHeight = 42f;
    private const float ToolbarHeight = 30f;
    private const float DiagnosticsBoxHeight = 96f;

    private sealed class LogMessageReferenceComparer : IEqualityComparer<LogMessage>
    {
        public static readonly LogMessageReferenceComparer Instance = new();
        public bool Equals(LogMessage? x, LogMessage? y) => ReferenceEquals(x, y);
        public int GetHashCode(LogMessage obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly QuickSearchWidget searchWidget = new();
    private readonly List<ILumenEffectHandle> previewHandles = new();
    private readonly Dictionary<string, EffectAsset> resolvedExternalAssets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> externalQueryAliases = new(StringComparer.Ordinal);
    private readonly List<string> reloadDiagnostics = new();

    private IReadOnlyList<LumenEffectPreviewEntry> allEntries = Array.Empty<LumenEffectPreviewEntry>();
    private IReadOnlyList<LumenEffectPreviewEntry> filteredEntries = Array.Empty<LumenEffectPreviewEntry>();
    private LumenEffectPreviewEntry? selectedEntry;
    private string? selectedEffectId;
    private int lastCatalogCount = -1;

    private ILumenEffectHandle? currentHandle;
    private EffectAsset? currentHandleAsset;
    private int currentHandleMapId = -1;
    private bool currentHandleStopRequested;
    private string? currentActiveStateName;
    private NumericVector3? lastPlayOrigin;
    private int lastPlayMapId = -1;
    private bool useRealTime;
    private bool isPickingMapPosition;

    private string? reloadSummaryText;
    private bool reloadHadFailures;
    private string? playErrorText;

    private Vector2 listScrollPosition;
    private Vector2 detailScrollPosition;
    private Vector2 diagnosticsScrollPosition;
    private float lastDetailContentHeight = 280f;

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

    public override Vector2 InitialSize => new(740f, 580f);

    protected override void SetInitialSizeAndPosition()
    {
        Vector2 initialSize = InitialSize;
        float width = Mathf.Min(initialSize.x, Mathf.Max(420f, UI.screenWidth - 32f));
        float height = Mathf.Min(initialSize.y, Mathf.Max(340f, UI.screenHeight - 80f));
        float y = Mathf.Max(20f, (UI.screenHeight - height) * 0.5f);
        windowRect = new Rect(16f, y, width, height);
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

        if (isPickingMapPosition && Find.Targeter is not { IsTargeting: true })
        {
            isPickingMapPosition = false;
        }

        if (Current.ProgramState != ProgramState.Playing)
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
        LumenSceneBootstrap.EnsureInitialized();
        if (LumenSceneBootstrap.Catalog.Count != lastCatalogCount)
        {
            RefreshCatalogEntries(selectFirstIfEmpty: false);
        }

        float curY = inRect.y;
        DrawToolbar(new Rect(inRect.x, curY, inRect.width, ToolbarHeight));
        curY += ToolbarHeight + 6f;

        if (!string.IsNullOrEmpty(reloadSummaryText))
        {
            Color prevColor = GUI.color;
            GUI.color = reloadHadFailures ? new Color(1f, 0.55f, 0.45f) : new Color(0.75f, 0.92f, 0.78f);
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = true;
            float summaryHeight = Mathf.Max(20f, Text.CalcHeight(reloadSummaryText, inRect.width));
            Widgets.Label(new Rect(inRect.x, curY, inRect.width, summaryHeight), reloadSummaryText);
            Text.WordWrap = prevWrap;
            GUI.color = prevColor;
            curY += summaryHeight + 4f;
        }

        string? combinedError = BuildCombinedDiagnosticsText();
        if (!string.IsNullOrEmpty(combinedError))
        {
            DrawDiagnosticsPanel(new Rect(inRect.x, curY, inRect.width, DiagnosticsBoxHeight), combinedError);
            curY += DiagnosticsBoxHeight + 8f;
        }

        float bodyHeight = Mathf.Max(120f, inRect.yMax - curY);
        float gap = 10f;
        float leftWidth = Mathf.Floor((inRect.width - gap) * 0.50f);
        float rightWidth = inRect.width - gap - leftWidth;

        DrawEffectListColumn(new Rect(inRect.x, curY, leftWidth, bodyHeight));
        DrawDetailColumn(new Rect(inRect.x + leftWidth + gap, curY, rightWidth, bodyHeight));
    }

    private void DrawToolbar(Rect rect)
    {
        const float gap = 6f;
        const float minSearchWidth = 140f;
        float available = Mathf.Max(120f, rect.width - gap * 3f);
        float resolveWidth = 104f;
        float reloadWidth = 128f;
        float stopAllWidth = 122f;
        float buttonBudget = resolveWidth + reloadWidth + stopAllWidth;
        if (available < buttonBudget + minSearchWidth)
        {
            float scale = Mathf.Clamp((available - Mathf.Min(minSearchWidth, available * 0.35f)) / buttonBudget, 0.35f, 1f);
            resolveWidth = Mathf.Floor(resolveWidth * scale);
            reloadWidth = Mathf.Floor(reloadWidth * scale);
            stopAllWidth = Mathf.Floor(stopAllWidth * scale);
        }

        float searchWidth = Mathf.Max(40f, available - resolveWidth - reloadWidth - stopAllWidth);
        Rect searchRect = new(rect.x, rect.y + Mathf.Max(0f, (rect.height - QuickSearchWidget.WidgetHeight) * 0.5f),
            searchWidth, QuickSearchWidget.WidgetHeight);
        searchWidget.OnGUI(searchRect, ApplyFilter);

        float x = searchRect.xMax + gap;
        Rect resolveRect = new(x, rect.y, resolveWidth, rect.height);
        string resolveQuery = searchWidget.filter.Text.Trim();
        bool canResolve = !string.IsNullOrEmpty(resolveQuery);
        if (Widgets.ButtonText(resolveRect, "RimLumen_PreviewResolveId".Translate(), active: canResolve) && canResolve)
        {
            ResolveEffectById(resolveQuery);
        }
        TooltipHandler.TipRegion(resolveRect, "RimLumen_PreviewResolveIdTip".Translate());

        x += resolveWidth + gap;
        Rect reloadRect = new(x, rect.y, reloadWidth, rect.height);
        if (Widgets.ButtonText(reloadRect, "RimLumen_PreviewReloadAssets".Translate()))
        {
            ReloadAssets();
        }
        TooltipHandler.TipRegion(reloadRect, "RimLumen_PreviewReloadAssetsTip".Translate());

        x += reloadWidth + gap;
        Rect stopAllRect = new(x, rect.y, stopAllWidth, rect.height);
        PruneDeadHandles();
        if (Widgets.ButtonText(stopAllRect, "RimLumen_PreviewStopAll".Translate(previewHandles.Count), active: previewHandles.Count > 0 || isPickingMapPosition))
        {
            StopAllPreviewInstances();
        }
        TooltipHandler.TipRegion(stopAllRect, "RimLumen_PreviewStopAllTip".Translate());
    }

    private void DrawDiagnosticsPanel(Rect rect, string errorText)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(6f);
        const float buttonWidth = 88f;
        const float gap = 6f;

        Rect textRect = new(inner.x, inner.y, Mathf.Max(80f, inner.width - buttonWidth - gap), inner.height);
        Color prevColor = GUI.color;
        GUI.color = new Color(1f, 0.45f, 0.45f);
        Widgets.LabelScrollable(textRect, errorText, ref diagnosticsScrollPosition, dontConsumeScrollEventsIfNoScrollbar: false);
        GUI.color = prevColor;

        float btnX = textRect.xMax + gap;
        float btnHeight = Mathf.Min(28f, (inner.height - 4f) * 0.5f);
        if (Widgets.ButtonText(new Rect(btnX, inner.y, buttonWidth, btnHeight), "RimLumen_CopyError".Translate()))
        {
            GUIUtility.systemCopyBuffer = errorText;
        }
        if (Widgets.ButtonText(new Rect(btnX, inner.y + btnHeight + 4f, buttonWidth, btnHeight), "RimLumen_PreviewClearError".Translate()))
        {
            reloadDiagnostics.Clear();
            playErrorText = null;
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
            Color prevColor = GUI.color;
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(inner, "RimLumen_PreviewNoMatches".Translate());
            GUI.color = prevColor;
            Text.Anchor = prevAnchor;
            return;
        }

        float viewHeight = Mathf.Max(inner.height, filteredEntries.Count * RowHeight);
        Rect viewRect = new(0f, 0f, inner.width - 16f, viewHeight);
        Widgets.BeginScrollView(inner, ref listScrollPosition, viewRect);

        float curY = 0f;
        for (int i = 0; i < filteredEntries.Count; i++)
        {
            LumenEffectPreviewEntry entry = filteredEntries[i];
            Rect rowRect = new(0f, curY, viewRect.width, RowHeight);
            if (curY + RowHeight >= listScrollPosition.y - RowHeight && curY <= listScrollPosition.y + inner.height + RowHeight)
            {
                if (i % 2 == 1)
                {
                    Widgets.DrawAltRect(rowRect);
                }

                bool selected = string.Equals(entry.EffectId, selectedEffectId, StringComparison.Ordinal);
                if (selected)
                {
                    Widgets.DrawHighlightSelected(rowRect);
                }
                else
                {
                    Widgets.DrawHighlightIfMouseover(rowRect);
                }

                Rect idRect = new(rowRect.x + 6f, rowRect.y + 3f, rowRect.width - 12f, 20f);
                Rect sourceRect = new(rowRect.x + 6f, rowRect.y + 22f, rowRect.width - 12f, 17f);

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

    private void DrawDetailColumn(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);

        if (selectedEntry is null)
        {
            TextAnchor prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Color prevColor = GUI.color;
            GUI.color = ColoredText.SubtleGrayColor;
            string emptyText = string.IsNullOrEmpty(selectedEffectId)
                ? "RimLumen_PreviewSelectPrompt".Translate()
                : "RimLumen_PreviewMissingAfterReload".Translate(selectedEffectId);
            Widgets.Label(inner, emptyText);
            GUI.color = prevColor;
            Text.Anchor = prevAnchor;
            return;
        }

        EffectAsset asset = selectedEntry.Asset;
        Rect viewRect = new(0f, 0f, inner.width - 16f, Mathf.Max(inner.height, lastDetailContentHeight));
        Widgets.BeginScrollView(inner, ref detailScrollPosition, viewRect);

        var listing = new Listing_Standard { maxOneColumn = true };
        listing.Begin(viewRect);

        Text.Font = GameFont.Medium;
        bool prevWrap = Text.WordWrap;
        Text.WordWrap = true;
        listing.Label(asset.EffectId);
        Text.Font = GameFont.Small;

        Color prevColorDetail = GUI.color;
        GUI.color = new Color(0.78f, 0.84f, 0.90f);
        listing.Label("RimLumen_PreviewSourceLine".Translate(FormatEntrySourceDetail(selectedEntry)));
        GUI.color = prevColorDetail;
        listing.Gap(4f);

        string loopLabel = (selectedEntry.IsLooping
            ? "RimLumen_PreviewPlaybackLooping"
            : "RimLumen_PreviewPlaybackOneShot").Translate();
        listing.Label("RimLumen_PreviewAssetStats".Translate(
            asset.Nodes.Length,
            asset.DefaultDuration.ToString("0.##"),
            loopLabel,
            asset.InteractionEvents.Length));

        if (asset.InteractionEvents.Length > 0)
        {
            GUI.color = new Color(0.72f, 0.86f, 0.96f);
            listing.Label("RimLumen_PreviewInteractionHint".Translate());
            GUI.color = prevColorDetail;
        }

        if (lastPlayOrigin is { } origin && Find.CurrentMap is { } originMap && lastPlayMapId == originMap.uniqueID)
        {
            GUI.color = new Color(0.74f, 0.80f, 0.86f);
            listing.Label("RimLumen_PreviewLastOrigin".Translate(origin.X.ToString("0.0"), origin.Z.ToString("0.0")));
            GUI.color = prevColorDetail;
        }

        listing.GapLine(10f);

        Map? currentMap = Find.CurrentMap;
        bool hasMap = currentMap is { Disposed: false };
        if (!hasMap)
        {
            GUI.color = new Color(1f, 0.55f, 0.45f);
            listing.Label("RimLumen_PreviewRequiresMap".Translate());
            GUI.color = prevColorDetail;
            listing.Gap(4f);
        }

        DrawPlaybackControls(listing, asset, currentMap, hasMap);

        if (asset.States.Length > 0)
        {
            listing.GapLine(12f);
            DrawStateMachineControls(listing, asset, currentMap, hasMap);
        }

        Text.WordWrap = prevWrap;
        listing.End();
        lastDetailContentHeight = listing.CurHeight + 16f;
        Widgets.EndScrollView();
    }

    private void DrawPlaybackControls(Listing_Standard listing, EffectAsset asset, Map? currentMap, bool hasMap)
    {
        const float gap = 6f;
        const float buttonHeight = 30f;

        Rect row1 = listing.GetRect(buttonHeight);
        float halfW = (row1.width - gap) * 0.5f;
        Rect cameraBtn = new(row1.x, row1.y, halfW, buttonHeight);
        Rect pickBtn = new(row1.x + halfW + gap, row1.y, halfW, buttonHeight);

        if (Widgets.ButtonText(cameraBtn, "RimLumen_PreviewPlayAtCamera".Translate(), active: hasMap) && hasMap)
        {
            NumericVector3 center = GetCameraCenterGround(currentMap!);
            PlayAssetAt(asset, currentMap!, center.X, center.Z, replaceCurrent: false);
        }

        string pickLabel = isPickingMapPosition
            ? "RimLumen_PreviewPickingMapPos".Translate()
            : "RimLumen_PreviewPickMapPos".Translate();
        if (Widgets.ButtonText(pickBtn, pickLabel, active: hasMap) && hasMap)
        {
            if (isPickingMapPosition)
            {
                CancelMapPick();
            }
            else
            {
                BeginMapPositionPick(asset.EffectId, currentMap!);
            }
        }

        listing.Gap(6f);

        Rect row2 = listing.GetRect(buttonHeight);
        Rect replayBtn = new(row2.x, row2.y, halfW, buttonHeight);
        Rect fadeBtn = new(row2.x + halfW + gap, row2.y, halfW, buttonHeight);

        if (Widgets.ButtonText(replayBtn, "RimLumen_PreviewReplay".Translate(), active: hasMap) && hasMap)
        {
            NumericVector3 origin = ResolveReplayGroundOrigin(currentMap!);
            PlayAssetAt(asset, currentMap!, origin.X, origin.Z, replaceCurrent: true);
        }

        bool canFadeOut = currentHandle is { IsAlive: true } && !IsCurrentHandleDraining();
        if (Widgets.ButtonText(fadeBtn, "RimLumen_PreviewFadeOutCurrent".Translate(), active: canFadeOut) && canFadeOut)
        {
            currentHandleStopRequested = true;
            currentHandle!.Stop(allowFadeOut: true);
        }

        listing.Gap(6f);
        listing.CheckboxLabeled(
            "RimLumen_PreviewRealTime".Translate(),
            ref useRealTime,
            "RimLumen_PreviewRealTimeTip".Translate());

        if (!useRealTime && Find.TickManager is { Paused: true })
        {
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0.45f);
            listing.Label("RimLumen_PreviewPausedHint".Translate());
            GUI.color = prevColor;
        }
    }

    private void DrawStateMachineControls(Listing_Standard listing, EffectAsset asset, Map? currentMap, bool hasMap)
    {
        Text.Font = GameFont.Small;
        listing.Label("RimLumen_PreviewStatesHeader".Translate(asset.States.Length));

        bool hasLiveCurrentForAsset = currentHandle is { IsAlive: true }
            && ReferenceEquals(currentHandleAsset, asset)
            && currentMap is not null
            && currentHandleMapId == currentMap.uniqueID;

        bool isDraining = hasLiveCurrentForAsset && IsCurrentHandleDraining();
        string activeText = hasLiveCurrentForAsset && !string.IsNullOrEmpty(currentActiveStateName)
            ? (isDraining
                ? "RimLumen_PreviewCurrentStateDraining".Translate(currentActiveStateName!)
                : "RimLumen_PreviewCurrentState".Translate(currentActiveStateName!))
            : "RimLumen_PreviewCurrentStateNone".Translate();

        Color prevColor = GUI.color;
        GUI.color = hasLiveCurrentForAsset ? new Color(0.75f, 0.94f, 0.80f) : ColoredText.SubtleGrayColor;
        listing.Label(activeText);
        GUI.color = prevColor;
        listing.Gap(4f);

        string effectiveInitial = LumenEffectPreviewCatalog.ResolveEffectiveInitialState(asset);
        const float btnHeight = 28f;
        const float gap = 6f;

        for (int i = 0; i < asset.States.Length; i += 2)
        {
            Rect row = listing.GetRect(btnHeight);
            if (i + 1 < asset.States.Length)
            {
                float halfW = (row.width - gap) * 0.5f;
                DrawStateButton(new Rect(row.x, row.y, halfW, btnHeight), asset, asset.States[i], effectiveInitial, currentMap, hasMap, hasLiveCurrentForAsset);
                DrawStateButton(new Rect(row.x + halfW + gap, row.y, halfW, btnHeight), asset, asset.States[i + 1], effectiveInitial, currentMap, hasMap, hasLiveCurrentForAsset);
            }
            else
            {
                DrawStateButton(row, asset, asset.States[i], effectiveInitial, currentMap, hasMap, hasLiveCurrentForAsset);
            }

            listing.Gap(4f);
        }
    }

    private void DrawStateButton(
        Rect rect,
        EffectAsset asset,
        EffectStateSpec stateSpec,
        string effectiveInitial,
        Map? currentMap,
        bool hasMap,
        bool hasLiveCurrentForAsset)
    {
        bool isActive = hasLiveCurrentForAsset
            && (currentHandle is not EffectInstanceHandle nativeHandle || nativeHandle.IsEmitting)
            && string.Equals(currentActiveStateName, stateSpec.Name, StringComparison.Ordinal);

        string label = BuildStateButtonLabel(asset, stateSpec, effectiveInitial);
        if (isActive)
        {
            Widgets.DrawStrongHighlight(rect, new Color(0.35f, 0.72f, 0.45f, 0.35f));
        }

        if (Widgets.ButtonText(rect, label, active: hasMap) && hasMap)
        {
            ActivateState(asset, currentMap!, stateSpec.Name);
        }
    }

    private static string BuildStateButtonLabel(EffectAsset asset, EffectStateSpec stateSpec, string effectiveInitial)
    {
        var tags = new List<string>(3);
        if (string.Equals(stateSpec.Name, effectiveInitial, StringComparison.Ordinal))
        {
            tags.Add("RimLumen_PreviewStateTagInitial".Translate());
        }
        if (stateSpec.Loop)
        {
            tags.Add("RimLumen_PreviewStateTagLoop".Translate());
        }
        if (string.Equals(stateSpec.Name, asset.StopState, StringComparison.Ordinal))
        {
            tags.Add("RimLumen_PreviewStateTagStop".Translate());
        }

        return tags.Count == 0
            ? stateSpec.Name
            : $"{stateSpec.Name} [{string.Join(", ", tags)}]";
    }

    private void ActivateState(EffectAsset asset, Map currentMap, string stateName)
    {
        if (currentHandle is { IsAlive: true }
            && !IsCurrentHandleDraining()
            && ReferenceEquals(currentHandleAsset, asset)
            && currentHandleMapId == currentMap.uniqueID)
        {
            try
            {
                currentHandle.GoTo(stateName);
                currentActiveStateName = stateName;
                playErrorText = null;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimLumen] Effect preview failed to switch state '{stateName}' on '{asset.EffectId}': {ex}");
                playErrorText = ex.Message;
            }
            return;
        }

        NumericVector3 origin = ResolveReplayGroundOrigin(currentMap);
        PlayAssetAt(asset, currentMap, origin.X, origin.Z, initialState: stateName, replaceCurrent: true);
    }

    private void BeginMapPositionPick(string effectId, Map expectedMap)
    {
        if (Find.Targeter is null)
        {
            return;
        }

        isPickingMapPosition = true;
        Find.Targeter.BeginTargeting(
            TargetingParameters.ForCell(),
            action: target =>
            {
                isPickingMapPosition = false;
                Map? mapNow = Find.CurrentMap;
                if (mapNow is not { Disposed: false } || mapNow != expectedMap || !target.Cell.InBounds(mapNow))
                {
                    return;
                }

                EffectAsset? latest = ResolveAssetForPick(effectId);
                if (latest is null)
                {
                    playErrorText = "RimLumen_PreviewMissingAfterReload".Translate(effectId);
                    return;
                }

                PlayAssetAt(latest, mapNow, target.Cell.x + 0.5f, target.Cell.z + 0.5f, replaceCurrent: false);
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

    private EffectAsset? ResolveAssetForPick(string effectId)
    {
        RefreshCatalogEntries(selectFirstIfEmpty: false);
        for (int i = 0; i < allEntries.Count; i++)
        {
            if (string.Equals(allEntries[i].EffectId, effectId, StringComparison.Ordinal))
            {
                return allEntries[i].Asset;
            }
        }

        if (RimLumenApi.TryGetEffect(effectId, out EffectAsset? resolved) && resolved is not null)
        {
            return resolved;
        }

        return null;
    }

    private void CancelMapPick()
    {
        if (isPickingMapPosition && Find.Targeter is { IsTargeting: true })
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
        playErrorText = null;

        LumenPlayRequest request = LumenEffectPreviewCatalog.BuildPlayRequest(
            asset,
            map.uniqueID,
            groundX,
            groundZ,
            initialState: initialState,
            realTime: useRealTime);

        Log.ResetMessageCount();
        Dictionary<LogMessage, int> logBefore = SnapshotLogMessages();
        ILumenEffectHandle handle;
        try
        {
            handle = RimLumenApi.Play(request);
        }
        catch (Exception ex)
        {
            Log.Error($"[RimLumen] Effect preview failed to play '{asset.EffectId}': {ex}");
            List<string> logged = CollectNewDiagnostics(logBefore);
            playErrorText = logged.Count > 0 ? string.Join(Environment.NewLine, logged) : ex.Message;
            return;
        }

        List<string> newDiagnostics = CollectNewDiagnostics(logBefore);
        if (!handle.IsAlive)
        {
            playErrorText = newDiagnostics.Count > 0
                ? string.Join(Environment.NewLine, newDiagnostics)
                : "RimLumen_PreviewPlayReturnedInactive".Translate(asset.EffectId);
            return;
        }

        if (newDiagnostics.Count > 0)
        {
            playErrorText = string.Join(Environment.NewLine, newDiagnostics);
        }

        DetachCurrentHandleEvents();
        previewHandles.Add(handle);
        currentHandle = handle;
        currentHandleAsset = asset;
        currentHandleMapId = map.uniqueID;
        currentHandleStopRequested = false;
        currentActiveStateName = string.IsNullOrEmpty(initialState)
            ? LumenEffectPreviewCatalog.ResolveEffectiveInitialState(asset)
            : initialState;
        handle.StateChanged += OnCurrentHandleStateChanged;
    }

    private void OnCurrentHandleStateChanged(string stateName)
    {
        currentActiveStateName = stateName;
    }

    private void DetachCurrentHandleEvents()
    {
        if (currentHandle is not null)
        {
            currentHandle.StateChanged -= OnCurrentHandleStateChanged;
        }
    }

    private void StopAndReleaseCurrentHandle()
    {
        if (currentHandle is null)
        {
            return;
        }

        ILumenEffectHandle old = currentHandle;
        DetachCurrentHandleEvents();
        currentHandle = null;
        currentHandleAsset = null;
        currentHandleMapId = -1;
        currentHandleStopRequested = false;
        currentActiveStateName = null;

        previewHandles.Remove(old);
        old.Stop(allowFadeOut: false);
        old.Dispose();
    }

    private void StopAllPreviewInstances()
    {
        CancelMapPick();
        DetachCurrentHandleEvents();
        currentHandle = null;
        currentHandleAsset = null;
        currentHandleMapId = -1;
        currentHandleStopRequested = false;
        currentActiveStateName = null;

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
            if (!previewHandles[i].IsAlive)
            {
                previewHandles.RemoveAt(i);
            }
        }

        if (currentHandle is not null && !currentHandle.IsAlive)
        {
            DetachCurrentHandleEvents();
            currentHandle = null;
            currentHandleAsset = null;
            currentHandleMapId = -1;
            currentHandleStopRequested = false;
            currentActiveStateName = null;
        }
    }

    private void ResolveEffectById(string query)
    {
        LumenSceneBootstrap.EnsureInitialized();
        Log.ResetMessageCount();
        Dictionary<LogMessage, int> logBefore = SnapshotLogMessages();

        bool resolvedOk;
        EffectAsset? resolved;
        try
        {
            resolvedOk = RimLumenApi.TryGetEffect(query, out resolved);
        }
        catch (Exception ex)
        {
            Log.Error($"[RimLumen] Effect preview failed to resolve '{query}': {ex}");
            List<string> logged = CollectNewDiagnostics(logBefore);
            playErrorText = logged.Count > 0 ? string.Join(Environment.NewLine, logged) : ex.Message;
            return;
        }

        List<string> newDiagnostics = CollectNewDiagnostics(logBefore);
        if (!resolvedOk || resolved is null)
        {
            playErrorText = newDiagnostics.Count > 0
                ? string.Join(Environment.NewLine, newDiagnostics)
                : "RimLumen_PreviewResolveNotFound".Translate(query);
            return;
        }

        playErrorText = newDiagnostics.Count > 0 ? string.Join(Environment.NewLine, newDiagnostics) : null;
        if (!LumenSceneBootstrap.Catalog.TryResolveEffect(resolved.EffectId, out _))
        {
            resolvedExternalAssets[resolved.EffectId] = resolved;
        }

        if (!string.Equals(resolved.EffectId, query, StringComparison.OrdinalIgnoreCase))
        {
            externalQueryAliases[resolved.EffectId] = query;
        }

        selectedEffectId = resolved.EffectId;
        RefreshCatalogEntries(selectFirstIfEmpty: false);
    }

    private void ReloadAssets()
    {
        LumenSceneBootstrap.EnsureInitialized();
        Log.ResetMessageCount();
        Dictionary<LogMessage, int> logBefore = SnapshotLogMessages();

        LumenAssetReloadResult result;
        try
        {
            result = LumenModAssetScanner.ReloadRunningModAssets(LumenSceneBootstrap.Catalog);
        }
        catch (Exception ex)
        {
            Log.Error($"[RimLumen] Reload effect assets failed: {ex}");
            reloadDiagnostics.Clear();
            reloadDiagnostics.AddRange(CollectNewDiagnostics(logBefore));
            if (reloadDiagnostics.Count == 0)
            {
                reloadDiagnostics.Add(ex.Message);
            }
            reloadHadFailures = true;
            reloadSummaryText = ex.Message;
            return;
        }

        List<string> newDiagnostics = CollectNewDiagnostics(logBefore);
        reloadDiagnostics.Clear();
        reloadDiagnostics.AddRange(newDiagnostics);
        if (result.Failed > 0 && reloadDiagnostics.Count == 0)
        {
            reloadDiagnostics.Add("RimLumen_PreviewReloadFailedSeeLog".Translate(result.Failed));
        }

        resolvedExternalAssets.Clear();
        externalQueryAliases.Clear();
        playErrorText = null;
        reloadHadFailures = result.Failed > 0;
        reloadSummaryText = "RimLumen_PreviewReloadSummary".Translate(result.Loaded, result.Failed);

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
        allEntries = LumenEffectPreviewCatalog.BuildEntries(catalog, resolvedExternalAssets.Values);
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
        filteredEntries = LumenEffectPreviewCatalog.FilterEntries(
            allEntries,
            searchWidget.filter.Text,
            entry => externalQueryAliases.TryGetValue(entry.EffectId, out string? alias) ? alias : string.Empty);
        searchWidget.noResultsMatched = filteredEntries.Count == 0 && searchWidget.filter.Active;
    }

    private bool IsCurrentHandleDraining() =>
        currentHandleStopRequested || (currentHandle is EffectInstanceHandle nativeHandle && !nativeHandle.IsEmitting);

    private void SelectEntry(LumenEffectPreviewEntry entry)
    {
        if (!string.Equals(selectedEffectId, entry.EffectId, StringComparison.Ordinal))
        {
            CancelMapPick();
        }

        selectedEntry = entry;
        selectedEffectId = entry.EffectId;
        playErrorText = null;
    }

    private NumericVector3 ResolveReplayGroundOrigin(Map map)
    {
        if (lastPlayOrigin is { } origin && lastPlayMapId == map.uniqueID)
        {
            return ClampToMapGround(map, origin.X, origin.Z);
        }

        return GetCameraCenterGround(map);
    }

    private static NumericVector3 GetCameraCenterGround(Map map)
    {
        Vector3 world = UI.UIToMapPosition(UI.screenWidth * 0.5f, UI.screenHeight * 0.5f);
        if (!float.IsFinite(world.x) || !float.IsFinite(world.z))
        {
            IntVec3 camCell = Find.CameraDriver is not null ? Find.CameraDriver.MapPosition : map.Center;
            world = camCell.ToVector3Shifted();
        }

        return ClampToMapGround(map, world.x, world.z);
    }

    private static NumericVector3 ClampToMapGround(Map map, float x, float z)
    {
        float maxX = Mathf.Max(0.5f, map.Size.x - 0.5f);
        float maxZ = Mathf.Max(0.5f, map.Size.z - 0.5f);
        return new NumericVector3(Mathf.Clamp(x, 0.5f, maxX), 0f, Mathf.Clamp(z, 0.5f, maxZ));
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

    private static List<string> CollectNewDiagnostics(Dictionary<LogMessage, int> before)
    {
        var diagnostics = new List<string>();
        foreach (LogMessage msg in Log.Messages)
        {
            if (msg.type != LogMessageType.Error && msg.type != LogMessageType.Warning)
            {
                continue;
            }

            if (!before.TryGetValue(msg, out int prevRepeats) || msg.repeats > prevRepeats)
            {
                string prefix = msg.type == LogMessageType.Error ? "[Error] " : "[Warning] ";
                diagnostics.Add(prefix + msg.text);
            }
        }
        return diagnostics;
    }

    private string? BuildCombinedDiagnosticsText()
    {
        if (reloadDiagnostics.Count == 0 && string.IsNullOrEmpty(playErrorText))
        {
            return null;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < reloadDiagnostics.Count; i++)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(reloadDiagnostics[i]);
        }

        if (!string.IsNullOrEmpty(playErrorText))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(playErrorText);
        }

        return sb.ToString();
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
            LumenEffectPreviewSourceKind.ExternalProvider => "RimLumen_PreviewSourceExternal".Translate(
                string.IsNullOrEmpty(entry.PackageId) ? "-" : entry.PackageId),
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
        LumenEffectPreviewSourceKind.ExternalProvider => "RimLumen_PreviewSourceExternal".Translate(
            string.IsNullOrEmpty(entry.PackageId) ? "-" : entry.PackageId),
        _ => entry.SourceDetail,
    };
}
