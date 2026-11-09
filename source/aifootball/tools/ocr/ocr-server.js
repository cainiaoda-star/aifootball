/**
 * OCR 服务：接收购买截图，返回识别文本 + 解析后的结构化投注数据。
 * 依赖 tesseract.js，中文语言包已本地化（chi_sim.traineddata）。
 * 监听 127.0.0.1:5089，仅本机 .NET 后端调用。
 *
 * 2026-11-04：App「自购方案详情」类截图是四列表格，PSM6 单遍纯文本流会把行列搅乱、
 * 赔率倒不进格。现跑三遍（PSM 4/6/11）拿词级 bbox：PSM6 文本仍喂给老解析器，
 * 三遍词框合并后走表格重建（table-reconstruct），认出表头就用重建结果覆盖。
 */
const http = require('http');
const { createWorker } = require('tesseract.js');
const path = require('path');
const { parseTicket } = require('./parse-ticket.js');
const { reconstructAppTable, extractTeamRows, mergeWordLists, collectWords } = require('./table-reconstruct.js');

const PORT = 5089;
const CORE_PATH = path.join(__dirname, 'node_modules', 'tesseract.js-core');
const PSMS = ['6', '4', '11'];

let worker = null;
// 请求排队：识别是串行单工，忙时不再直接 429，让请求等着（用户上传与我联调实测不再互相踢）
let queue = Promise.resolve();

async function getWorker() {
  if (worker) return worker;
  // 仅用本地 chi_sim（无 eng 包时 tesseract 会异步拉 gz 并拖垮进程）
  worker = await createWorker('chi_sim', 1, {
    langPath: __dirname,
    corePath: CORE_PATH,
    logger: () => {}
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
        const task = queue.then(async () => {
          const w = await getWorker();

          // 多 PSM 各跑一遍：PSM6 兜老解析器，4/11 补孤字（总进球的 4/2、低对比小字）
          const wordLists = [];
          let text = '';
          for (const psm of PSMS) {
            await w.setParameters({ tessedit_pageseg_mode: psm, preserve_interword_spaces: '1' });
            const { data: rec } = await w.recognize(imgBuf, {}, { text: true, blocks: true });
            wordLists.push(collectWords(rec));
            if (psm === '6') text = rec.text || '';
          }
          return { text, wordLists };
        });
        queue = task.catch(() => {});
        const { text, wordLists } = await task;

        const allowed = new Set([
          'auto', 'physical', 'app',
          'jc-score', 'jc-spf', 'bd-score', 'bd-spf',
          'jc-score-app', 'jc-spf-app', 'bd-score-app', 'bd-spf-app'
        ]);
        const kind = allowed.has(data.kind) ? data.kind : 'auto';
        const layout = data.layout === 'physical' || data.layout === 'app' ? data.layout : undefined;
        const parsed = parseTicket(text, { kind, layout });

        // 表格重建：认出四列表头（场次/对阵/玩法/投注内容）就用它覆盖，赔率按单元格配对；
        // 重建失败或队名缺失时，用 VS 锚点兜底——保底带出场次+对阵，选项留手填。
        try {
          const merged = mergeWordLists(wordLists);
          const rebuilt = reconstructAppTable(merged);
          if (rebuilt && rebuilt.length) {
            const fallback = extractTeamRows(merged);
            if (fallback) {
              const byNo = new Map(fallback.filter(r => r.matchNo).map(r => [r.matchNo, r]));
              for (const m of rebuilt) {
                if (!m.home || !m.away) {
                  const fr = byNo.get(m.matchNo);
                  if (fr) { m.home = m.home || fr.home; m.away = m.away || fr.away; }
                }
              }
            }
            parsed.matches = rebuilt;
            parsed.tableMode = true;
          } else if (!parsed.matches || !parsed.matches.length) {
            const fallback = extractTeamRows(merged);
            if (fallback) { parsed.matches = fallback; parsed.teamsOnly = true; }
          }
        } catch (e) { /* 重建失败退回纯文本流结果 */ }

        sendJson(res, 200, { text, parsed });
      } catch (e) {
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
