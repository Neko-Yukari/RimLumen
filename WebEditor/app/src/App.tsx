import { useCallback, useEffect, useRef, useState } from 'react';
import { Panel, PanelGroup, PanelResizeHandle } from 'react-resizable-panels';
import type { Pane } from 'tweakpane';
import {
  AlertTriangle,
  CheckCircle2,
  Download,
  FileUp,
  FolderOpen,
  Pause,
  Play,
  RotateCcw,
  Square,
  Video,
  Zap,
} from 'lucide-react';
import { WebSimClient } from './wasm/webSimClient';
import { DOC_PRESETS } from './wasm/docSamples';
import type {
  AssetFormat,
  DemoSummary,
  FrameEventInfo,
  StateSummary,
  TimelineEventSummary,
  ValidationResult,
} from './wasm/types';
import { RimLumenViewport, type CameraMode, type LoadedTextureEntry } from './render/RimLumenViewport';
import { CodeEditorPane } from './components/CodeEditorPane';
import { ViewportTweakpane, type TweakParams } from './components/ViewportTweakpane';

function downloadTextFile(filename: string, content: string, mimeType: string) {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function sanitizeStem(name: string): string {
  const cleaned = name.replace(/[^\w.-]+/g, '_');
  return cleaned.length > 0 ? cleaned : 'effect';
}

export function App() {
  const [statusMessage, setStatusMessage] = useState('正在初始化 .NET WebAssembly (AOT+SIMD)...');
  const [ready, setReady] = useState(false);
  const [initError, setInitError] = useState<string | null>(null);

  const [demos, setDemos] = useState<DemoSummary[]>([]);
  const [selectedPreset, setSelectedPreset] = useState('RimLumen.Demo.Firework.Gold');
  const [sourceText, setSourceText] = useState('');
  const [format, setFormat] = useState<AssetFormat>('json');
  const [docRevision, setDocRevision] = useState(0);

  const [validation, setValidation] = useState<ValidationResult>({ ok: true });
  const [paused, setPaused] = useState(false);
  const [cameraMode, setCameraMode] = useState<CameraMode>('3d');
  const [alive, setAlive] = useState(true);
  const [liveParticles, setLiveParticles] = useState(0);
  const [simMs, setSimMs] = useState(0);
  const [currentState, setCurrentState] = useState<string | null>(null);
  const [eventLog, setEventLog] = useState<FrameEventInfo[]>([]);
  const [loadedTextures, setLoadedTextures] = useState<LoadedTextureEntry[]>([]);
  const [dragOverCanvas, setDragOverCanvas] = useState(false);

  const [lossyWarnings, setLossyWarnings] = useState<{
    targetFormat: AssetFormat;
    content: string;
    warnings: string[];
  } | null>(null);

  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const toolbarRef = useRef<HTMLDivElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const texInputRef = useRef<HTMLInputElement | null>(null);
  const lossyDialogRef = useRef<HTMLDialogElement | null>(null);

  const simRef = useRef<WebSimClient | null>(null);
  const viewportRef = useRef<RimLumenViewport | null>(null);
  const paneApiRef = useRef<Pane | null>(null);

  const pausedRef = useRef(false);
  pausedRef.current = paused;
  const activeEffectIdRef = useRef('RimLumen.Demo.Firework.Gold');
  const lastAppliedTextRef = useRef('');
  const pendingTexKeyRef = useRef<string | null>(null);

  const tweakParamsRef = useRef<TweakParams>({
    speed: 1.0,
    density: 1.0,
    exposure: 1.0,
    intensity: 1.0,
    rate: 1.0,
    showGrid: true,
    showHelpers: true,
    orbitMotion: false,
    orbitSpeed: 2.4,
    targetWorld: { x: 3.0, y: 1.0, z: 0.0 },
    liveParticles: 0,
    simMs: 0,
  });

  const applyOrbitForEffect = useCallback((effectId: string | undefined) => {
    const isPylon = effectId === 'RimLumen.Demo.DistortionPylon';
    tweakParamsRef.current.orbitMotion = isPylon;
    simRef.current?.setOrbitMotion(isPylon, 1.5, tweakParamsRef.current.orbitSpeed);
    paneApiRef.current?.refresh();
  }, []);

  const loadPresetIntoEditor = useCallback(
    (presetId: string, preferredFormat?: AssetFormat) => {
      const sim = simRef.current;
      if (!sim) return;

      const docPreset = DOC_PRESETS.find((d) => d.id === presetId);
      let nextText = '';
      let nextFormat: AssetFormat = preferredFormat ?? 'json';
      let nextEffectId = presetId;

      if (docPreset) {
        nextFormat = docPreset.format;
        nextText = docPreset.source;
        const parsed = sim.validateSource(nextText, nextFormat);
        nextEffectId = parsed.effectId ?? presetId;
      } else {
        const exp = sim.getDemoSource(presetId, nextFormat, true);
        if (!exp.ok || !exp.content) {
          setStatusMessage(exp.error ?? '读取示例源码失败');
          return;
        }
        nextText = exp.content;
        nextEffectId = presetId;
      }

      setSelectedPreset(presetId);
      setFormat(nextFormat);
      setSourceText(nextText);
      setDocRevision((r) => r + 1);

      const loadRes = sim.loadAndPlay(nextText, nextFormat);
      setValidation(loadRes);
      if (loadRes.ok) {
        lastAppliedTextRef.current = `${nextFormat}:${nextText}`;
        activeEffectIdRef.current = loadRes.effectId ?? nextEffectId;
        applyOrbitForEffect(activeEffectIdRef.current);
        setPaused(false);
        setEventLog([]);
        setStatusMessage(`已加载 ${activeEffectIdRef.current} (${nextFormat.toUpperCase()})`);
      }
    },
    [applyOrbitForEffect],
  );

  // Initialize WASM runtime, Three.js viewport, and rAF loop once.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    let cancelled = false;
    let rafId = 0;
    let lastTs = performance.now();
    let telemetryTick = 0;

    const viewport = new RimLumenViewport(canvas);
    viewportRef.current = viewport;

    WebSimClient.create()
      .then((client) => {
        if (cancelled) return;
        simRef.current = client;
        const demoList = client.listDemos();
        setDemos(demoList);
        setReady(true);

        const exp = client.getDemoSource('RimLumen.Demo.Firework.Gold', 'json', true);
        if (exp.ok && exp.content) {
          setFormat('json');
          setSourceText(exp.content);
          setDocRevision((r) => r + 1);
          const loadRes = client.loadAndPlay(exp.content, 'json');
          setValidation(loadRes);
          if (loadRes.ok) {
            lastAppliedTextRef.current = `json:${exp.content}`;
            activeEffectIdRef.current = loadRes.effectId ?? 'RimLumen.Demo.Firework.Gold';
            setStatusMessage(`就绪 · 已加载 ${activeEffectIdRef.current}`);
          }
        }

        const loop = (now: number) => {
          if (cancelled) return;
          const rawDt = Math.min(0.05, Math.max(0, (now - lastTs) / 1000));
          lastTs = now;
          const tp = tweakParamsRef.current;
          const dt = pausedRef.current ? 0 : rawDt * tp.speed;

          const { width, height, view, proj } = viewport.getCameraMatrices();
          const frame = client.stepFrame(dt, width, height, view, proj);
          const simVerts = frame.simVertexCount > 0 ? client.getSimVerticesBytes() : new Uint8Array(0);
          const simIdx = frame.simIndexCount > 0 ? client.getSimIndicesBytes() : new Uint8Array(0);
          const ribVerts = frame.ribVertexCount > 0 ? client.getRibbonVerticesBytes() : new Uint8Array(0);
          const ribIdx = frame.ribIndexCount > 0 ? client.getRibbonIndicesBytes() : new Uint8Array(0);

          viewport.setTargetMarker(true, tp.targetWorld.x, tp.targetWorld.y, tp.targetWorld.z);
          viewport.renderFrame(frame, simVerts, simIdx, ribVerts, ribIdx);

          tp.liveParticles = frame.liveParticles;
          tp.simMs = frame.simMs;
          telemetryTick++;
          if (telemetryTick % 6 === 0) {
            setAlive(frame.alive);
            setLiveParticles(frame.liveParticles);
            setSimMs(frame.simMs);
            setCurrentState(frame.currentState);
          }
          if (frame.events.length > 0) {
            setEventLog((prev) => [...frame.events, ...prev].slice(0, 12));
          }

          rafId = requestAnimationFrame(loop);
        };
        rafId = requestAnimationFrame(loop);
      })
      .catch((err) => {
        if (cancelled) return;
        const msg = err instanceof Error ? err.message : String(err);
        setInitError(`WASM 初始化失败: ${msg}`);
        setStatusMessage('WASM 启动失败');
      });

    return () => {
      cancelled = true;
      cancelAnimationFrame(rafId);
      viewport.dispose();
      viewportRef.current = null;
      simRef.current = null;
    };
  }, []);

  // Toolbar roving-tabindex fallback when native focusgroup is unsupported (modern-web-guidance toolbar guide).
  useEffect(() => {
    const bar = toolbarRef.current;
    if (!bar || 'focusGroup' in HTMLElement.prototype) return;

    const getItems = () =>
      Array.from(bar.querySelectorAll<HTMLElement>('button, select')).filter(
        (el) => el.offsetParent !== null,
      );

    const items = getItems();
    items.forEach((el, idx) => {
      el.tabIndex = idx === 0 ? 0 : -1;
    });

    const onFocusIn = (e: FocusEvent) => {
      const target = e.target as HTMLElement | null;
      if (!target) return;
      const currentItems = getItems();
      if (currentItems.includes(target)) {
        currentItems.forEach((el) => {
          el.tabIndex = el === target ? 0 : -1;
        });
      }
    };

    const onKeyDown = (e: KeyboardEvent) => {
      const currentItems = getItems();
      const idx = currentItems.indexOf(document.activeElement as HTMLElement);
      if (idx < 0) return;
      if (e.key === 'ArrowRight') {
        e.preventDefault();
        currentItems[(idx + 1) % currentItems.length].focus();
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault();
        currentItems[(idx - 1 + currentItems.length) % currentItems.length].focus();
      } else if (e.key === 'Home') {
        e.preventDefault();
        currentItems[0]?.focus();
      } else if (e.key === 'End') {
        e.preventDefault();
        currentItems[currentItems.length - 1]?.focus();
      }
    };

    bar.addEventListener('focusin', onFocusIn);
    bar.addEventListener('keydown', onKeyDown);
    return () => {
      bar.removeEventListener('focusin', onFocusIn);
      bar.removeEventListener('keydown', onKeyDown);
    };
  }, [ready]);

  useEffect(() => {
    const dlg = lossyDialogRef.current;
    if (!dlg) return;
    if (lossyWarnings && !dlg.open) {
      dlg.showModal();
    } else if (!lossyWarnings && dlg.open) {
      dlg.close();
    }
  }, [lossyWarnings]);

  const handleTweakChange = useCallback((key: keyof TweakParams, value: unknown) => {
    const sim = simRef.current;
    const vp = viewportRef.current;
    const tp = tweakParamsRef.current;
    if (key === 'density' && typeof value === 'number') {
      sim?.setParticleDensity(value);
    } else if (key === 'exposure' && typeof value === 'number') {
      vp?.setExposure(value);
    } else if (key === 'intensity' && typeof value === 'number') {
      sim?.setParam('intensity', value);
    } else if (key === 'rate' && typeof value === 'number') {
      sim?.setParam('rate', value);
    } else if (key === 'showGrid' && typeof value === 'boolean') {
      vp?.setShowGrid(value);
    } else if (key === 'showHelpers' && typeof value === 'boolean') {
      vp?.setShowHelpers(value);
    } else if (key === 'orbitMotion' && typeof value === 'boolean') {
      sim?.setOrbitMotion(value, 1.5, tp.orbitSpeed);
    } else if (key === 'orbitSpeed' && typeof value === 'number') {
      sim?.setOrbitMotion(tp.orbitMotion, 1.5, value);
    } else if (key === 'targetWorld' && value && typeof value === 'object') {
      const tw = value as { x: number; y: number; z: number };
      sim?.setTargetWorld(tw.x, tw.y, tw.z);
    }
  }, []);

  const handleValidate = useCallback((doc: string, fmt: AssetFormat): ValidationResult => {
    const sim = simRef.current;
    if (!sim) return { ok: true };
    return sim.validateSource(doc, fmt);
  }, []);

  const handleValidChange = useCallback(
    (doc: string, fmt: AssetFormat, _result: ValidationResult) => {
      const key = `${fmt}:${doc}`;
      if (key === lastAppliedTextRef.current) {
        setValidation(_result);
        return;
      }
      const sim = simRef.current;
      if (!sim) return;
      const res = sim.loadAndPlay(doc, fmt);
      setValidation(res);
      if (res.ok) {
        lastAppliedTextRef.current = key;
        activeEffectIdRef.current = res.effectId ?? 'draft';
        setPaused(false);
        setStatusMessage(`已热加载 ${activeEffectIdRef.current}`);
      }
    },
    [],
  );

  const handleValidationError = useCallback((result: ValidationResult) => {
    setValidation(result);
  }, []);

  const handleUserEdit = useCallback((nextText: string) => {
    setSourceText(nextText);
    setSelectedPreset((prev) => (prev === '__custom__' ? prev : '__custom__'));
  }, []);

  const handleOpenAssetFile = async (file: File) => {
    const sim = simRef.current;
    if (!sim) return;
    try {
      const text = await file.text();
      const isXml = file.name.toLowerCase().endsWith('.xml') || text.trimStart().startsWith('<');
      const nextFmt: AssetFormat = isXml ? 'xml' : 'json';
      setSelectedPreset('__custom__');
      setEventLog([]);
      setFormat(nextFmt);
      setSourceText(text);
      setDocRevision((r) => r + 1);
      const res = sim.loadAndPlay(text, nextFmt);
      setValidation(res);
      if (res.ok) {
        lastAppliedTextRef.current = `${nextFmt}:${text}`;
        activeEffectIdRef.current = res.effectId ?? file.name;
        applyOrbitForEffect(activeEffectIdRef.current);
        setPaused(false);
        setStatusMessage(`已打开 ${file.name}`);
      } else {
        setStatusMessage(`打开 ${file.name} 存在校验错误`);
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setStatusMessage(`读取文件失败：${msg}`);
    }
  };

  const handleFormatSwitch = (nextFmt: AssetFormat) => {
    if (nextFmt === format) return;
    const sim = simRef.current;
    if (!sim) return;

    if (validation.ok) {
      // Convert current validated draft into the requested format cleanly for inspection/editing.
      const converted = sim.exportSource(sourceText, format, nextFmt, true);
      if (converted.ok && converted.content) {
        setFormat(nextFmt);
        setSourceText(converted.content);
        setDocRevision((r) => r + 1);
        const res = sim.loadAndPlay(converted.content, nextFmt);
        setValidation(res);
        if (res.ok) lastAppliedTextRef.current = `${nextFmt}:${converted.content}`;
        setStatusMessage(
          converted.warnings && converted.warnings.length > 0
            ? `已切换至 ${nextFmt.toUpperCase()}（含 ${converted.warnings.length} 项格式折算）`
            : `已切换至 ${nextFmt.toUpperCase()}`,
        );
        return;
      }
    }
    setFormat(nextFmt);
  };

  const handleExport = (targetFormat: AssetFormat) => {
    const sim = simRef.current;
    if (!sim) return;
    const res = sim.exportSource(sourceText, format, targetFormat, true);
    if (!res.ok || !res.content) {
      setValidation({ ok: false, error: res.error, line: res.line, column: res.column });
      setStatusMessage('导出失败：请先修复当前代码错误');
      return;
    }

    const stem = sanitizeStem(activeEffectIdRef.current);
    const warnings = res.warnings ?? [];
    if (targetFormat === 'xml' && warnings.length > 0) {
      setLossyWarnings({ targetFormat, content: res.content, warnings });
      return;
    }
    const ext = targetFormat === 'xml' ? '.xml' : '.lumen.json';
    const mime = targetFormat === 'xml' ? 'application/xml' : 'application/json';
    downloadTextFile(`${stem}${ext}`, res.content, mime);
    setStatusMessage(`已导出 ${stem}${ext}`);
  };

  const handleTextureUpload = async (file: File, requestedKey?: string | null) => {
    const vp = viewportRef.current;
    if (!vp) return;
    try {
      const missingKey = (validation.textureKeys ?? []).find((k) => !loadedTextures.some((t) => t.key === k));
      const key = requestedKey ?? missingKey ?? file.name.replace(/\.[^.]+$/, '');
      const entry = await vp.loadTextureFile(key, file, true);
      setLoadedTextures(vp.getLoadedTextures());
      setStatusMessage(`已绑定贴图 ${entry.key} (${entry.width}×${entry.height})`);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setStatusMessage(`贴图加载失败：${msg}`);
    }
  };

  const handleToggleCameraMode = () => {
    const vp = viewportRef.current;
    if (!vp) return;
    const next: CameraMode = cameraMode === '3d' ? '2d' : '3d';
    vp.setCameraMode(next);
    setCameraMode(next);
  };

  const states: StateSummary[] = validation.states ?? [];
  const timelineEvents: TimelineEventSummary[] = validation.timelineEvents ?? [];
  const declaredTextureKeys = validation.textureKeys ?? [];
  const extraTextures = loadedTextures.filter((t) => !declaredTextureKeys.includes(t.key));
  const showPylonTrigger = activeEffectIdRef.current === 'RimLumen.Demo.DistortionPylon';

  return (
    <div className="app-shell">
      <header className="top-bar">
        <div className="brand-group">
          <h1 className="brand-title">RimLumen 特效工作台</h1>
          <span className="status-pill" role="status">
            {statusMessage}
          </span>
        </div>

        <div
          ref={toolbarRef}
          className="main-toolbar"
          role="toolbar"
          aria-label="特效控制与文件操作"
          // @ts-expect-error focusgroup is an emerging baseline attribute handled with progressive fallback
          focusgroup="toolbar wrap"
        >
          <div className="toolbar-group" role="group" aria-label="示例与文件">
            <select
              className="preset-select"
              aria-label="选择内置或文档示例"
              disabled={!ready}
              value={selectedPreset}
              onChange={(e) => loadPresetIntoEditor(e.target.value)}
            >
              <optgroup label="内置 Demo（6）">
                {demos.map((d) => (
                  <option key={d.effectId} value={d.effectId}>
                    {d.effectId}
                  </option>
                ))}
              </optgroup>
              <optgroup label="docs/api.md 文档示例">
                {DOC_PRESETS.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.label}
                  </option>
                ))}
              </optgroup>
              <option value="__custom__" disabled hidden>
                自定义草稿 / 已打开文件
              </option>
            </select>

            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              onClick={() => fileInputRef.current?.click()}
            >
              <FolderOpen size={15} aria-hidden="true" />
              <span>打开</span>
            </button>
            <input
              ref={fileInputRef}
              type="file"
              accept=".json,.xml,.lumen.json"
              className="visually-hidden"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) void handleOpenAssetFile(f);
                e.currentTarget.value = '';
              }}
            />
          </div>

          <span className="toolbar-sep" role="separator" aria-orientation="vertical" />

          <div className="toolbar-group" role="group" aria-label="导出格式">
            <button
              type="button"
              className="btn-primary"
              disabled={!ready}
              onClick={() => handleExport(format)}
            >
              <Download size={15} aria-hidden="true" />
              <span>{format === 'xml' ? '导出 .xml' : '导出 .lumen.json'}</span>
            </button>
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              onClick={() => handleExport(format === 'xml' ? 'json' : 'xml')}
            >
              <span>{format === 'xml' ? '另存为 JSON' : '另存为 XML'}</span>
            </button>
          </div>

          <span className="toolbar-sep" role="separator" aria-orientation="vertical" />

          <div className="toolbar-group" role="group" aria-label="播放与相机控制">
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              aria-pressed={!paused}
              onClick={() => setPaused((p) => !p)}
            >
              {paused ? <Play size={15} aria-hidden="true" /> : <Pause size={15} aria-hidden="true" />}
              <span>{paused ? '继续' : '暂停'}</span>
            </button>
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              onClick={() => {
                simRef.current?.restart();
                setPaused(false);
                setEventLog([]);
              }}
            >
              <RotateCcw size={15} aria-hidden="true" />
              <span>重播</span>
            </button>
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              onClick={() => simRef.current?.stop(true)}
            >
              <Square size={15} aria-hidden="true" />
              <span>淡出停止</span>
            </button>
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              aria-pressed={cameraMode === '2d'}
              onClick={handleToggleCameraMode}
            >
              <Video size={15} aria-hidden="true" />
              <span>{cameraMode === '2d' ? '2D 60°' : '3D 视角'}</span>
            </button>
            <button
              type="button"
              className="btn-secondary"
              disabled={!ready}
              onClick={() => viewportRef.current?.resetCameras()}
            >
              <span>重置视角</span>
            </button>
          </div>
        </div>
      </header>

      {initError && (
        <div className="error-banner" role="alert">
          <AlertTriangle size={16} aria-hidden="true" />
          <span>{initError}</span>
        </div>
      )}

      <main className="workspace-main">
        <PanelGroup direction="horizontal">
          <Panel defaultSize={56} minSize={34} className="pane-column">
            <div
              className={`viewport-stage ${dragOverCanvas ? 'drag-active' : ''}`}
              onDragOver={(e) => {
                e.preventDefault();
                setDragOverCanvas(true);
              }}
              onDragLeave={() => setDragOverCanvas(false)}
              onDrop={(e) => {
                e.preventDefault();
                setDragOverCanvas(false);
                pendingTexKeyRef.current = null;
                const f = e.dataTransfer.files?.[0];
                if (!f) return;
                const lower = f.name.toLowerCase();
                if (lower.endsWith('.json') || lower.endsWith('.xml')) {
                  void handleOpenAssetFile(f);
                } else {
                  void handleTextureUpload(f, null);
                }
              }}
            >
              <canvas ref={canvasRef} className="viewport-canvas" />
              <ViewportTweakpane
                paramsRef={tweakParamsRef}
                onParamChange={handleTweakChange}
                paneApiRef={paneApiRef}
              />
              <div className="viewport-hud" aria-label="模拟实时计数">
                <span>{alive ? '运行中' : '已结束'}</span>
                <span>粒子 {liveParticles}</span>
                <span>WASM {simMs.toFixed(2)} ms</span>
                {currentState && <span>状态 {currentState}</span>}
              </div>
            </div>

            <section className="bottom-inspector" aria-label="状态机与贴图管理">
              <div className="inspector-row">
                <span className="inspector-label">状态 / 触发</span>
                <div className="chip-list">
                  {states.map((s) => (
                    <button
                      key={s.name}
                      type="button"
                      className="chip-btn"
                      aria-pressed={currentState === s.name}
                      onClick={() => {
                        const res = simRef.current?.goToState(s.name);
                        if (res && !res.ok && res.error) {
                          setStatusMessage(`切换状态失败：${res.error}`);
                        }
                      }}
                    >
                      {s.name}
                    </button>
                  ))}
                  {timelineEvents.map((ev) => (
                    <button
                      key={`${ev.eventName}-${ev.time}`}
                      type="button"
                      className="chip-btn chip-trigger"
                      onClick={() => simRef.current?.triggerEvent(ev.eventName)}
                    >
                      <Zap size={12} aria-hidden="true" />
                      <span>Trigger {ev.eventName}</span>
                    </button>
                  ))}
                  {showPylonTrigger && (
                    <button
                      type="button"
                      className="chip-btn chip-trigger"
                      onClick={() => simRef.current?.triggerEvent('shock_ring')}
                    >
                      <Zap size={12} aria-hidden="true" />
                      <span>Trigger shock_ring</span>
                    </button>
                  )}
                  {states.length === 0 && timelineEvents.length === 0 && !showPylonTrigger && (
                    <span className="muted-hint">隐式单状态（无命名状态或事件）</span>
                  )}
                </div>
                {eventLog.length > 0 && (
                  <span className="event-latest" title={eventLog.map((e) => `${e.kind}:${e.message}`).join('\n')}>
                    最近事件：{eventLog[0].kind} · {eventLog[0].message}
                  </span>
                )}
              </div>

              <div className="inspector-row">
                <span className="inspector-label">贴图资源</span>
                <div className="chip-list">
                  <button
                    type="button"
                    className="chip-btn"
                    onClick={() => {
                      pendingTexKeyRef.current = null;
                      texInputRef.current?.click();
                    }}
                  >
                    <FileUp size={13} aria-hidden="true" />
                    <span>拖入或上传贴图</span>
                  </button>
                  <input
                    ref={texInputRef}
                    type="file"
                    accept="image/png,image/webp,image/jpeg"
                    className="visually-hidden"
                    onChange={(e) => {
                      const f = e.target.files?.[0];
                      if (f) void handleTextureUpload(f, pendingTexKeyRef.current);
                      pendingTexKeyRef.current = null;
                      e.currentTarget.value = '';
                    }}
                  />
                  {declaredTextureKeys.map((key) => {
                    const loaded = loadedTextures.find((t) => t.key === key);
                    return (
                      <span key={key} className={`tex-badge ${loaded ? 'loaded' : 'fallback'}`}>
                        <span>{key}</span>
                        {loaded ? (
                          <>
                            <button
                              type="button"
                              className="tex-mini-btn"
                              onClick={() => {
                                const next = !loaded.isSrgb;
                                viewportRef.current?.setTextureSrgb(key, next);
                                setLoadedTextures(viewportRef.current?.getLoadedTextures() ?? []);
                              }}
                            >
                              {loaded.isSrgb ? 'sRGB' : 'Linear'}
                            </button>
                            <button
                              type="button"
                              className="tex-mini-btn"
                              onClick={() => {
                                viewportRef.current?.removeTexture(key);
                                setLoadedTextures(viewportRef.current?.getLoadedTextures() ?? []);
                              }}
                            >
                              清除
                            </button>
                          </>
                        ) : (
                          <button
                            type="button"
                            className="tex-mini-btn"
                            onClick={() => {
                              pendingTexKeyRef.current = key;
                              texInputRef.current?.click();
                            }}
                          >
                            绑定文件
                          </button>
                        )}
                      </span>
                    );
                  })}
                  {extraTextures.map((entry) => (
                    <span key={entry.key} className="tex-badge loaded">
                      <span>{entry.key} ({entry.width}×{entry.height})</span>
                      <button
                        type="button"
                        className="tex-mini-btn"
                        onClick={() => {
                          viewportRef.current?.removeTexture(entry.key);
                          setLoadedTextures(viewportRef.current?.getLoadedTextures() ?? []);
                        }}
                      >
                        移除
                      </button>
                    </span>
                  ))}
                  {declaredTextureKeys.length === 0 && extraTextures.length === 0 && (
                    <span className="muted-hint">未指定 textureKey（使用程序化径向圆盘，可拖拽 PNG 预览）</span>
                  )}
                </div>
              </div>
            </section>
          </Panel>

          <PanelResizeHandle className="resize-handle" aria-label="调整左右工作区宽度" />

          <Panel defaultSize={44} minSize={28} className="pane-column editor-column">
            <div className="editor-header">
              <div className="format-tablist" role="tablist" aria-label="资产格式切换">
                <button
                  type="button"
                  role="tab"
                  aria-selected={format === 'json'}
                  className="format-tab"
                  onClick={() => handleFormatSwitch('json')}
                >
                  .lumen.json
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={format === 'xml'}
                  className="format-tab"
                  onClick={() => handleFormatSwitch('xml')}
                >
                  XML LumenEffectDef
                </button>
              </div>
              <span className="effect-meta-id">
                {validation.effectId ?? activeEffectIdRef.current} ·{' '}
                {validation.ok ? `${(validation.nodes ?? []).length} 节点` : '校验未通过'}
              </span>
            </div>

            {format === 'xml' && (
              <div className="xml-limit-note">
                独立 LumenEffectDef XML 模式：不支持 <code>ParentName</code> 继承、<code>Abstract</code> 模板与{' '}
                <code>PatchOperation</code>。
              </div>
            )}

            <div className={`diag-bar ${validation.ok ? 'diag-ok' : 'diag-error'}`} role={validation.ok ? 'status' : 'alert'}>
              {validation.ok ? (
                <>
                  <CheckCircle2 size={15} aria-hidden="true" />
                  <span>校验通过 · 修改后自动热重载预览</span>
                </>
              ) : (
                <>
                  <AlertTriangle size={15} aria-hidden="true" />
                  <span>
                    第 {validation.line ?? 1} 行 第 {validation.column ?? 1} 列：{validation.error}（画布保持上一合法状态继续运行）
                  </span>
                </>
              )}
            </div>

            <div className="editor-surface" role="tabpanel" aria-label="代码编辑区">
              <CodeEditorPane
                value={sourceText}
                format={format}
                docRevision={docRevision}
                onValidate={handleValidate}
                onValidChange={handleValidChange}
                onValidationError={handleValidationError}
                onUserEdit={handleUserEdit}
              />
            </div>
          </Panel>
        </PanelGroup>
      </main>

      <dialog
        ref={lossyDialogRef}
        className="lossy-dialog"
        onCancel={(e) => {
          e.preventDefault();
          setLossyWarnings(null);
        }}
        onClose={() => setLossyWarnings(null)}
      >
        <h2>导出 XML 有损转换提示</h2>
        <p>当前资产包含 XML 契约无法无损表达的字段，已自动折算为近似值：</p>
        <ul>
          {lossyWarnings?.warnings.map((w, idx) => (
            <li key={idx}>{w}</li>
          ))}
        </ul>
        <div className="dialog-actions">
          <button
            type="button"
            className="btn-secondary"
            onClick={() => setLossyWarnings(null)}
          >
            取消
          </button>
          <button
            type="button"
            className="btn-primary"
            onClick={() => {
              if (!lossyWarnings) return;
              const stem = sanitizeStem(activeEffectIdRef.current);
              downloadTextFile(`${stem}.xml`, lossyWarnings.content, 'application/xml');
              setStatusMessage(`已导出 ${stem}.xml`);
              setLossyWarnings(null);
            }}
          >
            继续导出 XML
          </button>
        </div>
      </dialog>
    </div>
  );
}
