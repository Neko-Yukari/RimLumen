declare module '@tweakpane/core' {
  export interface Semver {
    major: number;
    minor: number;
    patch: number;
  }

  export interface BaseParams {
    disabled?: boolean;
    hidden?: boolean;
    index?: number;
  }

  export interface BaseBladeParams extends BaseParams {
    view: string;
  }

  export interface FolderParams extends BaseParams {
    expanded?: boolean;
    title: string;
  }

  export interface ButtonParams extends BaseParams {
    label?: string;
    title: string;
  }

  export interface TabPageParams {
    title: string;
  }

  export interface TabParams extends BaseParams {
    pages: TabPageParams[];
  }

  export type ListParamsOptions<T> = ArrayStyleListOptions<T> | ObjectStyleListOptions<T>;

  export interface ArrayStyleListOptions<T> {
    options: Array<{ text: string; value: T }>;
  }

  export type ObjectStyleListOptions<T> = Record<string, T>;

  export interface NumberInputParams extends BaseParams {
    label?: string;
    min?: number;
    max?: number;
    step?: number;
    format?: (value: number) => string;
  }

  export interface BooleanInputParams extends BaseParams {
    label?: string;
  }

  export interface StringInputParams extends BaseParams {
    label?: string;
  }

  export interface ColorInputParams extends BaseParams {
    label?: string;
    alpha?: boolean;
    color?: { type?: string };
    picker?: 'inline' | 'popup';
  }

  export interface Point2dInputParams extends BaseParams {
    label?: string;
    x?: NumberInputParams;
    y?: NumberInputParams;
  }

  export interface Point3dInputParams extends BaseParams {
    label?: string;
    x?: NumberInputParams;
    y?: NumberInputParams;
    z?: NumberInputParams;
  }

  export interface Point4dInputParams extends BaseParams {
    label?: string;
  }

  export interface NumberMonitorParams extends BaseParams {
    readonly: true;
    label?: string;
    bufferSize?: number;
    interval?: number;
    rows?: number;
    min?: number;
    max?: number;
    view?: 'graph';
    format?: (value: number) => string;
  }

  export interface BooleanMonitorParams extends BaseParams {
    readonly: true;
    label?: string;
    bufferSize?: number;
    interval?: number;
  }

  export interface StringMonitorParams extends BaseParams {
    readonly: true;
    label?: string;
    bufferSize?: number;
    interval?: number;
    multiline?: boolean;
    rows?: number;
  }

  export type BindingParams =
    | NumberInputParams
    | BooleanInputParams
    | StringInputParams
    | ColorInputParams
    | Point2dInputParams
    | Point3dInputParams
    | Point4dInputParams
    | NumberMonitorParams
    | BooleanMonitorParams
    | StringMonitorParams
    | (BaseParams & Record<string, unknown>);

  export interface TpChangeEvent<T> {
    value: T;
    last: boolean;
    presetKey?: string;
  }

  export interface BindingApiEvents<T> {
    change: TpChangeEvent<T>;
  }

  export class BladeApi {
    dispose(): void;
  }

  export class BindingApi<T = unknown> extends BladeApi {
    on(eventName: 'change', handler: (ev: TpChangeEvent<T>) => void): this;
    refresh(): void;
  }

  export class InputBindingApi<T = unknown> extends BindingApi<T> {}
  export class MonitorBindingApi<T = unknown> extends BindingApi<T> {}
  export class SliderInputBindingApi extends InputBindingApi<number> {}
  export class ListInputBindingApi<T = unknown> extends InputBindingApi<T> {}
  export class ButtonApi extends BladeApi {
    on(eventName: 'click', handler: () => void): this;
  }
  export class TabPageApi {
    addFolder(params: FolderParams): FolderApi;
    addBinding<O extends object, Key extends keyof O>(
      object: O,
      key: Key,
      params?: BindingParams,
    ): BindingApi<O[Key]>;
  }
  export class TabApi extends BladeApi {
    readonly pages: TabPageApi[];
  }

  export class FolderApi extends BladeApi {
    expanded: boolean;
    title: string;
    addFolder(params: FolderParams): FolderApi;
    addBinding<O extends object, Key extends keyof O>(
      object: O,
      key: Key,
      params?: BindingParams,
    ): BindingApi<O[Key]>;
    addBlade(params: BaseBladeParams): BladeApi;
    addButton(params: ButtonParams): ButtonApi;
    addTab(params: TabParams): TabApi;
    refresh(): void;
  }

  export interface PluginPool {}

  export interface TpPlugin {
    id: string;
  }

  export interface TpPluginBundle {
    id: string;
    plugins: TpPlugin[];
  }
}
