const { createWorker } = require('tesseract.js');

(async () => {
  const worker = await createWorker('chi_sim', 1, {
    logger: m => { if (m.status === 'recognizing text') console.error('progress', m.progress); }
  });
  const { data: { text } } = await worker.recognize('test.png');
  console.log('===== OCR 结果 =====');
  console.log(text);
  await worker.terminate();
})().catch(e => { console.error('ERROR:', e.message); process.exit(1); });
