'use strict';
const net = require('net');
const http = require('http');
const fs = require('fs');

const BASE = 18720;
const OUT = process.argv[2];
const CASES = ['chunkedAbrupt', 'chunkedClean', 'nolenClose', 'cl53Abrupt', 'cl19Abrupt', 'exactLength'];
const FRAME = Buffer.alloc(417);
FRAME[0] = 0xff; FRAME[1] = 0xfb; FRAME[2] = 0x90; FRAME[3] = 0x00;
const BODY = Buffer.concat(Array(320).fill(FRAME));
const records = [];
const t0 = Date.now();

const PAGE = `<!doctype html><meta charset="utf-8"><title>audio test 2</title>
<script>
const BASE = parseInt(location.port, 10);
const CASES = ${JSON.stringify(CASES)};
const t0 = performance.now();
const out = {};
function rng(r) { const a = []; for (let i = 0; i < r.length; i++) a.push([Number(r.start(i)).toFixed(2), String(Number(r.end(i)).toFixed(2))]); return a; }
CASES.forEach((c, i) => {
  const a = new Audio();
  a.preload = 'auto';
  const log = out[c] = [];
  const seen = {};
  const snap = (ev, extra) => log.push(Object.assign({
    t: Math.round(performance.now() - t0), ev, dur: String(a.duration), rs: a.readyState, ns: a.networkState,
    ct: Number(a.currentTime.toFixed(2)), paused: a.paused, ended: a.ended,
    err: a.error ? (a.error.code + ':' + a.error.message) : null,
    buffered: rng(a.buffered), seekable: rng(a.seekable)
  }, extra || {}));
  ['loadedmetadata', 'durationchange', 'playing', 'error', 'stalled', 'waiting', 'ended', 'abort', 'emptied', 'pause'].forEach(e => a.addEventListener(e, () => {
    seen[e] = (seen[e] || 0) + 1;
    if (seen[e] <= 3) snap(e);
  }));
  a.src = 'http://127.0.0.1:' + (BASE + 1 + i) + '/' + c;
  a.play().catch(e => snap('play-rejected', { msg: String(e) }));
  setTimeout(() => snap('t4s'), 4000);
  setTimeout(() => snap('t17s', { seen }), 17000);
});
setTimeout(() => fetch('/report', { method: 'POST', body: JSON.stringify(out) }).catch(() => {}), 17500);
</script>`;

CASES.forEach((kase, i) => {
  net.createServer(sock => {
    const rec = { case: kase, t: Date.now() - t0, range: null, status: null, closedAt: null };
    records.push(rec);
    let head = Buffer.alloc(0);
    let started = false;
    sock.on('error', () => {});
    sock.on('close', () => { rec.closedAt = Date.now() - t0; });
    sock.on('data', d => {
      if (started) return;
      head = Buffer.concat([head, d]);
      if (head.indexOf('\r\n\r\n') < 0) return;
      started = true;
      rec.range = (/\r\nRange: ([^\r]+)/i.exec(head.toString('latin1')) || [])[1] || null;
      const common = 'HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nCache-Control: no-cache, no-store\r\n';
      const chunk = b => Buffer.concat([Buffer.from(b.length.toString(16) + '\r\n'), b, Buffer.from('\r\n')]);
      let h, body = BODY, finish;
      switch (kase) {
        case 'chunkedAbrupt': h = common + 'Transfer-Encoding: chunked\r\n\r\n'; body = chunk(BODY); finish = () => sock.destroy(); break;
        case 'chunkedClean': h = common + 'Transfer-Encoding: chunked\r\n\r\n'; body = chunk(BODY); finish = () => sock.end('0\r\n\r\n'); break;
        case 'nolenClose': h = common + 'Connection: close\r\n\r\n'; finish = () => sock.end(); break;
        case 'cl53Abrupt': h = common + 'Content-Length: 9007199254740991\r\n\r\n'; finish = () => sock.destroy(); break;
        case 'cl19Abrupt': h = common + 'Content-Length: 9999999999999999999\r\n\r\n'; finish = () => sock.destroy(); break;
        case 'exactLength': h = common + 'Content-Length: ' + BODY.length + '\r\n\r\n'; finish = () => {}; break;
      }
      rec.status = h.trim().split('\r\n').filter(l => /^(HTTP|Content-Length|Transfer|Connection)/.test(l)).join(' | ');
      sock.write(h);
      sock.write(body);
      setTimeout(finish, 2500);
    });
  }).listen(BASE + 1 + i, '127.0.0.1');
});

function save(reports) {
  fs.writeFileSync(OUT, JSON.stringify({ bodyBytes: BODY.length, records, reports }, null, 1));
}

http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  if (u.pathname === '/t.html') {
    res.writeHead(200, { 'Content-Type': 'text/html' });
    res.end(PAGE);
  } else if (u.pathname === '/report' && req.method === 'POST') {
    let b = '';
    req.on('data', d => { b += d; });
    req.on('end', () => {
      res.end('ok');
      save(JSON.parse(b));
      setTimeout(() => process.exit(0), 500);
    });
  } else {
    res.writeHead(404);
    res.end();
  }
}).listen(BASE, '127.0.0.1', () => console.log('listening on', BASE));

setTimeout(() => { save(null); process.exit(0); }, 60000);
