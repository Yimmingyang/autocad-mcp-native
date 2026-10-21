import net from 'node:net';
import { once } from 'node:events';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { requestAutoCad } from '../src/pipe-client.js';

function fakePipe(responseFactory, pipePath) {
  const server = net.createServer((socket) => {
    let input = '';
    socket.on('data', (chunk) => {
      input += chunk.toString();
      const newline = input.indexOf('\n');
      if (newline < 0) return;
      const request = JSON.parse(input.slice(0, newline));
      socket.end(`${JSON.stringify(responseFactory(request))}\n`);
    });
  });
  server.listen(pipePath);
  return server;
}

test('bridge sends bounded JSON-line request and returns matching AutoCAD result', async (t) => {
  const pipePath = String.raw`\\.\pipe\acmcp-test-${process.pid}-${Date.now()}`;
  const server = fakePipe((request) => ({ id: request.id, ok: true, data: { tool: request.tool, args: request.args } }), pipePath);
  await once(server, 'listening');
  t.after(() => server.close());
  const result = await requestAutoCad('count_modelspace_entities', { limit: 123 }, { pipePath, timeoutMs: 1500 });
  assert.deepEqual(result, { tool: 'count_modelspace_entities', args: { limit: 123 } });
});

test('bridge rejects a mismatched response ID', async (t) => {
  const pipePath = String.raw`\\.\pipe\acmcp-bad-${process.pid}-${Date.now()}`;
  const server = fakePipe(() => ({ id: 'wrong', ok: true, data: {} }), pipePath);
  await once(server, 'listening');
  t.after(() => server.close());
  await assert.rejects(requestAutoCad('list_layers', {}, { pipePath, timeoutMs: 1500 }), /ID mismatch/);
});

test('bridge does not replay a request after it has reached AutoCAD', async (t) => {
  const pipePath = String.raw`\\.\pipe\acmcp-no-replay-${process.pid}-${Date.now()}`;
  let requestCount = 0;
  const server = net.createServer((socket) => {
    socket.once('data', () => {
      requestCount++;
      socket.destroy(); // Simulate AutoCAD finishing/receiving the command but losing its reply.
    });
  });
  server.listen(pipePath);
  await once(server, 'listening');
  t.after(() => server.close());

  await assert.rejects(
    requestAutoCad('draw_line', { start: { x: 0, y: 0 }, end: { x: 1, y: 1 } }, { pipePath, timeoutMs: 1000, retryDelayMs: 10 }),
    /operation state is unknown.*Inspect the drawing before retrying/i
  );
  assert.equal(requestCount, 1, 'a mutation must never be blindly resent after dispatch');
});

test('bridge timeout remains live and identifies uncertain dispatched operations', async (t) => {
  const pipePath = String.raw`\\.\pipe\acmcp-timeout-${process.pid}-${Date.now()}`;
  const server = net.createServer((socket) => socket.on('data', () => {}));
  server.listen(pipePath);
  await once(server, 'listening');
  t.after(() => server.close());

  await assert.rejects(
    requestAutoCad('apply_drawing_plan', { planId: 'deadbeef' }, { pipePath, timeoutMs: 100, retryDelayMs: 10 }),
    /may have completed.*Inspect the drawing before retrying/i
  );
});
