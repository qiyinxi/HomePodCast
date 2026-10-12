// A stand-in for HomePodCast's local API (src/Net/LocalApi.cs): GET http://127.0.0.1:<port>/v1/status on loopback,
// no CORS headers, 403 unless the Host header names 127.0.0.1:<port> or localhost:<port>, 404 for anything else.
// The status is whatever the test sets; every request is recorded. The extension uses no other endpoint: its
// settings (site offsets, switches) stay in chrome.storage.sync and are never sent to the app.
import http from 'node:http';

export const DEFAULT_STATUS = Object.freeze({
  app: 'HomePodCast',
  version: '1.0.0',
  streaming: true,
  device: 'E2E HomePod',
  latencyMs: 120,
  videoDelayMs: 141,
  videoDelaySource: 'estimate',
});

/**
 * Starts the fake API. port: the port to listen on (0 = any free one); throws EADDRINUSE when it is taken.
 * Returns { port, url, requests, setStatus(patch), setDown(bool), close() }.
 */
export function startFakeApp({ port = 47100 } = {}) {
  let status = { ...DEFAULT_STATUS };
  let down = false;
  const requests = [];
  const server = http.createServer((req, res) => {
    const actual = server.address().port;
    const host = req.headers.host;
    requests.push({
      at: Date.now(), method: req.method, path: req.url, host,
      origin: req.headers.origin || null, secFetchSite: req.headers['sec-fetch-site'] || null,
    });
    if (down) { // the app isn't running: the connection fails like a refused one
      req.socket.destroy();
      return;
    }
    let code = 200;
    if (host !== `127.0.0.1:${actual}` && String(host).toLowerCase() !== `localhost:${actual}`) code = 403;
    else if (req.method !== 'GET' || req.url.split('?')[0] !== '/v1/status') code = 404;
    const body = code === 200 ? JSON.stringify(status) : JSON.stringify({ error: code === 403 ? 'forbidden host' : 'not found' });
    res.writeHead(code, {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      'Content-Length': Buffer.byteLength(body),
      Connection: 'close',
    });
    res.end(body);
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => {
      server.off('error', reject);
      const actual = server.address().port;
      resolve({
        port: actual,
        url: `http://127.0.0.1:${actual}/v1/status`,
        requests,
        get status() { return { ...status }; },
        setStatus(patch) { status = { ...status, ...patch }; },
        resetStatus() { status = { ...DEFAULT_STATUS }; down = false; },
        setDown(value) { down = !!value; },
        close: () => new Promise((done) => { server.closeAllConnections?.(); server.close(() => done()); }),
      });
    });
  });
}

// Run on its own: node fake-app.mjs [port] [videoDelayMs] — for trying the extension by hand.
if (process.argv[1] && process.argv[1].endsWith('fake-app.mjs')) {
  const app = await startFakeApp({ port: Number(process.argv[2] || 47100) });
  if (process.argv[3]) app.setStatus({ videoDelayMs: Number(process.argv[3]) });
  console.log(`fake HomePodCast API on ${app.url}: ${JSON.stringify(app.status)}`);
}
