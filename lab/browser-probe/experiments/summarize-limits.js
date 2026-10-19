'use strict';
const fs = require('fs');
const data = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const mb = n => (n / 1048576).toFixed(2) + 'MB';

const browsers = [...new Set(data.records.map(r => r.b))];
for (const b of browsers) {
  console.log('\n================ ' + b + ' ================');
  const recs = data.records.filter(r => r.b === b);
  const cases = [...new Set(recs.map(r => r.case))];
  for (const c of cases) {
    console.log('\n--- case ' + c + ' ---');
    recs.filter(r => r.case === c).forEach((r, i) => {
      const q = r.req || {};
      console.log(' conn#' + i + ' @' + r.t + 'ms  ' + q.line + '  Range=' + q.range + '  IfRange=' + q.ifRange + '  AE=' + q.acceptEncoding + '  dest=' + q.secFetchDest + '  conn=' + q.connection + '  icy=' + q.icy);
      if (i === 0) console.log('   accept=' + q.accept + '  ua=' + q.ua);
      console.log('   -> ' + r.status + ' | ' + (r.respHeaders || []).join(' | '));
      console.log('   flushed: ' + r.progress.map(p => (p[0] / 1000) + 's:' + mb(p[1])).join('  ') + '  | total=' + mb(r.flushed) + ' closedAt=' + r.closedAt + (r.capReached ? ' CAP' : ''));
    });
    const rep = data.reports[b] && data.reports[b][c];
    if (rep) {
      for (const e of rep) {
        const extra = Object.keys(e).filter(k => !['t', 'ev', 'dur', 'rs', 'ns', 'ct', 'paused', 'err', 'buffered', 'seekable'].includes(k)).map(k => k + '=' + JSON.stringify(e[k])).join(' ');
        console.log('   [' + String(e.t).padStart(5) + 'ms] ' + e.ev.padEnd(15) + ' dur=' + e.dur + ' rs=' + e.rs + ' ns=' + e.ns + ' ct=' + e.ct + ' paused=' + e.paused + ' err=' + e.err + ' buffered=' + JSON.stringify(e.buffered) + ' seekable=' + JSON.stringify(e.seekable) + (extra ? ' ' + extra : ''));
      }
    }
  }
}
