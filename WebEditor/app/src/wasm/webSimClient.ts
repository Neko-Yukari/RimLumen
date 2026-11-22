import type {
  AssetFormat,
  DemoSummary,
  ExportResult,
  InitResult,
  StepFrameResult,
  ValidationResult,
} from './types';

interface RawWebSimApi {
  Initialize(): string;
  ListDemosJson(): string;
  GetDemoSource(effectId: string, format: string, compact: boolean): string;
  ValidateSource(source: string, format: string): string;
  ExportSource(source: string, fromFormat: string, toFormat: string, compact: boolean): string;
  LoadAndPlay(source: string, format: string, initialState: string): string;
  PlayDemo(effectId: string): string;
  Restart(): string;
  Stop(allowFadeOut: boolean): void;
  GoToState(stateName: string): string;
  TriggerEvent(name: string): void;
  SetParam(name: string, value: number): string;
  SetParticleDensity(density: number): void;
  SetTargetWorld(x: number, y: number, z: number): void;
  SetOrbitMotion(enabled: boolean, radius: number, angularSpeed: number): void;
  StepFrame(dt: number, viewportWidth: number, viewportHeight: number, viewColMajor: number[], projColMajor: number[]): string;
  GetSimVerticesBytes(): Uint8Array;
  GetSimIndicesBytes(): Uint8Array;
  GetRibbonVerticesBytes(): Uint8Array;
  GetRibbonIndicesBytes(): Uint8Array;
}

export class WebSimClient {
  private static initPromise: Promise<WebSimClient> | null = null;
  private readonly api: RawWebSimApi;
  readonly initInfo: InitResult;

  private constructor(api: RawWebSimApi, initInfo: InitResult) {
    this.api = api;
    this.initInfo = initInfo;
  }

  static create(): Promise<WebSimClient> {
    if (!WebSimClient.initPromise) {
      WebSimClient.initPromise = WebSimClient.createInternal().catch((err) => {
        WebSimClient.initPromise = null;
        throw err;
      });
    }
    return WebSimClient.initPromise;
  }

  private static async createInternal(): Promise<WebSimClient> {
    const baseUrl = import.meta.env.BASE_URL.endsWith('/') ? import.meta.env.BASE_URL : `${import.meta.env.BASE_URL}/`;
    const dotnetUrl = new URL(`${baseUrl}wasm/_framework/dotnet.js`, window.location.origin).href;
    const mod = (await import(/* @vite-ignore */ dotnetUrl)) as {
      dotnet: {
        withDiagnosticTracing(enabled: boolean): {
          create(): Promise<{
            getConfig(): { mainAssemblyName: string };
            getAssemblyExports(name: string): Promise<{ RimLumen: { WebSim: { WebSimApi: RawWebSimApi } } }>;
          }>;
        };
      };
    };

    const runtime = await mod.dotnet.withDiagnosticTracing(false).create();
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    const api = exports.RimLumen.WebSim.WebSimApi;
    const initInfo = JSON.parse(api.Initialize()) as InitResult;
    return new WebSimClient(api, initInfo);
  }

  listDemos(): DemoSummary[] {
    return JSON.parse(this.api.ListDemosJson()) as DemoSummary[];
  }

  getDemoSource(effectId: string, format: AssetFormat, compact = true): ExportResult {
    return JSON.parse(this.api.GetDemoSource(effectId, format, compact)) as ExportResult;
  }

  validateSource(source: string, format: AssetFormat): ValidationResult {
    return JSON.parse(this.api.ValidateSource(source, format)) as ValidationResult;
  }

  exportSource(source: string, fromFormat: AssetFormat, toFormat: AssetFormat, compact = true): ExportResult {
    return JSON.parse(this.api.ExportSource(source, fromFormat, toFormat, compact)) as ExportResult;
  }

  loadAndPlay(source: string, format: AssetFormat, initialState = ''): ValidationResult {
    return JSON.parse(this.api.LoadAndPlay(source, format, initialState)) as ValidationResult;
  }

  playDemo(effectId: string): ValidationResult {
    return JSON.parse(this.api.PlayDemo(effectId)) as ValidationResult;
  }

  restart(): ValidationResult {
    return JSON.parse(this.api.Restart()) as ValidationResult;
  }

  stop(allowFadeOut: boolean): void {
    this.api.Stop(allowFadeOut);
  }

  goToState(stateName: string): { ok: boolean; error?: string } {
    return JSON.parse(this.api.GoToState(stateName)) as { ok: boolean; error?: string };
  }

  triggerEvent(name: string): void {
    this.api.TriggerEvent(name);
  }

  setParam(name: string, value: number): { ok: boolean; error?: string } {
    return JSON.parse(this.api.SetParam(name, value)) as { ok: boolean; error?: string };
  }

  setParticleDensity(density: number): void {
    this.api.SetParticleDensity(density);
  }

  setTargetWorld(x: number, y: number, z: number): void {
    this.api.SetTargetWorld(x, y, z);
  }

  setOrbitMotion(enabled: boolean, radius: number, angularSpeed: number): void {
    this.api.SetOrbitMotion(enabled, radius, angularSpeed);
  }

  stepFrame(
    dt: number,
    viewportWidth: number,
    viewportHeight: number,
    viewColMajor: number[],
    projColMajor: number[],
  ): StepFrameResult {
    return JSON.parse(
      this.api.StepFrame(dt, viewportWidth, viewportHeight, viewColMajor, projColMajor),
    ) as StepFrameResult;
  }

  getSimVerticesBytes(): Uint8Array {
    return this.api.GetSimVerticesBytes();
  }

  getSimIndicesBytes(): Uint8Array {
    return this.api.GetSimIndicesBytes();
  }

  getRibbonVerticesBytes(): Uint8Array {
    return this.api.GetRibbonVerticesBytes();
  }

  getRibbonIndicesBytes(): Uint8Array {
    return this.api.GetRibbonIndicesBytes();
  }
}
