'use strict';
const fs = require('fs');
const data = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
console.log('body bytes per case: ' + data.bodyBytes + ' (' + (data.bodyBytes / 16000).toFixed(2) + ' s at 128 kbps)');
for (const r of data.records) {
  console.log('\n--- ' + r.case + ' ---  conn@' + r.t + 'ms Range=' + r.range + ' closedAt=' + r.closedAt + '  ' + r.status);
  const rep = data.reports && data.reports[r.case];
  if (!rep || r.done) continue;
  for (const e of rep) {
    console.log('   [' + String(e.t).padStart(5) + 'ms] ' + e.ev.padEnd(15) + ' dur=' + e.dur + ' rs=' + e.rs + ' ns=' + e.ns + ' ct=' + e.ct + ' paused=' + e.paused + ' ended=' + e.ended + ' err=' + e.err + ' buffered=' + JSON.stringify(e.buffered) + (e.seen ? ' seen=' + JSON.stringify(e.seen) : ''));
  }
}
