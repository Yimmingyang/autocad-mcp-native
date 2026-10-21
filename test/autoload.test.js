import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('AutoCAD extension starts the MCP pipe listener when loaded', async () => {
  const entry = await readFile(new URL('../plugin/Entry.cs', import.meta.url), 'utf8');
  assert.match(entry, /public void Initialize\(\)/);
  assert.match(entry, /Application.Idle \+= StartOnIdle;/);
  assert.match(entry, /PipeName = "AutoCAD_MCP_2025_V2"/);
  assert.match(entry, /#elif CAD2027\s+private const string PipeName = "AutoCAD_MCP_2027_V2";/);
  assert.match(entry, /Add\("get_bridge_status"/);
  const client = await readFile(new URL('../src/pipe-client.js', import.meta.url), 'utf8');
  assert.match(client, /\\\\\.\\pipe\\AutoCAD_MCP_2025_V2/);
});

test('AutoCAD 2025 bundle is configured for startup autoload', async () => {
  const manifest = await readFile(new URL('../release/AutoCadMcp.bundle/PackageContents.xml', import.meta.url), 'utf8');
  assert.match(manifest, /<Components>\s*<RuntimeRequirements OS="Win64" Platform="AutoCAD\*" SeriesMin="R25\.0"\s*\/>/);
  assert.match(manifest, /AppType="\.Net" LoadOnAutoCADStartup="True"/);
  assert.match(manifest, /ModuleName="\.\/Contents\/Windows\/AutoCadMcpPluginV2\.dll"/);
  for (const suffix of ['.deps.json', '.runtimeconfig.json']) {
    await readFile(new URL(`../release/AutoCadMcp.bundle/Contents/Windows/AutoCadMcpPluginV2${suffix}`, import.meta.url));
  }
});

