// Fluxo assíncrono da revisão com API simulada. Não acessa o banco nem o navegador.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import vm from 'node:vm';

let checks = 0;
function check(condition, label) { assert.ok(condition, label); checks++; console.log(`OK: ${label}`); }
const source = readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js', import.meta.url), 'utf8');
const marker = source.indexOf('$("filters").addEventListener');
assert.ok(marker > 0);
const elements = new Map();
function element(id) {
  if (!elements.has(id)) elements.set(id, {
    value: '', hidden: false, disabled: false, open: false, textContent: '',
    set innerHTML(value) { this.html = value; if (id === 'target-shift') this.value = ''; },
    get innerHTML() { return this.html || ''; },
    addEventListener() {}, setAttribute() {},
    showModal() { this.open = true; }, close() { this.open = false; },
    reset() { for (const id of ['target-date','target-shift','reason']) element(id).value = ''; },
    checkValidity() { return !!element('target-date').value && !!element('target-shift').value && element('reason').value.length <= 80; }
  });
  return elements.get(id);
}
const timers = new Map(); let nextTimer = 0;
const note = { id:541442,branchId:17581,databaseId:17581,number:1337740,supplier:'Fornecedor',products:'Gasolina',volume:6000,
  documentDate:'2026-08-13',issueFieldDate:'2026-08-14',operationalDate:'2026-09-04',generalShift:true,shiftId:1,shiftNumber:0,gapDays:21,inconsistent:false };
let detail = {note,parcels:[],movements:[],version:'original'};
const requests = []; let pendingPreview = null; let applyStatus = 503;
const response = (value, status=200) => ({ok:status<400,status,text:async()=>JSON.stringify(value)});
const makePreview = body => ({canApply:true,sourceDate:detail.note.operationalDate,sourceShiftId:1,targetDate:body.operationalDate,
  targetShift:{id:body.shiftId,number:body.shiftId===1?0:1},reason:body.reason,version:'original',effects:[],booksToReview:[],blockers:[],bookReceipts:[],allocations:body.allocations??null});
const sandbox = vm.createContext({
  console,Intl,URLSearchParams,crypto:{randomUUID},
  setTimeout(fn) { const id=++nextTimer;timers.set(id,fn);return id; }, clearTimeout(id) {timers.delete(id);},
  document:{getElementById:element,querySelectorAll:()=>['target-date','target-shift','reason'].map(element)},
  fetch:async(url,options={})=>{
    const body=options.body?JSON.parse(options.body):null;requests.push({url,body});
    if(url.endsWith('/history')) return response([]);
    if(url.startsWith('/api/shifts?')) return response([{id:1,number:0,isGeneral:true},{id:8912,number:1,isGeneral:false}]);
    if(url.endsWith('/preview')) {
      if(pendingPreview) return new Promise(resolve=>{pendingPreview.resolve=()=>resolve(response(makePreview(body)));});
      return response(makePreview(body));
    }
    if(url.endsWith('/apply')) return applyStatus===200
      ? response({sourceDate:note.operationalDate,targetDate:body.operationalDate,targetShiftId:body.shiftId})
      : response({message:applyStatus===409?'A nota mudou.':'Resposta indisponível.'},applyStatus);
    return response(detail);
  }
});
const run = code => vm.runInContext(code,sandbox);
run(source.slice(0,marker));
run('state.context={actor:"teste"};state.csrf="csrf";loadNotes=async()=>{};');
element('threshold').value='5';
async function flush() { for(const [id,fn] of [...timers]) {timers.delete(id);await fn();} }
const writes = () => requests.filter(r=>r.url.endsWith('/apply'));

await run('openReview("17581/17581/541442")');
check(element('target-date').value==='2026-08-14' && element('target-shift').value==='1','abertura sugere entrada registrada e preserva turno geral');
check(element('apply-button').disabled,'salvamento aguarda a conferência automática');
await flush();
check(!element('apply-button').disabled && writes().length===0,'conferência libera o botão sem gravar automaticamente');

pendingPreview={};
element('target-date').value='2026-08-15';run('schedulePreview()');
const delayed=flush();
element('target-date').value='2026-08-16';run('schedulePreview()');
check(element('apply-button').disabled && run('state.preview===null'),'editar invalida imediatamente a proposta anterior');
pendingPreview.resolve();await delayed;
check(element('apply-button').disabled && run('state.preview===null'),'resposta atrasada não libera uma correção desatualizada');
pendingPreview=null;await flush();
check(run('state.preview.targetDate')==='2026-08-16','conferência mais recente usa a data escolhida');

await run('applyCorrection()');
check(writes().length===1 && element('target-date').disabled,'falha sem confirmação mantém a proposta congelada para nova tentativa');
await run('applyCorrection()');
check(JSON.stringify(writes()[0].body)===JSON.stringify(writes()[1].body),'nova tentativa preserva UUID, versão, destino e aceite dos livros');
applyStatus=409;await run('applyCorrection()');
check(element('apply-button').disabled && run('state.preview===null') && !element('retry-review').hidden,'conflito de versão exige nova conferência');

applyStatus=200;
await run('openReview("17581/17581/541442")');await flush();
await run('applyCorrection()');
check(!element('review-dialog').open && !element('review-feedback').hidden,'salvamento confirmado retorna à lista e mostra o resultado');
check(writes().at(-1).body.acceptBookReview===true && writes().at(-1).body.reason==='','clique em salvar aceita revisão posterior com observação vazia');

detail={...detail,note:{...note,issueFieldDate:'2026-08-12'}};
await run('openReview("17581/17581/541442")');await flush();
check(element('target-date').value===note.operationalDate && element('apply-button').disabled,'entrada anterior à emissão não vira sugestão automática');
detail={...detail,note:{...note,issueFieldDate:null}};
await run('openReview("17581/17581/541442")');await flush();
check(element('target-date').value===note.operationalDate && element('apply-button').disabled,'entrada ausente não é inventada');
detail={...detail,note:{...note,generalShift:false,shiftId:8912,shiftNumber:1}};
await run('openReview("17581/17581/541442")');await flush();
check(element('target-date').value===note.operationalDate && element('target-shift').value==='8912','turno diário existente não é trocado por sugestão genérica');
check(writes().length===4,'somente os cliques explícitos de teste chamaram a gravação simulada');
detail={note:{...note,issueFieldDate:note.operationalDate,volume:10000},
  parcels:[{id:10,productId:1,product:'Gasolina',tankId:1,tank:'1',volume:10000,operationalDate:note.operationalDate}],movements:[],version:'original',
  allocationTanks:[{tankId:1,productId:1,tank:'1',active:true},{tankId:2,productId:1,tank:'2',active:true}]};
await run('openReview("17581/17581/541442")');await flush();
check(element('apply-button').disabled,'sem mudança de data, turno ou volume não oferece gravação');
run('editAllocation(1,"volume","6000")');
check(element('apply-button').disabled&&element('allocation-total-1').textContent.includes('Faltam'),'volume faltante impede salvar e mostra a diferença');
run('addAllocation(1)');await flush();
check(!element('apply-button').disabled&&run('state.preview.allocations[1].volume')===4000,'dividir preenche o restante em outro tanque e permite correção só de volumes');
run('editAllocation(2,"volume","5000")');
check(element('apply-button').disabled&&element('allocation-total-1').textContent.includes('Excedeu'),'volume excedente invalida a prévia imediatamente');
run('editAllocation(2,"volume","4000")');await flush();
applyStatus=503;await run('applyCorrection()');
const allocationAttempt=writes().at(-1).body;
run('editAllocation(1,"volume","5000")');
check(run('state.allocations[0].volume')===6000&&element('close-review').disabled,'resposta incerta congela distribuição e impede descartar a operação');
await run('applyCorrection()');
check(JSON.stringify(writes().at(-1).body)===JSON.stringify(allocationAttempt),'repetição conserva exatamente os litros, tanques e identificador');
applyStatus=200;await run('applyCorrection()');
check(element('review-feedback').textContent.includes('Volumes redistribuídos'),'sucesso informa a redistribuição mantendo o total');
console.log(`${checks} verificações passaram; API simulada, nenhum acesso ao banco.`);
