import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { createServer } from '../src/index.js';

test('MCP advertises polyline edits and validates bulges before bridging', async (t) => {
  const server = createServer(async (tool, args) => tool === 'get_active_drawing'
    ? { name: 'test.dwg', path: 'C:/mock/test.dwg', tileMode: true }
    : tool === 'get_bridge_status'
      ? { plugin: 'autocad-2025-local', version: '0.3.3', pipe: String.raw`\\.\pipe\AutoCAD_MCP_2025`, drawing: 'test.dwg', readOnly: false, units: 'Millimeters' }
    : { tool, args });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'autocad-mcp-test', version: '0.1.0' });
  await server.connect(serverTransport);
  await client.connect(clientTransport);
  t.after(async () => { await client.close(); await server.close(); });
  t.after(() => client.close());

  const { tools } = await client.listTools();
  assert.deepEqual(tools.map((tool) => tool.name).sort(), [
    'apply_drawing_plan', 'cad_call', 'count_modelspace_entities', 'create_block', 'create_drawing_copy', 'delete_entity', 'draw_circle', 'draw_line', 'draw_polyline', 'draw_rectangle', 'get_active_drawing', 'get_bridge_status', 'insert_block', 'inspect_drawing_geometry', 'list_blocks', 'list_layers', 'list_modelspace_entities', 'list_tools', 'move_entity', 'open_drawing', 'preview_drawing_plan', 'save_active_drawing', 'set_entity_layer'
  ]);
  const drawing = await client.callTool({ name: 'get_active_drawing', arguments: {} });
  assert.match(drawing.content[0].text, /test\.dwg/);
  const status = await client.callTool({ name: 'get_bridge_status', arguments: {} });
  assert.match(status.content[0].text, /0\.3\.3/);
  const layers = await client.callTool({ name: 'list_layers', arguments: {} });
  assert.match(layers.content[0].text, /list_layers/);
  const counts = await client.callTool({ name: 'count_modelspace_entities', arguments: { limit: 10 } });
  assert.match(counts.content[0].text, /count_modelspace_entities/);
  const planned = await client.callTool({ name: 'preview_drawing_plan', arguments: { operations: [{ type: 'polyline', points: [{ x: 0, y: 0 }, { x: 20, y: 0 }, { x: 20, y: 10 }], closed: true, bulges: [0, 0.41421356, 0] }] } });
  assert.match(planned.content[0].text, /preview_drawing_plan/);
  const invalid = await client.callTool({ name: 'preview_drawing_plan', arguments: { operations: [{ type: 'polyline', points: [{ x: 0, y: 0 }, { x: 20, y: 0 }, { x: 20, y: 10 }], bulges: [0] }] } });
  assert.equal(invalid.isError, true);
});

