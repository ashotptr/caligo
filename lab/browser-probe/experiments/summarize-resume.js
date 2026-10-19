'use strict';
const fs = require('fs');
const data = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
console.log('burst bytes: ' + data.burstBytes + ' (' + (data.burstBytes / 16000).toFixed(2) + ' s at 128 kbps)');
const t1 = Math.min(...data.records.map(r => r.t));
for (const c of [...new Set(data.records.map(r => r.case))]) {
  console.log('\n=== ' + c + ' ===');
  data.records.filter(r => r.case === c).forEach((r, i) => {
    console.log(' conn#' + i + ' at page~' + (r.t - t1) + 'ms  Range=' + r.range + '  -> ' + r.status + '  sent=' + r.sent + '  closedAt(page~)=' + (r.closedAt === null ? 'open' : (r.closedAt - t1)));
  });
  const rep = data.reports && data.reports[c];
  if (!rep) continue;
  for (const e of rep) {
    console.log('   [' + String(e.t).padStart(5) + 'ms] ' + e.ev.padEnd(15) + ' dur=' + e.dur + ' rs=' + e.rs + ' ns=' + e.ns + ' ct=' + e.ct + ' paused=' + e.paused + ' ended=' + e.ended + ' err=' + e.err + ' buffered=' + JSON.stringify(e.buffered) + ' seekable=' + JSON.stringify(e.seekable) + (e.seen ? ' seen=' + JSON.stringify(e.seen) : ''));
  }
}
