'use strict';
const net = require('net');
const http = require('http');
const fs = require('fs');

const BASE = 18740;
const OUT = process.argv[2];
const CASES = ['retryLive200', 'retry206'];
const FRAME = Buffer.alloc(417);
FRAME[0] = 0xff; FRAME[1] = 0xfb; FRAME[2] = 0x90; FRAME[3] = 0x00;
const BURST = Buffer.concat(Array(384).fill(FRAME));
const SMALL = Buffer.concat(Array(10).fill(FRAME));
const records = [];
const t0 = Date.now();
const chunk = b => Buffer.concat([Buffer.from(b.length.toString(16) + '\r\n'), b, Buffer.from('\r\n')]);

const PAGE = `<!doctype html><meta charset="utf-8"><title>audio test 3</title>
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
    if (seen[e] <= 4) snap(e);
  }));
  a.src = 'http://127.0.0.1:' + (BASE + 1 + i) + '/' + c;
  a.play().catch(e => snap('play-rejected', { msg: String(e) }));
  [4000, 8000, 12000, 16000, 20000].forEach(ms => setTimeout(() => snap('t' + (ms / 1000) + 's'), ms));
  setTimeout(() => snap('t25s', { seen }), 25000);
});
setTimeout(() => fetch('/report', { method: 'POST', body: JSON.stringify(out) }).catch(() => {}), 25500);
</script>`;

CASES.forEach((kase, i) => {
  net.createServer(sock => {
    const rec = { case: kase, t: Date.now() - t0, range: null, status: null, sent: 0, closedAt: null };
    records.push(rec);
    let head = Buffer.alloc(0);
    let started = false;
    let pacer = null;
    sock.on('error', () => {});
    sock.on('close', () => { rec.closedAt = Date.now() - t0; clearInterval(pacer); });
    sock.on('data', d => {
      if (started) return;
      head = Buffer.concat([head, d]);
      if (head.indexOf('\r\n\r\n') < 0) return;
      started = true;
      rec.range = (/\r\nRange: ([^\r]+)/i.exec(head.toString('latin1')) || [])[1] || null;
      const m = /^bytes=(\d+)-/.exec(rec.range || '');
      const offset = m ? Number(m[1]) : 0;
      const common = 'Content-Type: audio/mpeg\r\nCache-Control: no-cache, no-store\r\nTransfer-Encoding: chunked\r\n';
      if (offset === 0) {
        rec.status = '200 chunked, burst then close';
        sock.write('HTTP/1.1 200 OK\r\n' + common + '\r\n');
        sock.write(chunk(BURST));
        rec.sent = BURST.length;
        setTimeout(() => sock.destroy(), 2000);
        return;
      }
      if (kase === 'retry206') {
        rec.status = '206 chunked paced, Content-Range: bytes ' + offset + '-9007199254740990/*';
        sock.write('HTTP/1.1 206 Partial Content\r\n' + common + 'Accept-Ranges: bytes\r\nContent-Range: bytes ' + offset + '-9007199254740990/*\r\n\r\n');
      } else {
        rec.status = '200 chunked paced (Range ignored)';
        sock.write('HTTP/1.1 200 OK\r\n' + common + '\r\n');
      }
      pacer = setInterval(() => {
        if (sock.destroyed) return;
        sock.write(chunk(SMALL), () => { rec.sent += SMALL.length; });
      }, 261);
    });
  }).listen(BASE + 1 + i, '127.0.0.1');
});

function save(reports) {
  fs.writeFileSync(OUT, JSON.stringify({ burstBytes: BURST.length, records, reports }, null, 1));
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

setTimeout(() => { save(null); process.exit(0); }, 70000);
