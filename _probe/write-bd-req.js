const fs = require('fs');
for (const [name, kind] of [['bd_sample1.jpg', 'bd-spf'], ['bd_sample2.jpg', 'bd-score']]) {
  const buf = fs.readFileSync('D:/AiFootBall/_probe/' + name);
  const body = JSON.stringify({ image: buf.toString('base64'), kind });
  fs.writeFileSync('D:/AiFootBall/_probe/' + name + '.req.json', body);
}
console.log('ok');
