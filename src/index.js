import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { pathToFileURL } from 'node:url';
import { requestAutoCad } from './pipe-client.js';

export function createServer(bridge = requestAutoCad) {
  const server = new McpServer({ name: 'autocad-2025-local', version: '0.5.0' });
  const readOnly = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
  const edit = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };
  const point = z.object({ x: z.number().finite(), y: z.number().finite(), z: z.number().finite().optional() });
  const layer = z.string().min(1).max(255).optional();
  const planOperation = z.union([
    z.object({ type: z.literal('line'), start: point, end: point, layer }),
    z.object({ type: z.literal('polyline'), points: z.array(z.object({ x: z.number().finite(), y: z.number().finite() })).min(2).max(500), closed: z.boolean().optional(), bulges: z.array(z.number().finite().min(-1000).max(1000)).max(500).optional(), layer }),
    z.object({ type: z.literal('circle'), center: point, radius: z.number().finite().positive(), layer }),
    z.object({ type: z.literal('rectangle'), x1: z.number().finite(), y1: z.number().finite(), x2: z.number().finite(), y2: z.number().finite(), layer }),
    z.object({ type: z.literal('text'), position: point, text: z.string().min(1).max(2048), height: z.number().finite().positive(), rotation: z.number().finite().optional(), layer }),
    z.object({ type: z.literal('dimension'), start: point, end: point, dimensionLinePoint: point, layer }),
    z.object({ type: z.literal('create_layer'), name: z.string().min(1).max(255), colorIndex: z.number().int().min(1).max(255).optional() })
  ]).superRefine((value, ctx) => {
    if (value.type !== 'polyline') return;
    if (value.bulges && value.bulges.length !== value.points.length) ctx.addIssue({ code: 'custom', path: ['bulges'], message: 'bulges must have one value per point' });
    if (value.closed && value.points.length < 3) ctx.addIssue({ code: 'custom', path: ['points'], message: 'closed polylines require at least 3 points' });
  });

  function register(name, description, schema, annotation = readOnly) {
    server.tool(name, description, schema, async (args) => {
      try {
        const data = await bridge(name, args);
        return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
      } catch (error) {
        return { isError: true, content: [{ type: 'text', text: error.message }] };
      }
    }, { annotations: annotation });
  }

  register('get_active_drawing', 'Read the active AutoCAD drawing name/path and current space state.', {});
  register('get_bridge_status', 'Check the loaded AutoCAD plug-in version, named pipe, active drawing, read-only state and units.', {});
  register('open_drawing', 'Open an existing DWG in AutoCAD through the bridge. Set readOnly to true to prevent modifications.', { path: z.string().min(1), readOnly: z.boolean().optional() });
  register('create_drawing_copy', 'Create a full DWG copy of the active drawing without renaming or modifying the source drawing.', { path: z.string().min(1) }, edit);
  register('save_active_drawing', 'Save changes in the active drawing to its existing path.', {} , edit);
  register('list_layers', 'List drawing layers and off/frozen/locked state.', {});
  register('count_modelspace_entities', 'Count model-space entities by DXF type.', { limit: z.number().int().min(1).max(20000).optional() });
  register('list_modelspace_entities', 'List model-space entity handles, DXF types and layers. Use the returned handle for targeted edits.', { limit: z.number().int().min(1).max(1000).optional() });
  register('inspect_drawing_geometry', 'Read model-space geometry and native text/dimension entities. Returns total entity counts and error samples independently of the returned-page limit; splines include control and fit points by default.', { limit: z.number().int().min(1).max(20000).optional(), includeSplines: z.boolean().optional() });
  register('audit_drawing_read', 'Run a deterministic ModelSpace inventory audit and report enumerated counts, unreadable entities, and unclassified entity types. Inventory coverage is not proof of semantic or design correctness.', {});
  register('draw_line', 'Draw a line in model space using start/end XYZ coordinates in drawing units.', { start: point, end: point }, edit);
  register('draw_polyline', 'Draw a 2D lightweight polyline with optional bulge arcs. Points are XY drawing units; each bulge controls the outgoing segment.', { points: z.array(z.object({ x: z.number().finite(), y: z.number().finite() })).min(2).max(500), closed: z.boolean().optional(), bulges: z.array(z.number().finite().min(-1000).max(1000)).max(500).optional() }, edit);
  register('draw_circle', 'Draw a circle in model space using center XYZ coordinates and positive radius in drawing units.', { center: point, radius: z.number().finite().positive() }, edit);
  register('draw_rectangle', 'Draw a closed rectangular polyline in model space using opposite XY corners in drawing units.', { x1: z.number().finite(), y1: z.number().finite(), x2: z.number().finite(), y2: z.number().finite() }, edit);
  register('move_entity', 'Move one entity by handle by the given XYZ offset in drawing units.', { handle: z.string().regex(/^[0-9a-f]+$/i), dx: z.number().finite(), dy: z.number().finite(), dz: z.number().finite().optional() }, edit);
  register('set_entity_layer', 'Change a model-space entity to an existing drawing layer.', { handle: z.string().regex(/^[0-9a-f]+$/i), layer: z.string().min(1) }, edit);
  register('delete_entity', 'Delete exactly one entity identified by its AutoCAD hexadecimal handle.', { handle: z.string().regex(/^[0-9a-f]+$/i) }, edit);
  register('preview_drawing_plan', 'Validate and preview up to 100 geometry, text, dimension and layer operations without changing the drawing. Returns a planId for explicit commit.', { operations: z.array(planOperation).min(1).max(100) });
  register('apply_drawing_plan', 'Apply the exact operations of a previously previewed plan to the same active drawing in one transaction. Any error rolls the transaction back.', { planId: z.string().regex(/^[0-9a-f]{32}$/i) }, edit);
  register('list_blocks', 'List named, non-layout, non-anonymous local block definitions that can be inserted.', {});
  register('create_block', 'Create a named local block definition from 1 to 50 line, circle, rectangle and/or text entities; rejects duplicate names.', { name: z.string().min(1).max(255), entities: z.array(z.discriminatedUnion('type', [
    z.object({ type: z.literal('line'), start: point, end: point }),
    z.object({ type: z.literal('circle'), center: point, radius: z.number().finite().positive() }),
    z.object({ type: z.literal('rectangle'), x1: z.number().finite(), y1: z.number().finite(), x2: z.number().finite(), y2: z.number().finite() }),
    z.object({ type: z.literal('text'), position: point, text: z.string().min(1).max(2048), height: z.number().finite().positive() })
  ])).min(1).max(50) }, edit);
  register('insert_block', 'Insert an existing local block definition into model space with uniform scale and rotation in degrees.', { name: z.string().min(1), position: point, scale: z.number().finite().positive().optional(), rotation: z.number().finite().optional() }, edit);

  // Native escape hatch: the plug-in owns a self-describing operation registry,
  // so new AutoCAD capabilities do not require a client-side schema change.
  register('list_tools', 'List every native AutoCAD operation the plug-in exposes (name, JSON params, mutating flag). Call this first when you need a capability that has no dedicated tool.', {});
  server.tool('cad_call', 'Call any native AutoCAD operation by name with a JSON argument object. Discover names with list_tools.', { tool: z.string().min(1).max(64), args: z.record(z.string(), z.any()).optional() }, async ({ tool, args }) => {
    try {
      const data = await bridge(tool, args ?? {});
      return { content: [{ type: 'text', text: JSON.stringify(data, null, 2) }] };
    } catch (error) {
      return { isError: true, content: [{ type: 'text', text: error.message }] };
    }
  }, { annotations: edit });
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const server = createServer();
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error('AutoCAD 2025 MCP server ready on stdio. The AutoCAD bundle starts its pipe listener on load; ACMCPSTART can restart it manually.');
}

