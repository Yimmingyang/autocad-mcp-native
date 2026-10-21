import net from 'node:net';
import { randomUUID } from 'node:crypto';

const DEFAULT_PIPE = String.raw`\\.\pipe\AutoCAD_MCP_2025_V2`;

export function requestAutoCad(tool, args = {}, options = {}) {
  const pipePath = options.pipePath ?? process.env.AUTOCAD_MCP_PIPE ?? DEFAULT_PIPE;
  const timeoutMs = options.timeoutMs ?? 120000;
  const retryDelayMs = options.retryDelayMs ?? 100;
  const maxResponseBytes = options.maxResponseBytes ?? 1_048_576;
  const id = randomUUID();
  const deadline = Date.now() + timeoutMs;

  return new Promise((resolve, reject) => {
    let settled = false;
    let activeSocket;
    let retryTimer;
    let requestSent = false;
    const timer = setTimeout(() => {
      const status = requestSent
        ? 'AutoCAD did not reply before timeout; the operation may have completed. Inspect the drawing before retrying.'
        : `AutoCAD bridge was not reached before timeout at ${pipePath}.`;
      finish(new Error(`${status} Timeout: ${timeoutMs} ms.`));
    }, timeoutMs);

    function finish(error, value) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      clearTimeout(retryTimer);
      activeSocket?.destroy();
      if (error) reject(error);
      else resolve(value);
    }

    function connect() {
      if (settled) return;
      const socket = net.createConnection(pipePath);
      activeSocket = socket;
      let buffer = '';
      socket.once('connect', () => {
        requestSent = true;
        socket.write(`${JSON.stringify({ id, tool, args })}\n`, (error) => {
          if (error) finish(new Error(`AutoCAD bridge connection failed after request dispatch; operation state is unknown. Inspect the drawing before retrying. ${error.message}`));
        });
      });
      socket.on('data', (chunk) => {
        buffer += chunk.toString('utf8');
        const newline = buffer.indexOf('\n');
        if ((newline < 0 && Buffer.byteLength(buffer, 'utf8') > maxResponseBytes)
          || newline > maxResponseBytes)
          return finish(new Error(`AutoCAD bridge response exceeded ${maxResponseBytes} bytes`));
        if (newline < 0) return;
        let response;
        try { response = JSON.parse(buffer.slice(0, newline)); }
        catch { return finish(new Error('AutoCAD bridge returned invalid JSON')); }
        if (response.id !== id) return finish(new Error('AutoCAD bridge response ID mismatch'));
        if (!response.ok) return finish(new Error(response.error ?? 'AutoCAD bridge request failed'));
        finish(null, response.data);
      });
      socket.once('error', (error) => {
        socket.destroy();
        if (!requestSent && Date.now() + retryDelayMs < deadline) {
          retryTimer = setTimeout(connect, retryDelayMs);
        } else {
          const status = requestSent
            ? 'AutoCAD connection failed after request dispatch; operation state is unknown. Inspect the drawing before retrying.'
            : `AutoCAD bridge unavailable at ${pipePath}`;
          finish(new Error(`${status}: ${error.message}`));
        }
      });
      socket.once('end', () => {
        if (!settled) finish(new Error(requestSent
          ? 'AutoCAD closed the bridge before replying; operation state is unknown. Inspect the drawing before retrying.'
          : 'AutoCAD closed the bridge before the request was sent.'));
      });
      socket.once('close', () => {
        if (!settled && requestSent)
          finish(new Error('AutoCAD closed the bridge before replying; operation state is unknown. Inspect the drawing before retrying.'));
      });
    }

    connect();
  });
}

export { DEFAULT_PIPE };
