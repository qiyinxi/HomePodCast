// Serves the test pages and the test video on loopback. The browser reaches it under made-up site names
// (youtube.localhost, bilibili.localhost, plain.localhost: Chromium resolves *.localhost to loopback itself and treats
// it as a secure context, so WebCodecs works as on https sites). The extension's content scripts match <all_urls>,
// so no real site's name is needed, and nothing ever goes to the real YouTube or bilibili.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PAGES = path.join(HERE, 'pages');
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.webm': 'video/webm' };

export function startSiteServer({ videoFile }) {
  const routes = new Map([
    ['/watch', path.join(PAGES, 'youtube.html')],
    ['/video/BV1e2e', path.join(PAGES, 'bilibili.html')],
    ['/article', path.join(PAGES, 'plain.html')],
    ['/media/test.webm', videoFile],
  ]);
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    let file = routes.get(url.pathname);
    if (!file && url.pathname.startsWith('/pages/')) {
      const p = path.normalize(path.join(PAGES, url.pathname.slice('/pages/'.length)));
      if (p.startsWith(PAGES + path.sep)) file = p;
    }
    if (!file || !fs.existsSync(file)) {
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      res.end('not found');
      return;
    }
    const size = fs.statSync(file).size;
    const headers = { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream', 'Accept-Ranges': 'bytes', 'Cache-Control': 'no-cache' };
    // Seeking in a <video> needs byte ranges.
    const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || '');
    if (range && (range[1] || range[2])) {
      let start = range[1] ? Number(range[1]) : size - Number(range[2]);
      let end = range[1] && range[2] ? Number(range[2]) : size - 1;
      start = Math.max(0, start);
      end = Math.min(end, size - 1);
      if (start > end) {
        res.writeHead(416, { 'Content-Range': `bytes */${size}` });
        res.end();
        return;
      }
      res.writeHead(206, { ...headers, 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
      if (req.method === 'HEAD') res.end();
      else fs.createReadStream(file, { start, end }).pipe(res);
      return;
    }
    res.writeHead(200, { ...headers, 'Content-Length': size });
    if (req.method === 'HEAD') res.end();
    else fs.createReadStream(file).pipe(res);
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      resolve({
        port,
        url: (site, pathname) => `http://${site}.localhost:${port}${pathname}`,
        close: () => new Promise((done) => { server.closeAllConnections?.(); server.close(() => done()); }),
      });
    });
  });
}
