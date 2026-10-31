/**
 * OCR 服务：接收购买截图，返回识别文本 + 解析后的结构化投注数据。
 * 依赖 tesseract.js，中文语言包已本地化（chi_sim.traineddata）。
 * 监听 127.0.0.1:5089，仅本机 .NET 后端调用。
 */
const http = require('http');
const { createWorker } = require('tesseract.js');
const path = require('path');
const { parseTicket } = require('./parse-ticket.js');

const PORT = 5089;
const CORE_PATH = path.join(__dirname, 'node_modules', 'tesseract.js-core');

let worker = null;
let busy = false;

async function getWorker() {
  if (worker) return worker;
  // 仅用本地 chi_sim（无 eng 包时 tesseract 会异步拉 gz 并拖垮进程）
  worker = await createWorker('chi_sim', 1, {
    langPath: __dirname,
    corePath: CORE_PATH,
    logger: () => {}
  });
  // 假定统一文本块，适合购彩截图
  await worker.setParameters({
    tessedit_pageseg_mode: '6',
    preserve_interword_spaces: '1'
  });
  return worker;
}

function sendJson(res, code, obj) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(obj));
}

const server = http.createServer(async (req, res) => {
  if (req.method === 'POST' && req.url === '/ocr') {
    let body = '';
    req.on('data', c => { body += c; if (body.length > 20 * 1024 * 1024) req.destroy(); });
    req.on('end', async () => {
      try {
        const data = JSON.parse(body);
        const imgBuf = Buffer.from(data.image, 'base64');
        if (busy) { sendJson(res, 429, { error: 'busy' }); return; }
        busy = true;
        const w = await getWorker();
        const { data: rec } = await w.recognize(imgBuf);
        busy = false;
        const text = rec.text || '';
        const allowed = new Set([
          'auto', 'physical', 'app',
          'jc-score', 'jc-spf', 'bd-score', 'bd-spf',
          'jc-score-app', 'jc-spf-app', 'bd-score-app', 'bd-spf-app'
        ]);
        const kind = allowed.has(data.kind) ? data.kind : 'auto';
        const layout = data.layout === 'physical' || data.layout === 'app' ? data.layout : undefined;
        const parsed = parseTicket(text, { kind, layout });
        sendJson(res, 200, { text, parsed });
      } catch (e) {
        busy = false;
        sendJson(res, 500, { error: e.message });
      }
    });
    return;
  }
  if (req.method === 'GET' && req.url === '/health') {
    sendJson(res, 200, { status: 'ok' });
    return;
  }
  sendJson(res, 404, { error: 'not found' });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`OCR server listening on http://127.0.0.1:${PORT}`);
});
