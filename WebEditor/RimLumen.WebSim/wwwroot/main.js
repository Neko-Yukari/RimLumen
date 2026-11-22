import { dotnet } from './_framework/dotnet.js';

const statusEl = document.getElementById('status');

function setStatus(text, data) {
  statusEl.textContent = text;
  if (data !== undefined) {
    window.__SPIKE_RESULT__ = data;
  }
}

async function run() {
  const { getAssemblyExports, getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .create();

  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  const api = exports.RimLumen.WebSim.WebSimApi;

  const initInfo = JSON.parse(api.Initialize());
  window.__SPIKE_READY__ = true;

  const demos = JSON.parse(api.ListDemosJson());
  const fireworkXmlResp = JSON.parse(api.GetDemoSource('RimLumen.Demo.Firework.Gold', 'xml', true));
  const fireworkJsonResp = JSON.parse(api.GetDemoSource('RimLumen.Demo.Firework.Gold', 'json', true));

  const validXmlTorch = `<Defs><RimLumen.Core.Assets.LumenEffectDef><defName>MyTorchBurst</defName><nodes><li><name>sparks</name><spacePlane>Vertical3D</spacePlane><emitter><maxParticles>300</maxParticles><rateOverTime>60</rateOverTime><shape>Cone</shape><coneAngleDegrees>22</coneAngleDegrees></emitter></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>`;
  const legacyXmlBad = `<Defs><RimLumen.Core.Assets.LumenEffectDef><defName>LegacyXml</defName><nodes><li><name>sparks</name><emitter><startColorLinear>(1,1,1,1)</startColorLinear></emitter></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>`;
  const parentNameXmlBad = `<Defs><RimLumen.Core.Assets.LumenEffectDef ParentName="BaseEffect"><defName>Inherited</defName><nodes><li><name>sparks</name><emitter /></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>`;
  const validJson = `{"effectId":"SmokeJson","nodes":[{"name":"core","emitter":{"rateOverTime":50}}]}`;

  const validation = {
    validJson: JSON.parse(api.ValidateSource(validJson, 'json')),
    validXml: JSON.parse(api.ValidateSource(validXmlTorch, 'xml')),
    legacyXmlRejected: JSON.parse(api.ValidateSource(legacyXmlBad, 'xml')),
    parentNameRejected: JSON.parse(api.ValidateSource(parentNameXmlBad, 'xml')),
  };

  const playXml = JSON.parse(api.LoadAndPlay(fireworkXmlResp.content, 'xml', ''));
  const frames = [];
  let maxVerts = 0;
  let lastSimBytesLen = 0;
  for (let i = 0; i < 75; i++) {
    const f = JSON.parse(api.StepFrame(1 / 60, 1280, 720, [], []));
    if (f.simVertexCount > maxVerts) maxVerts = f.simVertexCount;
    if (i === 70) {
      lastSimBytesLen = api.GetSimVerticesBytes().length;
    }
    frames.push(f.liveParticles);
  }

  const exportRoundTrip = JSON.parse(api.ExportSource(fireworkXmlResp.content, 'xml', 'json', true));

  const result = {
    initInfo,
    demoIds: demos.map((d) => d.effectId),
    fireworkXmlOk: fireworkXmlResp.ok,
    fireworkXmlWarnings: fireworkXmlResp.warnings,
    fireworkJsonOk: fireworkJsonResp.ok,
    validation,
    playXmlOk: playXml.ok,
    maxVerts,
    lastSimBytesLen,
    maxLive: Math.max(...frames),
    exportXmlToJsonOk: exportRoundTrip.ok,
  };

  setStatus(JSON.stringify(result, null, 2), result);
  window.__SPIKE_DONE__ = true;
}

run().catch((err) => {
  console.error(err);
  setStatus('ERROR: ' + (err?.stack || err));
  window.__SPIKE_ERROR__ = String(err);
  window.__SPIKE_DONE__ = true;
});
