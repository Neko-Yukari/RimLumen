export type AssetFormat = 'json' | 'xml';

export interface InitResult {
  ok: boolean;
  particleVertexSize: number;
  catalogCount: number;
}

export interface DemoSummary {
  effectId: string;
  defaultDuration: number;
  nodeCount: number;
  stateCount: number;
  textureKeys: string[];
}

export interface ExportResult {
  ok: boolean;
  format?: AssetFormat;
  content?: string;
  warnings?: string[];
  error?: string;
  line?: number;
  column?: number;
}

export interface NodeSummary {
  index: number;
  name: string;
  parentIndex: number;
  spacePlane: string;
  hasEmitter: boolean;
  hasPointLight: boolean;
  hasDistortion: boolean;
  hasRibbon: boolean;
  hasScreenDim: boolean;
}

export interface StateSummary {
  name: string;
  duration: number;
  loop: boolean;
  next: string | null;
  nodes: string[];
}

export interface TimelineEventSummary {
  time: number;
  eventName: string;
  targetNodeIndex: number;
  state: string | null;
  notify: boolean;
}

export interface ValidationResult {
  ok: boolean;
  format?: AssetFormat;
  effectId?: string;
  defaultDuration?: number;
  initialState?: string | null;
  stopState?: string | null;
  states?: StateSummary[];
  nodes?: NodeSummary[];
  timelineEvents?: TimelineEventSummary[];
  textureKeys?: string[];
  error?: string;
  line?: number;
  column?: number;
}

export interface SubBatchInfo {
  textureKey: string | null;
  blendMode: 'Additive' | 'AlphaBlend' | 'Premultiplied';
  indexStart: number;
  indexCount: number;
  softParticleFadeDistance: number;
  materialTint: [number, number, number, number];
  colorSpace: 'Stored' | 'Linear';
}

export interface PointLightInfo {
  x: number;
  y: number;
  z: number;
  radius: number;
  r: number;
  g: number;
  b: number;
}

export interface DistortionInfo {
  kind: 'RingWave' | 'Ripple' | 'HeatHaze';
  x: number;
  y: number;
  z: number;
  radius: number;
  strength: number;
  thickness: number;
  frequency: number;
  elapsed: number;
  ringRadii: [number, number, number, number];
  ringAmplitudes: [number, number, number, number];
  hazeAmplitude: number;
  hazePhase: number;
}

export interface FrameEventInfo {
  kind: string;
  message: string;
}

export interface StepFrameResult {
  alive: boolean;
  liveParticles: number;
  currentState: string | null;
  simMs: number;
  simVertexCount: number;
  simIndexCount: number;
  ribVertexCount: number;
  ribIndexCount: number;
  screenDimCoverage: number;
  simBatches: SubBatchInfo[];
  ribBatches: SubBatchInfo[];
  pointLights: PointLightInfo[];
  distortions: DistortionInfo[];
  events: FrameEventInfo[];
}
