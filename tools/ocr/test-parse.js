const { createWorker } = require('tesseract.js');
const fs = require('fs');
const path = require('path');

// 复用 ocr-server.js 里的 parseTicket 逻辑做本地验证
const parseTicket = require('./parse-ticket.js');

(async () => {
  const worker = await createWorker('chi_sim', 1, {
    langPath: __dirname,
    corePath: path.join(__dirname, 'node_modules', 'tesseract.js-core'),
    logger: () => {}
  });
  const img = fs.readFileSync('test.png');
  const { data: { text } } = await worker.recognize(img);
  console.log('===== OCR 文本 =====');
  console.log(text);
  console.log('===== 解析结果 =====');
  console.log(JSON.stringify(parseTicket(text), null, 2));
  await worker.terminate();
})().catch(e => { console.error('ERROR:', e.message); process.exit(1); });
