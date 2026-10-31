const els = {};
function mk(sel){
  const kids={};
  return {
    _html:'', _text:'',
    get innerHTML(){return this._html}, set innerHTML(v){this._html=v},
    get textContent(){return this._text}, set textContent(v){this._text=v},
    querySelector:(s)=>kids[s]||(kids[s]=mk(s)),
    querySelectorAll:()=>[],
    classList:{toggle(){},add(){}},
    dataset:{code:'dlt'},
    onclick:null, onkeydown:null, value:'',
    insertAdjacentHTML(){}
  };
}
global.document = {
  querySelector:s=>els[s]||(els[s]=mk(s)),
  querySelectorAll:s=>{
    if(s==='.tab') return ['dlt','ssq'].map(c=>{const e=mk('tab');e.dataset={code:c};return e;});
    return [];
  },
  addEventListener(){}
};
global.setInterval=()=>0;
global.window = global;
const _f = global.fetch;
global.fetch = (u,o)=> _f(u.startsWith('/') ? 'http://localhost:5088'+u : u, o);
const src = await (await import('node:fs/promises')).readFile('ui.js','utf8');
try{
  await new Function(src + "\n;return init();")();
  await new Promise(r=>setTimeout(r,2500));
  console.log('kpis', els['#kpis'].innerHTML.length);
  console.log('coverage', els['#oddsCoverage'].innerHTML.length, '|', els['#oddsCoverage'].innerHTML.slice(0,120).replace(/\s+/g,' '));
  console.log('signal rows', els['#signalTable'].querySelector('tbody')._html.length);
  console.log('signalAh', els['#signalAh'].innerHTML.slice(0,120));
  console.log('jobTable', els['#jobTable'].querySelector('tbody')._html.length);
  console.log('league', els['#leagueTable'].querySelector('tbody')._html.length);
  await els['#oddsFid'] , null;
}catch(e){ console.log('RUNTIME ERROR:', e.message); }
