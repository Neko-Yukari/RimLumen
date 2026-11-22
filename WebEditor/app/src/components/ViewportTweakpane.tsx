import { useEffect, useRef } from 'react';
import { Pane } from 'tweakpane';

export interface TweakParams {
  speed: number;
  density: number;
  exposure: number;
  intensity: number;
  rate: number;
  showGrid: boolean;
  showHelpers: boolean;
  orbitMotion: boolean;
  orbitSpeed: number;
  targetWorld: { x: number; y: number; z: number };
  liveParticles: number;
  simMs: number;
}

interface ViewportTweakpaneProps {
  paramsRef: React.MutableRefObject<TweakParams>;
  onParamChange: (key: keyof TweakParams, value: unknown) => void;
  paneApiRef: React.MutableRefObject<Pane | null>;
}

export function ViewportTweakpane({ paramsRef, onParamChange, paneApiRef }: ViewportTweakpaneProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const p = paramsRef.current;
    const pane = new Pane({
      container: host,
      title: '运行与视口参数',
      expanded: false,
    });
    paneApiRef.current = pane;

    const simFolder = pane.addFolder({ title: '模拟与材质', expanded: true });
    simFolder.addBinding(p, 'speed', { label: '倍速', min: 0.1, max: 2.5, step: 0.05 }).on('change', (ev) => {
      onParamChange('speed', ev.value);
    });
    simFolder.addBinding(p, 'density', { label: '粒子密度', min: 0.1, max: 1.0, step: 0.05 }).on('change', (ev) => {
      onParamChange('density', ev.value);
    });
    simFolder.addBinding(p, 'exposure', { label: 'HDR 曝光', min: 0.25, max: 2.5, step: 0.05 }).on('change', (ev) => {
      onParamChange('exposure', ev.value);
    });
    simFolder.addBinding(p, 'intensity', { label: '强度倍率', min: 0, max: 4, step: 0.1 }).on('change', (ev) => {
      onParamChange('intensity', ev.value);
    });
    simFolder.addBinding(p, 'rate', { label: '速率倍率', min: 0, max: 3, step: 0.1 }).on('change', (ev) => {
      onParamChange('rate', ev.value);
    });

    const sceneFolder = pane.addFolder({ title: '辅助与轨迹', expanded: true });
    sceneFolder.addBinding(p, 'showGrid', { label: '地面网格' }).on('change', (ev) => {
      onParamChange('showGrid', ev.value);
    });
    sceneFolder.addBinding(p, 'showHelpers', { label: '光池/扭曲环' }).on('change', (ev) => {
      onParamChange('showHelpers', ev.value);
    });
    sceneFolder.addBinding(p, 'orbitMotion', { label: '轨迹演示旋转' }).on('change', (ev) => {
      onParamChange('orbitMotion', ev.value);
    });
    sceneFolder.addBinding(p, 'orbitSpeed', { label: '角速度', min: 0.2, max: 6.0, step: 0.1 }).on('change', (ev) => {
      onParamChange('orbitSpeed', ev.value);
    });
    sceneFolder.addBinding(p, 'targetWorld', { label: 'Beam 目标点' }).on('change', (ev) => {
      onParamChange('targetWorld', ev.value);
    });

    const statsFolder = pane.addFolder({ title: '实时遥测', expanded: true });
    statsFolder.addBinding(p, 'liveParticles', { label: '粒子数', readonly: true, format: (v: number) => `${Math.round(v)}` });
    statsFolder.addBinding(p, 'simMs', { label: 'WASM ms', readonly: true, view: 'graph', min: 0, max: 8 });

    return () => {
      paneApiRef.current = null;
      pane.dispose();
    };
  }, [onParamChange, paneApiRef, paramsRef]);

  return <div ref={hostRef} className="tweakpane-dock" />;
}
