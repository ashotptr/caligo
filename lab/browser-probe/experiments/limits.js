'use strict';
const net = require('net');
const http = require('http');
const fs = require('fs');

const BASE = 18700;
const OUT = process.argv[2];
const EXPECT = (process.argv[3] || 'edge,chrome').split(',');
const CASES = ['radio', 'fastlive', 'fastidle', 'cl10g', 'range10g', 'cl53', 'cl63', 'cl19', 'both19'];
const TEN_GIB = 10 * 1024 * 1024 * 1024;
const CAP = 2 * 1024 * 1024 * 1024;
const LIFETIME_MS = 110000;

const FRAME = Buffer.alloc(417);
FRAME[0] = 0xff; FRAME[1] = 0xfb; FRAME[2] = 0x90; FRAME[3] = 0x00;
const SMALL = Buffer.concat(Array(10).fill(FRAME));
const BIG = Buffer.concat(Array(160).fill(FRAME));

const records = [];
const reports = {};
const t0 = Date.now();

const PAGE = `<!doctype html><meta charset="utf-8"><title>audio test</title>
<script>
const B = new URLSearchParams(location.search).get('b') || 'x';
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
    ct: Number(a.currentTime.toFixed(2)), paused: a.paused,
    err: a.error ? (a.error.code + ':' + a.error.message) : null,
    buffered: rng(a.buffered), seekable: rng(a.seekable)
  }, extra || {}));
  ['loadstart', 'loadedmetadata', 'durationchange', 'canplay', 'canplaythrough', 'playing', 'error', 'stalled', 'suspend', 'waiting', 'ended', 'abort', 'emptied', 'seeking', 'seeked'].forEach(e => a.addEventListener(e, () => {
    seen[e] = (seen[e] || 0) + 1;
    if (seen[e] <= 2) snap(e);
  }));
  a.src = 'http://127.0.0.1:' + (BASE + 1 + i) + '/' + c + '?b=' + B;
  if (c !== 'fastidle') a.play().catch(e => snap('play-rejected', { msg: String(e) }));
  setTimeout(() => snap('t3s'), 3000);
  setTimeout(() => snap('t8s'), 8000);
  setTimeout(() => {
    if (isFinite(a.duration)) {
      const target = Math.min(a.duration / 2, 3600);
      snap('seek-request', { target });
      try { a.currentTime = target; } catch (e) { snap('seek-threw', { msg: String(e) }); }
    }
  }, 9000);
  setTimeout(() => snap('t15s', { seen }), 15000);
});
setTimeout(() => fetch('/report?b=' + B, { method: 'POST', body: JSON.stringify(out) }).catch(() => {}), 16000);
</script>`;

function save() {
  fs.writeFileSync(OUT, JSON.stringify({ records: records.map(r => Object.assign({}, r, { timer: undefined, pacer: undefined })), reports }, null, 1));
}

function respond(kase, sock, rec, h) {
  let chunked = false, paced = false, offset = 0, limit = Infinity, head;
  const common = 'Content-Type: audio/mpeg\r\nCache-Control: no-cache, no-store\r\nAccess-Control-Allow-Origin: *\r\n';
  const live = 'HTTP/1.1 200 OK\r\n' + common + 'Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n';
  const withLength = v => 'HTTP/1.1 200 OK\r\n' + common + 'Content-Length: ' + v + '\r\n\r\n';
  switch (kase) {
    case 'radio': chunked = true; paced = true; head = live; break;
    case 'fastlive':
    case 'fastidle': chunked = true; head = live; break;
    case 'cl10g': head = withLength(TEN_GIB); break;
    case 'range10g': {
      const m = /^bytes=(\d+)-(\d*)$/.exec(h['range'] || '');
      if (m) {
        offset = Number(m[1]);
        const end = m[2] ? Number(m[2]) : TEN_GIB - 1;
        limit = end - offset + 1;
        head = 'HTTP/1.1 206 Partial Content\r\n' + common + 'Accept-Ranges: bytes\r\nContent-Range: bytes ' + offset + '-' + end + '/' + TEN_GIB + '\r\nContent-Length: ' + limit + '\r\n\r\n';
      } else {
        limit = TEN_GIB;
        head = 'HTTP/1.1 200 OK\r\n' + common + 'Accept-Ranges: bytes\r\nContent-Length: ' + TEN_GIB + '\r\n\r\n';
      }
      break;
    }
    case 'cl53': head = withLength('9007199254740991'); break;
    case 'cl63': head = withLength('9223372036854775807'); break;
    case 'cl19': head = withLength('9999999999999999999'); break;
    case 'both19': chunked = true; head = 'HTTP/1.1 200 OK\r\n' + common + 'Content-Length: 9999999999999999999\r\nTransfer-Encoding: chunked\r\n\r\n'; break;
  }
  rec.status = head.split('\r\n')[0];
  rec.respHeaders = head.trim().split('\r\n').slice(1).filter(l => !/^(Content-Type|Cache-Control|Access-Control)/.test(l));
  sock.write(head);
  const start = Date.now();
  const marks = [500, 1000, 2000, 3000, 5000, 8000, 12000, 16000, 19000];
  rec.timer = setInterval(() => {
    const el = Date.now() - start;
    while (marks.length && el >= marks[0]) rec.progress.push([marks.shift(), rec.flushed]);
  }, 100);
  const send = buf => {
    const payload = chunked ? Buffer.concat([Buffer.from(buf.length.toString(16) + '\r\n'), buf, Buffer.from('\r\n')]) : buf;
    rec.queued += buf.length;
    return sock.write(payload, () => { rec.flushed += buf.length; });
  };
  if (paced) {
    rec.pacer = setInterval(() => { if (!sock.destroyed) send(SMALL); }, 261);
    return;
  }
  let first = offset % 417 ? FRAME.subarray(offset % 417) : null;
  const pump = () => {
    let n = 0;
    while (!sock.destroyed && rec.queued < Math.min(limit, CAP)) {
      const buf = first || BIG;
      first = null;
      if (!send(buf)) { sock.once('drain', pump); return; }
      if (++n >= 16) { setImmediate(pump); return; }
    }
    if (rec.queued >= CAP) rec.capReached = true;
  };
  pump();
}

CASES.forEach((kase, i) => {
  net.createServer(sock => {
    const rec = { case: kase, b: '?', t: Date.now() - t0, req: null, status: null, respHeaders: null, progress: [], flushed: 0, queued: 0, closedAt: null };
    records.push(rec);
    let head = Buffer.alloc(0);
    let started = false;
    sock.on('error', () => {});
    sock.on('close', () => { rec.closedAt = Date.now() - t0; clearInterval(rec.timer); clearInterval(rec.pacer); });
    sock.on('data', d => {
      if (started) return;
      head = Buffer.concat([head, d]);
      const idx = head.indexOf('\r\n\r\n');
      if (idx < 0) return;
      started = true;
      const lines = head.subarray(0, idx).toString('latin1').split('\r\n');
      const h = {};
      for (const l of lines.slice(1)) {
        const p = l.indexOf(':');
        if (p > 0) h[l.slice(0, p).toLowerCase()] = l.slice(p + 1).trim();
      }
      rec.req = {
        line: lines[0], range: h['range'] || null, accept: h['accept'] || null, acceptEncoding: h['accept-encoding'] || null,
        connection: h['connection'] || null, secFetchDest: h['sec-fetch-dest'] || null, icy: h['icy-metadata'] || null,
        ifRange: h['if-range'] || null, ua: (h['user-agent'] || '').slice(0, 140)
      };
      rec.b = (/[?&]b=([a-z]+)/.exec(lines[0]) || [])[1] || '?';
      respond(kase, sock, rec, h);
    });
  }).listen(BASE + 1 + i, '127.0.0.1');
});

http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  if (u.pathname === '/t.html') {
    res.writeHead(200, { 'Content-Type': 'text/html' });
    res.end(PAGE);
  } else if (u.pathname === '/report' && req.method === 'POST') {
    let b = '';
    req.on('data', d => { b += d; });
    req.on('end', () => {
      reports[u.searchParams.get('b')] = JSON.parse(b);
      res.end('ok');
      save();
      if (EXPECT.every(x => reports[x])) setTimeout(() => { save(); process.exit(0); }, 4500);
    });
  } else {
    res.writeHead(404);
    res.end();
  }
}).listen(BASE, '127.0.0.1', () => {
  console.log('listening on', BASE);
  const c = net.connect(BASE + 1 + CASES.indexOf('fastlive'), '127.0.0.1', () => {
    c.write('GET /fastlive?b=sink HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: sink\r\n\r\n');
  });
  c.pause();
  c.on('error', () => {});
});

setTimeout(() => { save(); process.exit(0); }, LIFETIME_MS);
