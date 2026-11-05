/** 用真实截图测表格重建：node test-reconstruct.js <图片路径> [PSM列表] */
const fs = require('fs');
const path = require('path');
const { createWorker } = require('tesseract.js');
const { reconstructAppTable, mergeWordLists, collectWords } = require('./table-reconstruct.js');
const { parseTicket } = require('./parse-ticket.js');

(async () => {
  const img = process.argv[2];
  if (!img) { console.error('usage: node test-reconstruct.js <image>'); process.exit(1); }
  const psms = (process.argv[3] || '6,11').split(',');
  const workers = [];
  for (const psm of psms) {
    const worker = await createWorker('chi_sim', 1, {
      langPath: __dirname,
      corePath: path.join(__dirname, 'node_modules', 'tesseract.js-core'),
      logger: () => {}
    });
    await worker.setParameters({ tessedit_pageseg_mode: psm, preserve_interword_spaces: '1' });
    workers.push(worker);
  }

  const wordLists = [];
  let text6 = '';
  for (let i = 0; i < workers.length; i++) {
    const { data: rec } = await workers[i].recognize(fs.readFileSync(img), {}, { text: true, blocks: true });
    wordLists.push(collectWords(rec));
    if (i === 0) text6 = rec.text || '';
  }

  const parsed = parseTicket((text6 || '').replace(/\s+/g, ''), { kind: 'auto' });
  console.log('== parseTicket(纯文本流, 现状) ==');
  console.log(JSON.stringify(parsed.matches, null, 1));

  const merged = mergeWordLists(wordLists);
  const rebuilt = reconstructAppTable(merged);
  console.log(`== reconstructAppTable(双PSM词框合并, ${merged.length} 词) ==`);
  console.log(rebuilt ? JSON.stringify(rebuilt, null, 1) : 'null（未识别出表格）');
  process.exit(0);
})().catch(e => { console.error(e); process.exit(1); });
