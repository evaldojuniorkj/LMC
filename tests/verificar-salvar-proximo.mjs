// API simulada: testa a sequência de gravação sem escrever no SQL Server.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {randomUUID} from 'node:crypto';
import vm from 'node:vm';
let checks=0;const check=(ok,label)=>{assert.ok(ok,label);checks++;console.log(`OK: ${label}`);};
const elements=new Map();
function element(id){if(!elements.has(id))elements.set(id,{value:'',hidden:false,disabled:false,textContent:'',innerHTML:'',validity:{badInput:false},listeners:{},
  setAttribute(){},checkValidity(){return true;},addEventListener(name,fn){this.listeners[name]=fn;}});return elements.get(id);}
const timers=new Map(),calls=[];let timer=0,status=200,heldSave=null,heldNext=null,failNext=false,canSave=true,alreadyApplied=false;
let nextDate='2026-10-01',lastClosing=9000;
const fixture={branch:17581,date:'2026-09-30',productId:2,product:'Diesel',version:'A'.repeat(64),bookId:null,number:31,
  tanks:[{tankId:1,capacity:30000,previousClosing:10000,inherited:true}],pumps:[{pumpId:1,tankId:1,opening:2000,closing:3000,calibration:0,sales:1000,price:7}],documents:[],blockers:[],warnings:[],followingBooks:[],history:[],observation:'',monthSalesBefore:0,monthValueBefore:0};
const response=(data,status=200)=>({ok:status<400,status,text:async()=>JSON.stringify(data)});
const ctx=vm.createContext({console,Intl,URLSearchParams,crypto:{randomUUID},document:{getElementById:element,querySelectorAll:()=>[...elements].filter(([id])=>id.startsWith('book-closing-')).map(([,e])=>e)},
  setTimeout(fn){timers.set(++timer,fn);return timer;},clearTimeout(id){timers.delete(id);},fetch:async(url,options={})=>{
    const body=options.body?JSON.parse(options.body):null;calls.push({url,body});
    if(url==='/api/lmc/preview')return response({canSave,blockers:canSave?[]:['Confira o fechamento.'],warnings:[],balance:{aboveReference:false}});
    if(url==='/api/lmc/save'){
      if(heldSave)await new Promise(resolve=>heldSave.resolve=resolve);
      if(status!==200)return response({message:'Gravação não confirmada.'},status);
      lastClosing=body.tanks[0].closing;
      return response({bookId:5555,date:body.date,productId:body.productId,mode:fixture.bookId?'refeito':'inserido',alreadyApplied,followingBooks:[]});
    }
    if(url.startsWith('/api/lmc/next?')){
      if(heldNext)await new Promise(resolve=>heldNext.resolve=resolve);
      if(failNext)return response({message:'Consulta temporariamente indisponível.'},503);
      return response({date:nextDate,productId:2});
    }
    if(url.startsWith('/api/lmc/day?'))return response({products:[{id:2,name:'Diesel'}],readings:[]});
    if(url.startsWith('/api/lmc/prepare?'))return response({...fixture,date:nextDate,bookId:null,version:'B'.repeat(64),tanks:[{tankId:1,capacity:30000,previousClosing:lastClosing,inherited:true}],savedMeasurements:null});
    throw new Error(`Consulta inesperada: ${url}`);
  }});
const run=code=>vm.runInContext(code,ctx);
const app=readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js',import.meta.url),'utf8');run(app.slice(0,app.indexOf('$("filters").addEventListener')));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-closing.js',import.meta.url),'utf8'));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-tank-table.js',import.meta.url),'utf8'));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-ui.js',import.meta.url),'utf8'));
run(`state.branch=17581;state.context={branches:[{id:17581}]};state.csrf='csrf';var ledgerLoads=0;loadBookLedger=async()=>ledgerLoads++;
  renderBookEditor=()=>{for(const t of bookState.preparation.tanks){const initial=initialBookVolumes(bookState.preparation,t);$('book-opening-'+t.tankId).value=String(initial.opening??'');$('book-closing-'+t.tankId).value=String(initial.closing??'');}$('book-pump-closing-1').value='3000';$('book-pump-calibration-1').value='0';$('book-observation').value='';};`);
async function flush(){for(const [id,fn] of [...timers]){timers.delete(id);await fn();}}
async function ready(){run(`bookState.saving=false;bookState.uncertain=false;bookState.nextBook=null;bookState.preparation=${JSON.stringify(fixture)};renderBookEditor();setBookMeasurementMethod('manual');`);element('book-closing-1').value='9000';run('scheduleBookPreview()');await flush();}
const saves=()=>calls.filter(x=>x.url==='/api/lmc/save'),nextCalls=()=>calls.filter(x=>x.url.startsWith('/api/lmc/next?'));
await ready();check(!element('save-book').disabled&&!element('save-next-book').disabled,'as duas ações usam a mesma prévia validada');
heldSave={};const saving=element('save-next-book').listeners.click();
check(element('save-book').disabled&&element('save-next-book').disabled&&nextCalls().length===0,'nenhum avanço antes da confirmação e ambos os botões bloqueados durante a gravação');
await run('saveCurrentBook(true)');check(saves().length===1,'clique repetido não dispara outra gravação');
heldSave.resolve();heldSave=null;await saving;
check(nextCalls().length===1&&nextCalls()[0].url.includes('product=2'),'gravação confirmada consulta próximo livro do mesmo combustível');
check(element('book-date').value==='2026-10-01'&&element('book-month').value==='2026-10','próxima data vem do banco e atravessa a virada do mês');
check(element('book-opening-1').value==='9000'&&element('book-closing-1').value==='','novo livro herda fechamento salvo e aguarda nova medição');
check(run('ledgerLoads')===0&&element('book-feedback').textContent.includes('5555'),'Salvar e próximo mantém editor e confirma o livro gravado');
check(element('save-next-book').disabled&&element('save-book').disabled,'livro seguinte incompleto não pode ser salvo');
await ready();const nextBefore=nextCalls().length;await element('save-book').listeners.click();
check(run('ledgerLoads')===1&&nextCalls().length===nextBefore,'Salvar comum continua voltando à lista');
await ready();status=503;await run('saveCurrentBook(true)');const uncertainRequest=saves().at(-1).body;
check(run('bookState.uncertain')&&nextCalls().length===nextBefore&&!element('save-book').disabled&&element('save-next-book').disabled,'resposta incerta permite apenas confirmar a gravação atual');
status=200;alreadyApplied=true;await element('save-book').listeners.click();alreadyApplied=false;
check(JSON.stringify(uncertainRequest)===JSON.stringify(saves().at(-1).body)&&nextCalls().length===nextBefore+1,'nova tentativa conserva UUID e intenção de abrir o próximo, inclusive se já estava gravado');
await ready();status=422;const nextRejected=nextCalls().length;await run('saveCurrentBook(true)');
check(!run('bookState.uncertain')&&nextCalls().length===nextRejected&&element('save-next-book').disabled,'rejeição da gravação mantém o dia e exige nova conferência');
status=200;await ready();failNext=true;await run('saveCurrentBook(true)');const savesBeforeRetry=saves().length;
check(!run('bookState.uncertain')&&run('bookState.requestBody===null')&&element('book-feedback').textContent.includes('5555')&&element('reload-book').textContent==='Tentar abrir próximo','falha ao consultar próximo preserva confirmação do livro já salvo');
failNext=false;await element('reload-book').listeners.click();
check(saves().length===savesBeforeRetry&&element('book-date').value===nextDate,'tentar abrir próximo repete apenas a consulta, sem gravar novamente');
fixture.bookId=5555;fixture.savedMeasurements=[{tankId:1,previousClosing:10000,closing:9000}];nextDate='2026-10-06';await ready();await run('saveCurrentBook(true)');
check(element('book-date').value==='2026-10-06'&&element('book-feedback').textContent.includes('refeito'),'ao refazer livro antigo, segue próxima inserção indicada pelo banco');
await ready();canSave=false;run('scheduleBookPreview()');await flush();const rejectedCount=saves().length;await run('saveCurrentBook(true)');
check(element('save-next-book').disabled&&saves().length===rejectedCount,'prévia com impedimento bloqueia também Salvar e próximo');
console.log(`${checks} verificações passaram; nenhuma gravação no banco.`);
