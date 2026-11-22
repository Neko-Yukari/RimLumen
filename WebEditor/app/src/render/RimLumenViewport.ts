import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import type { DistortionInfo, PointLightInfo, StepFrameResult, SubBatchInfo } from '../wasm/types';
import {
  L1_PARTICLE_FRAGMENT_SHADER,
  L1_PARTICLE_VERTEX_SHADER,
  POINT_LIGHT_FRAGMENT_SHADER,
  POINT_LIGHT_VERTEX_SHADER,
} from './particleShaders';

export type CameraMode = '2d' | '3d';

export interface LoadedTextureEntry {
  key: string;
  fileName: string;
  isSrgb: boolean;
  width: number;
  height: number;
  texture: THREE.Texture;
}

interface DynamicStream {
  vertexData: Float32Array;
  interleaved: THREE.InterleavedBuffer;
  indexData: Uint32Array;
  indexAttr: THREE.BufferAttribute;
  batches: BatchMeshSlot[];
}

interface BatchMeshSlot {
  mesh: THREE.Mesh;
  geometry: THREE.BufferGeometry;
  material: THREE.RawShaderMaterial;
}

const FLOATS_PER_VERTEX = 14; // 56 bytes: pos(3) + uv(2) + color(4) + radialUvGain(3) + depthMask(2)

export class RimLumenViewport {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene: THREE.Scene;
  private readonly orthoCamera: THREE.OrthographicCamera;
  private readonly perspCamera: THREE.PerspectiveCamera;
  private activeCamera: THREE.OrthographicCamera | THREE.PerspectiveCamera;
  private readonly controls2D: OrbitControls;
  private readonly controls3D: OrbitControls;
  private activeControls: OrbitControls;

  private readonly gridHelper: THREE.GridHelper;
  private readonly targetMarker: THREE.Mesh;
  private readonly screenDimMesh: THREE.Mesh;
  private readonly screenDimMat: THREE.MeshBasicMaterial;
  private readonly lightPoolGroup = new THREE.Group();
  private readonly lightSlots: { mesh: THREE.Mesh; mat: THREE.RawShaderMaterial }[] = [];
  private readonly distortionGroup = new THREE.Group();
  private readonly distortionRings: { line: THREE.LineLoop; mat: THREE.LineBasicMaterial }[] = [];

  private readonly simStream: DynamicStream;
  private readonly ribStream: DynamicStream;
  private readonly textures = new Map<string, LoadedTextureEntry>();
  private readonly fallbackWhiteTex: THREE.DataTexture;

  private readonly viewProjMatrix = new THREE.Matrix4();
  private readonly lightModelMatrix = new THREE.Matrix4();
  private readonly resizeObserver: ResizeObserver;

  private cameraMode: CameraMode = '3d';
  private exposure = 1.0;
  private showHelpers = true;
  private width = 640;
  private height = 480;
  private disposed = false;

  constructor(canvas: HTMLCanvasElement) {
    this.renderer = new THREE.WebGLRenderer({
      canvas,
      antialias: true,
      alpha: false,
      preserveDrawingBuffer: true,
      powerPreference: 'high-performance',
    });
    this.renderer.setClearColor(0x0d1015, 1);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;

    this.scene = new THREE.Scene();

    this.orthoCamera = new THREE.OrthographicCamera(-12, 12, 9, -9, 0.3, 500);
    this.perspCamera = new THREE.PerspectiveCamera(45, 4 / 3, 0.1, 500);
    this.activeCamera = this.perspCamera;

    this.controls2D = new OrbitControls(this.orthoCamera, canvas);
    this.controls2D.enableRotate = false;
    this.controls2D.enableZoom = true;
    this.controls2D.enablePan = true;
    this.controls2D.screenSpacePanning = true;

    this.controls3D = new OrbitControls(this.perspCamera, canvas);
    this.controls3D.enableDamping = true;
    this.controls3D.dampingFactor = 0.08;
    this.controls3D.enableRotate = true;
    this.controls3D.enablePan = true;
    this.controls3D.enableZoom = true;

    this.activeControls = this.controls3D;
    this.resetCameras();
    this.syncCameraControls();

    // Subtle dark-slate ground grid (RimWorld cell = 1 unit).
    this.gridHelper = new THREE.GridHelper(32, 32, 0x364154, 0x1d232f);
    this.gridHelper.position.y = -0.02;
    this.scene.add(this.gridHelper);

    // ScreenDim approximate backdrop darkening plane behind particle layer.
    this.screenDimMat = new THREE.MeshBasicMaterial({
      color: 0x000000,
      transparent: true,
      opacity: 0,
      depthWrite: false,
      depthTest: false,
    });
    const dimGeo = new THREE.PlaneGeometry(200, 200);
    dimGeo.rotateX(-Math.PI / 2);
    this.screenDimMesh = new THREE.Mesh(dimGeo, this.screenDimMat);
    this.screenDimMesh.position.y = -0.01;
    this.screenDimMesh.renderOrder = 1;
    this.screenDimMesh.visible = false;
    this.scene.add(this.screenDimMesh);

    this.lightPoolGroup.renderOrder = 2;
    this.scene.add(this.lightPoolGroup);

    this.distortionGroup.renderOrder = 15;
    this.scene.add(this.distortionGroup);

    const markerGeo = new THREE.SphereGeometry(0.18, 14, 10);
    const markerMat = new THREE.MeshBasicMaterial({ color: 0xe5a93c });
    this.targetMarker = new THREE.Mesh(markerGeo, markerMat);
    this.targetMarker.visible = false;
    this.targetMarker.renderOrder = 16;
    this.scene.add(this.targetMarker);

    this.fallbackWhiteTex = new THREE.DataTexture(new Uint8Array([255, 255, 255, 255]), 1, 1);
    this.fallbackWhiteTex.needsUpdate = true;

    this.simStream = this.createStream(1024);
    this.ribStream = this.createStream(256);

    // Modern ResizeObserver pattern supporting devicePixelContentBoxSize with DPR fallback.
    this.resizeObserver = new ResizeObserver((entries) => {
      if (this.disposed || entries.length === 0) return;
      const entry = entries[0];
      const cssW = Math.max(1, Math.floor(entry.contentRect.width));
      const cssH = Math.max(1, Math.floor(entry.contentRect.height));
      const dpc = (entry as ResizeObserverEntry & { devicePixelContentBoxSize?: ReadonlyArray<ResizeObserverSize> })
        .devicePixelContentBoxSize;
      const pixelW = dpc?.[0] ? Math.max(1, Math.round(dpc[0].inlineSize)) : Math.max(1, Math.round(cssW * Math.min(window.devicePixelRatio || 1, 2)));
      const pixelH = dpc?.[0] ? Math.max(1, Math.round(dpc[0].blockSize)) : Math.max(1, Math.round(cssH * Math.min(window.devicePixelRatio || 1, 2)));
      this.handleResize(cssW, cssH, pixelW, pixelH);
    });
    const boxOpt = typeof ResizeObserverEntry !== 'undefined' && 'devicePixelContentBoxSize' in ResizeObserverEntry.prototype
      ? ({ box: 'device-pixel-content-box' } as ResizeObserverOptions)
      : undefined;
    try {
      this.resizeObserver.observe(canvas, boxOpt);
    } catch {
      this.resizeObserver.observe(canvas);
    }
  }

  getCameraMode(): CameraMode {
    return this.cameraMode;
  }

  setCameraMode(mode: CameraMode): void {
    if (this.cameraMode === mode) return;
    this.cameraMode = mode;
    this.syncCameraControls();
    this.updateOrthoFrustum();
  }

  resetCameras(): void {
    // 2D RimWorld straight-down camera: up = -Z so world +X is screen-right and Vertical3D pitches upward on screen.
    this.orthoCamera.position.set(0, 30, 0);
    this.orthoCamera.up.set(0, 0, -1);
    this.orthoCamera.zoom = 1;
    this.controls2D.target.set(0, 0, 0);
    this.orthoCamera.lookAt(0, 0, 0);
    this.orthoCamera.updateProjectionMatrix();
    this.controls2D.update();

    this.perspCamera.position.set(0, 11.0, 22.0);
    this.perspCamera.up.set(0, 1, 0);
    this.controls3D.target.set(0, 4.5, 0);
    this.perspCamera.lookAt(0, 4.5, 0);
    this.perspCamera.updateProjectionMatrix();
    this.controls3D.update();
  }

  setExposure(exposure: number): void {
    this.exposure = Math.max(0.05, exposure);
  }

  setShowGrid(show: boolean): void {
    this.gridHelper.visible = show;
  }

  setShowHelpers(show: boolean): void {
    this.showHelpers = show;
    this.distortionGroup.visible = show;
  }

  setTargetMarker(visible: boolean, x: number, y: number, z: number): void {
    this.targetMarker.visible = visible && this.showHelpers;
    this.targetMarker.position.set(x, y, z);
  }

  async loadTextureFile(textureKey: string, file: File, isSrgb = true): Promise<LoadedTextureEntry> {
    const objectUrl = URL.createObjectURL(file);
    try {
      const img = await new Promise<HTMLImageElement>((resolve, reject) => {
        const el = new Image();
        el.onload = () => resolve(el);
        el.onerror = () => reject(new Error(`无法解码贴图文件 '${file.name}'`));
        el.src = objectUrl;
      });
      // CpuParticleSimulator.cs:813-816 sets v=1 at image top, matching Three.js default flipY = true.
      const tex = new THREE.Texture(img);
      tex.flipY = true;
      tex.colorSpace = THREE.NoColorSpace; // Raw stored texel; GLSL handles sRGB decode when colorSpace == Linear.
      tex.wrapS = THREE.ClampToEdgeWrapping;
      tex.wrapT = THREE.ClampToEdgeWrapping;
      tex.minFilter = THREE.LinearMipmapLinearFilter;
      tex.magFilter = THREE.LinearFilter;
      tex.generateMipmaps = true;
      tex.needsUpdate = true;

      const prev = this.textures.get(textureKey);
      if (prev) prev.texture.dispose();

      const entry: LoadedTextureEntry = {
        key: textureKey,
        fileName: file.name,
        isSrgb,
        width: img.naturalWidth,
        height: img.naturalHeight,
        texture: tex,
      };
      this.textures.set(textureKey, entry);
      return entry;
    } finally {
      URL.revokeObjectURL(objectUrl);
    }
  }

  setTextureSrgb(textureKey: string, isSrgb: boolean): void {
    const entry = this.textures.get(textureKey);
    if (entry) entry.isSrgb = isSrgb;
  }

  removeTexture(textureKey: string): void {
    const entry = this.textures.get(textureKey);
    if (entry) {
      entry.texture.dispose();
      this.textures.delete(textureKey);
    }
  }

  getLoadedTextures(): LoadedTextureEntry[] {
    return Array.from(this.textures.values());
  }

  getCameraMatrices(): { width: number; height: number; view: number[]; proj: number[] } {
    this.activeControls.update();
    this.activeCamera.updateMatrixWorld();
    return {
      width: this.width,
      height: this.height,
      view: Array.from(this.activeCamera.matrixWorldInverse.elements),
      proj: Array.from(this.activeCamera.projectionMatrix.elements),
    };
  }

  renderFrame(
    frame: StepFrameResult,
    simVertBytes: Uint8Array,
    simIdxBytes: Uint8Array,
    ribVertBytes: Uint8Array,
    ribIdxBytes: Uint8Array,
  ): void {
    if (this.disposed) return;

    this.activeCamera.updateMatrixWorld();
    this.viewProjMatrix.multiplyMatrices(
      this.activeCamera.projectionMatrix,
      this.activeCamera.matrixWorldInverse,
    );

    // ScreenDim backdrop approximation
    const cov = Math.min(1, Math.max(0, frame.screenDimCoverage));
    this.screenDimMesh.visible = cov > 0.001;
    this.screenDimMat.opacity = cov;

    // Ground point lights & distortion indicator helpers
    this.updatePointLights(frame.pointLights);
    this.updateDistortionHelpers(frame.distortions);

    // Particle & trail stream
    this.uploadAndBindStream(this.simStream, frame.simBatches, simVertBytes, simIdxBytes, 10);
    // Node ribbon / beam stream
    this.uploadAndBindStream(this.ribStream, frame.ribBatches, ribVertBytes, ribIdxBytes, 12);

    this.renderer.render(this.scene, this.activeCamera);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.resizeObserver.disconnect();
    this.controls2D.dispose();
    this.controls3D.dispose();
    this.disposeStream(this.simStream);
    this.disposeStream(this.ribStream);
    for (const slot of this.lightSlots) {
      slot.mesh.geometry.dispose();
      slot.mat.dispose();
    }
    for (const ring of this.distortionRings) {
      ring.line.geometry.dispose();
      ring.mat.dispose();
    }
    this.screenDimMesh.geometry.dispose();
    this.screenDimMat.dispose();
    this.targetMarker.geometry.dispose();
    (this.targetMarker.material as THREE.Material).dispose();
    this.gridHelper.dispose();
    for (const entry of this.textures.values()) {
      entry.texture.dispose();
    }
    this.textures.clear();
    this.fallbackWhiteTex.dispose();
    this.renderer.dispose();
  }

  private syncCameraControls(): void {
    if (this.cameraMode === '2d') {
      this.activeCamera = this.orthoCamera;
      this.controls3D.enabled = false;
      this.controls2D.enabled = true;
      this.activeControls = this.controls2D;
    } else {
      this.activeCamera = this.perspCamera;
      this.controls2D.enabled = false;
      this.controls3D.enabled = true;
      this.activeControls = this.controls3D;
    }
  }

  private handleResize(cssW: number, cssH: number, pixelW: number, pixelH: number): void {
    this.width = cssW;
    this.height = cssH;
    this.renderer.setPixelRatio(1);
    this.renderer.setSize(pixelW, pixelH, false);
    this.perspCamera.aspect = cssW / cssH;
    this.perspCamera.updateProjectionMatrix();
    this.updateOrthoFrustum();
  }

  private updateOrthoFrustum(): void {
    const halfH = 10;
    const halfW = halfH * (this.width / Math.max(1, this.height));
    this.orthoCamera.left = -halfW;
    this.orthoCamera.right = halfW;
    this.orthoCamera.top = halfH;
    this.orthoCamera.bottom = -halfH;
    this.orthoCamera.updateProjectionMatrix();
  }

  private createStream(initialVerts: number): DynamicStream {
    const vertexData = new Float32Array(initialVerts * FLOATS_PER_VERTEX);
    const interleaved = new THREE.InterleavedBuffer(vertexData, FLOATS_PER_VERTEX);
    interleaved.setUsage(THREE.DynamicDrawUsage);
    const indexData = new Uint32Array(initialVerts * 2);
    const indexAttr = new THREE.BufferAttribute(indexData, 1);
    indexAttr.setUsage(THREE.DynamicDrawUsage);
    return { vertexData, interleaved, indexData, indexAttr, batches: [] };
  }

  private uploadAndBindStream(
    stream: DynamicStream,
    batches: SubBatchInfo[],
    vertBytes: Uint8Array,
    idxBytes: Uint8Array,
    baseRenderOrder: number,
  ): void {
    const floatCount = vertBytes.byteLength >>> 2;
    const idxCount = idxBytes.byteLength >>> 2;

    if (batches.length === 0 || floatCount === 0 || idxCount === 0) {
      for (const slot of stream.batches) slot.mesh.visible = false;
      return;
    }

    let layoutRecreated = false;
    if (floatCount > stream.vertexData.length) {
      stream.vertexData = new Float32Array(Math.max(floatCount, stream.vertexData.length * 2));
      stream.interleaved = new THREE.InterleavedBuffer(stream.vertexData, FLOATS_PER_VERTEX);
      stream.interleaved.setUsage(THREE.DynamicDrawUsage);
      layoutRecreated = true;
    }
    if (idxCount > stream.indexData.length) {
      stream.indexData = new Uint32Array(Math.max(idxCount, stream.indexData.length * 2));
      stream.indexAttr = new THREE.BufferAttribute(stream.indexData, 1);
      stream.indexAttr.setUsage(THREE.DynamicDrawUsage);
      layoutRecreated = true;
    }

    const srcFloats = new Float32Array(vertBytes.buffer, vertBytes.byteOffset, floatCount);
    stream.vertexData.set(srcFloats);
    stream.interleaved.needsUpdate = true;

    const srcIndices = new Uint32Array(idxBytes.buffer, idxBytes.byteOffset, idxCount);
    stream.indexData.set(srcIndices);
    stream.indexAttr.needsUpdate = true;

    while (stream.batches.length < batches.length) {
      const slot = this.createBatchSlot(stream);
      stream.batches.push(slot);
      this.scene.add(slot.mesh);
    }

    for (let i = 0; i < stream.batches.length; i++) {
      const slot = stream.batches[i];
      if (layoutRecreated) {
        this.bindStreamToGeometry(slot.geometry, stream);
      }
      if (i >= batches.length || batches[i].indexCount <= 0) {
        slot.mesh.visible = false;
        continue;
      }
      const b = batches[i];
      slot.geometry.setDrawRange(b.indexStart, b.indexCount);
      this.configureBatchMaterial(slot.material, b);
      slot.mesh.renderOrder = baseRenderOrder + (b.blendMode === 'AlphaBlend' ? 0 : 1);
      slot.mesh.visible = true;
    }
  }

  private createBatchSlot(stream: DynamicStream): BatchMeshSlot {
    const geometry = new THREE.BufferGeometry();
    const material = new THREE.RawShaderMaterial({
      glslVersion: THREE.GLSL3,
      vertexShader: L1_PARTICLE_VERTEX_SHADER,
      fragmentShader: L1_PARTICLE_FRAGMENT_SHADER,
      transparent: true,
      depthWrite: false,
      depthTest: true,
      side: THREE.DoubleSide,
      uniforms: {
        uViewProj: { value: this.viewProjMatrix },
        uLumenDraw: { value: new THREE.Vector4(0, 1, 0, 0) },
        uLumenMaterialTint: { value: new THREE.Vector4(1, 1, 1, 1) },
        uLumenColor: { value: new THREE.Vector4(0, 1, 0, 0) },
        uExposure: { value: this.exposure },
        uParticleTexture: { value: this.fallbackWhiteTex },
      },
    });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.frustumCulled = false;
    mesh.visible = false;
    const slot: BatchMeshSlot = { mesh, geometry, material };
    this.bindStreamToGeometry(geometry, stream);
    return slot;
  }

  private bindStreamToGeometry(geometry: THREE.BufferGeometry, stream: DynamicStream): void {
    const ib = stream.interleaved;
    geometry.setAttribute('inPosition', new THREE.InterleavedBufferAttribute(ib, 3, 0, false));
    geometry.setAttribute('inUv', new THREE.InterleavedBufferAttribute(ib, 2, 3, false));
    geometry.setAttribute('inColor', new THREE.InterleavedBufferAttribute(ib, 4, 5, false));
    geometry.setAttribute('inRadialUvGain', new THREE.InterleavedBufferAttribute(ib, 3, 9, false));
    geometry.setAttribute('inDepthMask', new THREE.InterleavedBufferAttribute(ib, 2, 12, false));
    geometry.setIndex(stream.indexAttr);
  }

  private configureBatchMaterial(mat: THREE.RawShaderMaterial, batch: SubBatchInfo): void {
    const texEntry = batch.textureKey ? this.textures.get(batch.textureKey) : undefined;
    const hasTex = texEntry !== undefined;
    const premultiply = batch.blendMode === 'Additive' || batch.blendMode === 'Premultiplied';

    mat.uniforms.uViewProj.value = this.viewProjMatrix;
    (mat.uniforms.uLumenDraw.value as THREE.Vector4).set(hasTex ? 1 : 0, premultiply ? 1 : 0, 0, 0);
    (mat.uniforms.uLumenMaterialTint.value as THREE.Vector4).set(
      batch.materialTint[0],
      batch.materialTint[1],
      batch.materialTint[2],
      batch.materialTint[3],
    );
    (mat.uniforms.uLumenColor.value as THREE.Vector4).set(
      batch.colorSpace === 'Linear' ? 1 : 0,
      hasTex && texEntry.isSrgb ? 1 : 0,
      0,
      0,
    );
    mat.uniforms.uExposure.value = this.exposure;
    mat.uniforms.uParticleTexture.value = texEntry ? texEntry.texture : this.fallbackWhiteTex;

    if (batch.blendMode === 'Additive') {
      mat.blending = THREE.CustomBlending;
      mat.blendEquation = THREE.AddEquation;
      mat.blendSrc = THREE.OneFactor;
      mat.blendDst = THREE.OneFactor;
    } else if (batch.blendMode === 'Premultiplied') {
      mat.blending = THREE.CustomBlending;
      mat.blendEquation = THREE.AddEquation;
      mat.blendSrc = THREE.OneFactor;
      mat.blendDst = THREE.OneMinusSrcAlphaFactor;
    } else {
      mat.blending = THREE.NormalBlending;
    }
  }

  private updatePointLights(lights: PointLightInfo[]): void {
    while (this.lightSlots.length < lights.length) {
      const geo = new THREE.PlaneGeometry(2, 2);
      geo.rotateX(-Math.PI / 2);
      const mat = new THREE.RawShaderMaterial({
        glslVersion: THREE.GLSL3,
        vertexShader: POINT_LIGHT_VERTEX_SHADER,
        fragmentShader: POINT_LIGHT_FRAGMENT_SHADER,
        transparent: true,
        depthWrite: false,
        depthTest: false,
        blending: THREE.AdditiveBlending,
        uniforms: {
          uViewProj: { value: this.viewProjMatrix },
          uModel: { value: new THREE.Matrix4() },
          uLightColor: { value: new THREE.Vector3() },
        },
      });
      const mesh = new THREE.Mesh(geo, mat);
      mesh.frustumCulled = false;
      mesh.visible = false;
      this.lightSlots.push({ mesh, mat });
      this.lightPoolGroup.add(mesh);
    }

    for (let i = 0; i < this.lightSlots.length; i++) {
      const slot = this.lightSlots[i];
      if (i >= lights.length || !this.showHelpers) {
        slot.mesh.visible = false;
        continue;
      }
      const l = lights[i];
      this.lightModelMatrix.makeScale(l.radius, 1, l.radius);
      this.lightModelMatrix.setPosition(l.x, 0.015, l.z);
      (slot.mat.uniforms.uModel.value as THREE.Matrix4).copy(this.lightModelMatrix);
      slot.mat.uniforms.uViewProj.value = this.viewProjMatrix;
      (slot.mat.uniforms.uLightColor.value as THREE.Vector3).set(l.r, l.g, l.b);
      slot.mesh.visible = true;
    }
  }

  private updateDistortionHelpers(distortions: DistortionInfo[]): void {
    if (!this.showHelpers || distortions.length === 0) {
      for (const r of this.distortionRings) r.line.visible = false;
      return;
    }

    const activeRings: { x: number; z: number; radius: number; opacity: number; color: number }[] = [];
    for (const d of distortions) {
      if (d.kind === 'HeatHaze') {
        activeRings.push({ x: d.x, z: d.z, radius: d.radius, opacity: 0.4, color: 0x62b4ff });
      } else {
        for (let k = 0; k < 4; k++) {
          const r = d.ringRadii[k];
          const a = Math.abs(d.ringAmplitudes[k]);
          if (r > 0.02 && r <= d.radius && a > 0.001) {
            activeRings.push({
              x: d.x,
              z: d.z,
              radius: r,
              opacity: Math.min(0.85, 0.25 + a),
              color: 0xe5a93c,
            });
          }
        }
      }
    }

    while (this.distortionRings.length < activeRings.length) {
      const pts: THREE.Vector3[] = [];
      for (let s = 0; s < 48; s++) {
        const theta = (s / 48) * Math.PI * 2;
        pts.push(new THREE.Vector3(Math.cos(theta), 0.04, Math.sin(theta)));
      }
      const geo = new THREE.BufferGeometry().setFromPoints(pts);
      const mat = new THREE.LineBasicMaterial({ color: 0xe5a93c, transparent: true, opacity: 0.6 });
      const line = new THREE.LineLoop(geo, mat);
      line.frustumCulled = false;
      this.distortionRings.push({ line, mat });
      this.distortionGroup.add(line);
    }

    for (let i = 0; i < this.distortionRings.length; i++) {
      const slot = this.distortionRings[i];
      if (i >= activeRings.length) {
        slot.line.visible = false;
        continue;
      }
      const spec = activeRings[i];
      slot.line.position.set(spec.x, 0, spec.z);
      slot.line.scale.set(spec.radius, 1, spec.radius);
      slot.mat.color.setHex(spec.color);
      slot.mat.opacity = spec.opacity;
      slot.line.visible = true;
    }
  }

  private disposeStream(stream: DynamicStream): void {
    for (const b of stream.batches) {
      b.geometry.dispose();
      b.material.dispose();
    }
    stream.batches = [];
  }
}
