import * as Moq from '@moq/net';

import { moqLiteReport } from './moq_lite_report.mjs';

const query = new URLSearchParams(location.search);
const url = new URL(query.get('relay'));
const seconds = Number(query.get('seconds') ?? 10);
const status = document.getElementById('status');

const send = async report => {
  report.userAgent = navigator.userAgent;
  report.webTransport = typeof WebTransport !== 'undefined';
  status.textContent = report.error ? 'failed: ' + report.error : 'done';

  await fetch('/report', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(report) });
};

status.textContent = 'connecting over WebTransport to ' + url;

try {
  await send(await moqLiteReport(Moq, { url, seconds, options: { websocket: { enabled: false } } }));
} catch (error) {
  await send({ url: url.toString(), error: String(error && error.stack ? error.stack : error) });
}
