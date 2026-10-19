import * as Moq from '@moq/net';

import { moqLiteReport } from './moq_lite_report.mjs';

const url = new URL(process.argv[2] ?? 'http://relay:8000/live/moq');
const seconds = Number(process.argv[3] ?? 8);

const finish = (report, code) => {
  process.stdout.write(JSON.stringify(report) + '\n', () => process.exit(code));
};

setTimeout(() => finish({ url: url.toString(), error: 'timed out' }, 2), (seconds + 30) * 1000).unref();

try {
  const report = await moqLiteReport(Moq, { url, seconds });

  finish(report, report.error ? 1 : 0);
} catch (error) {
  finish({ url: url.toString(), error: String(error && error.stack ? error.stack : error) }, 1);
}
