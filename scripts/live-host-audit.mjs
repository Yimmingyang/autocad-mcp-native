// Manual AutoCAD-host read audit. CAD must be running with this plugin build loaded.
// Usage: node scripts/live-host-audit.mjs <drawing.dwg> [more.dwg ...]
import { basename, resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { createServer } from '../src/index.js';

const paths = process.argv.slice(2).map((p) => resolve(p));
if (!paths.length) {
  console.error('Pass one or more DWG/DXF paths.');
  process.exit(2);
}

const server = createServer();
const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
const client = new Client({ name: 'autocad-live-read-audit', version: '1.0.0' });

async function call(name, args = {}) {
  const result = await client.callTool({ name, arguments: args });
  if (result.isError) throw new Error(`${name}: ${result.content?.[0]?.text ?? 'unknown error'}`);
  const raw = result.content?.find((part) => part.type === 'text')?.text;
  if (!raw) throw new Error(`${name}: MCP returned no text result`);
  return JSON.parse(raw);
}

try {
  await server.connect(serverTransport);
  await client.connect(clientTransport);
  const advertised = await client.listTools();
  const status = await call('get_bridge_status');
  const reports = [];

  for (const path of paths) {
    await call('open_drawing', { path, readOnly: true });
    const audit = await call('audit_drawing_read');
    const geometry = await call('inspect_drawing_geometry', { limit: 20000, includeSplines: true });
    const extracted = await call('cad_call', { tool: 'extract_parameters', args: {} });
    reports.push({
      drawing: basename(path),
      modelSpace: {
        enumeratedObjectIds: audit.enumeratedObjectIds,
        entityCount: audit.entityCount,
        nonEntityObjects: audit.nonEntityObjects,
        unreadableEntityCount: audit.unreadableEntityCount,
        entityCountsByDxfType: audit.entityCountsByDxfType,
        unclassifiedEntityCountsByDxfType: audit.unclassifiedEntityCountsByDxfType,
        rasterAndUnderlayTypesForVisualOrOcrReview: audit.rasterAndUnderlayTypesForVisualOrOcrReview,
        proxyEntityTypesForSeparateReview: audit.proxyEntityTypesForSeparateReview,
        enumerationComplete: audit.enumerationComplete,
        geometryEntityCount: geometry.totalEntities,
        geometryReadErrorCount: geometry.readErrorCount,
        geometryErrorSamples: geometry.geometryReadErrors,
        geometryTruncated: geometry.truncated,
        parameterCounts: {
          dimensions: extracted.dimensionCount,
          texts: extracted.textCount,
          circles: extracted.holeCount,
          mtextVisibleText: extracted.texts.filter((t) => t.type === 'MTEXT' && typeof t.text === 'string').length,
          mtextRawContent: extracted.texts.filter((t) => t.type === 'MTEXT' && typeof t.rawContents === 'string').length
        }
      }
    });
  }

  console.log(JSON.stringify({
    mcpToolCount: advertised.tools.length,
    bridge: { plugin: status.plugin, version: status.version, acadVersion: status.acadVersion, units: status.units },
    reports
  }, null, 2));
} finally {
  await client.close();
  await server.close();
}
