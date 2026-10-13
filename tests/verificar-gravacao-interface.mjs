// Ensaios de interface com API simulada: nenhuma gravação ou consulta ao SQL Server.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import vm from 'node:vm';
let checks=0;
function check(ok,label){assert.ok(ok,label);checks++;console.log(`OK: ${label}`);}
const elements=new Map();
function element(id){if(!elements.has(id))elements.set(id,{value:'',textContent:'',innerHTML:'',hidden:false,disabled:false,validity:{badInput:false},setAttribute(){},checkValidity(){return true;}});return elements.get(id);}
const timers=new Map();let timer=0,saveStatus=503,pending=null,auditWarning=null;const calls=[];
const response=(data,status=200)=>({ok:status<400,status,text:async()=>JSON.stringify(data)});
const context=vm.createContext({console,Intl,URLSearchParams,crypto:{randomUUID},
  document:{getElementById:element,querySelectorAll:()=>['book-opening-1','book-closing-1','book-observation'].map(element)},
  setTimeout(fn){const id=++timer;timers.set(id,fn);return id;},clearTimeout(id){timers.delete(id);},
  fetch:async(url,options={})=>{
    const body=options.body?JSON.parse(options.body):null;calls.push({url,body});
    if(url.startsWith('/api/lmc/next?'))return response({productId:2,date:'2026-09-01',lastBookDate:'2026-08-31'});
    if(url==='/api/lmc/preview'){
      const value={canSave:true,blockers:[],warnings:[],balance:{aboveReference:false}};
      if(pending)return new Promise(resolve=>{pending.resolve=()=>resolve(response(value));});
      return response(value);
    }
    if(url==='/api/lmc/save')return saveStatus===200?response({bookId:5555,date:body.date,productId:1,mode:'inserido',alreadyApplied:false,followingBooks:[],auditWarning}):response({message:saveStatus===409?'As fontes mudaram.':'Resposta indisponível.'},saveStatus);
    return response({token:'csrf'});
  }});
const run=code=>vm.runInContext(code,context);
const app=readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js',import.meta.url),'utf8');run(app.slice(0,app.indexOf('$("filters").addEventListener')));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-closing.js',import.meta.url),'utf8'));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-tank-table.js',import.meta.url),'utf8'));
const books=readFileSync(new URL('../src/Lmc.Web/wwwroot/book-ui.js',import.meta.url),'utf8');run(books.slice(0,books.indexOf('$("book-month").value=previousMonthPeriod()')));
run(`var savedBook={bookId:42,savedMeasurements:[{tankId:1,previousClosing:12345.6789,closing:11234.5678}]};var inheritedTank={tankId:1,previousClosing:14000,inherited:true};`);
check(run('initialBookVolumes(savedBook,inheritedTank).opening')===12345.6789&&run('initialBookVolumes(savedBook,inheritedTank).closing')===11234.5678,'livro existente carrega abertura e fechamento salvos sem sobrescrever pela herança');
check(run('initialBookVolumes({...savedBook,savedMeasurements:[{tankId:1,previousClosing:0,closing:0}]},inheritedTank).opening')===0&&run('initialBookVolumes({...savedBook,savedMeasurements:[{tankId:1,previousClosing:0,closing:0}]},inheritedTank).closing')===0,'valores salvos iguais a zero são preservados');
check(run('initialBookVolumes({...savedBook,savedMeasurements:[{tankId:1,previousClosing:null,closing:null}]},inheritedTank).opening')===null,'abertura ausente no livro existente não é substituída automaticamente');
check(run('initialBookVolumes({bookId:null},inheritedTank).opening')===14000&&run('initialBookVolumes({bookId:null},inheritedTank).closing')===null,'novo livro continua herdando a abertura e aguardando o fechamento do dia');
run(`state.branch=17581;state.csrf='csrf';state.context={branches:[{id:17581,lastBookDate:'2026-09-28'}]};loadBookLedger=async()=>{};
bookState.preparation={branch:17581,date:'2026-09-29',productId:1,version:'A'.repeat(64),bookId:null,tanks:[{tankId:1,inherited:false}],pumps:[{pumpId:1,tankId:1,opening:2000,closing:3000,calibration:0,sales:1000,price:7}],documents:[],followingBooks:[],blockers:[],monthSalesBefore:0,monthValueBefore:0};`);
element('book-pump-closing-1').value='3000';element('book-pump-calibration-1').value='0';element('book-opening-1').value='10000';element('book-closing-1').value='9000';run('setBookMeasurementMethod(null)');
async function flush(){for(const [id,fn] of [...timers]){timers.delete(id);await fn();}}
const saves=()=>calls.filter(c=>c.url==='/api/lmc/save');
check(run('bookMeasurementMethod()')==='manual'&&element('measurement-manual').checked&&!element('measurement-electronic').checked,'Manual fica selecionado por padrão sem escolha adicional');
run('setBookMeasurementMethod("eletronica")');
check(run('collectBookRequest().measurementMethod')==='eletronica'&&!element('measurement-manual').checked,'origem eletrônica salva é preservada e enviada pela opção de rádio');
run('setBookMeasurementMethod("manual")');
run('scheduleBookPreview()');await flush();
check(!element('save-book').disabled&&saves().length===0,'preencher os volumes com origem padrão permite salvar após validar, sem gravar automaticamente');
pending={};element('book-closing-1').value='8999';run('scheduleBookPreview()');const old=flush();
element('book-closing-1').value='8998';run('scheduleBookPreview()');
check(element('save-book').disabled&&run('bookState.preview===null'),'edição invalida imediatamente a prévia de gravação');
pending.resolve();await old;
check(element('save-book').disabled,'prévia atrasada não libera gravação de valores antigos');
pending=null;await flush();await run('saveCurrentBook()');
check(run('bookState.uncertain')&&element('book-closing-1').disabled,'resposta incerta preserva a solicitação e congela medições');
await run('saveCurrentBook()');
check(JSON.stringify(saves()[0].body)===JSON.stringify(saves()[1].body),'nova tentativa usa exatamente o mesmo UUID, versão e medições');
saveStatus=409;await run('saveCurrentBook()');
check(!run('bookState.uncertain')&&run('bookState.preview===null')&&!element('reload-book').hidden,'conflito exige atualizar a conferência antes de salvar');
run('scheduleBookPreview()');await flush();saveStatus=200;auditWarning='LMC salvo; confirmação do arquivo de auditoria pendente.';await run('saveCurrentBook()');
check(element('book-feedback').textContent.includes('5555')&&!element('book-feedback').hidden,'confirmação identifica o livro gravado');
check(element('book-feedback').textContent.includes(auditWarning)&&!run('bookState.uncertain'),'aviso do log após commit preserva a confirmação do LMC sem pedir outra gravação');auditWarning=null;
check(run('state.context.branches[0].lastBookDate')==='2026-09-29','próxima inserção parte da última data gravada');
element('book-closing-1').value='';run('scheduleBookPreview()');
check(element('save-book').disabled&&timers.size===0,'medição ausente mantém o salvamento bloqueado');
run('bookState.preparation.tanks[0]={tankId:1,previousClosing:11000,inherited:true};bookState.uncertain=true;');
element('book-pump-closing-1').value='3000';element('book-pump-calibration-1').value='0';element('book-opening-1').value='10000';run('usePreviousBookClosing(1)');
check(element('book-opening-1').value==='10000','resposta incerta impede substituir a abertura pela herança');
run('bookState.uncertain=false;usePreviousBookClosing(1)');
check(element('book-opening-1').value===11000&&element('book-inherit-opening-1').hidden,'fechamento anterior diferente só substitui a abertura por ação explícita');
run('var openedNext=null;openBookEditor=async(day,product,newEntry)=>{openedNext={day,product,newEntry};};bookState.preview={canSave:true};');
element('book-month').value='2026-09';await run('openNewBook(2)');
check(run('openedNext.day')==='2026-09-01'&&run('openedNext.product')===2&&run('openedNext.newEntry')===true,'inserir usa data consultada no banco para o combustível escolhido, sem aproveitar a data antiga da sessão');
check(run('bookState.preview===null')&&element('save-book').disabled,'consulta da próxima data invalida imediatamente qualquer gravação anterior');
run('bookState.rows=[{id:1,productId:1},{id:2,productId:2}];bookState.selectedIds=new Set([1]);');element('ledger-product').value='';run('updateBookSelection()');
check(!element('delete-selected-books').disabled&&element('select-all-books').indeterminate,'seleção parcial informa a quantidade e habilita exclusão em lote');
run('clearBookSelection()');
check(element('delete-selected-books').disabled&&run('bookState.selectedIds.size')===0,'limpar ou atualizar seleção desabilita a exclusão do lote');
run(`bookState.preparation={branch:17581,date:'2026-09-29',productId:1,version:'A'.repeat(64),bookId:null,tanks:[{tankId:1,inherited:false}],pumps:[{pumpId:13,tankId:1,opening:1000,closing:2000,calibration:0,sales:1000,price:7,cutoff:{automationOpening:1000,automationClosing:2000,shiftOpening:990,shiftClosing:2020,firstAt:'2026-09-29T00:05:00',lastAt:'2026-09-29T23:55:00'}}],documents:[],followingBooks:[],blockers:[],monthSalesBefore:0,monthValueBefore:0};bookState.counterSources={};`);
element('book-opening-1').value='10000';element('book-closing-1').value='9000';element('book-pump-closing-13').value='2000';element('book-pump-calibration-13').value='0';
run('bookState.day={readings:[]};bookState.preparation.warnings=[];bookState.preparation.history=[];renderBookEditor();scheduleBookPreview()');
check(run('bookState.counterSources[13]')==='automation'&&element('book-editor-body').innerHTML.includes('value="automation" checked')&&element('book-editor-body').innerHTML.includes('diferença no fechamento'),'editor seleciona automação por padrão e mantém aviso visível');
check(element('book-pump-opening-13').textContent==='1.000,00'&&element('total-sales').textContent==='1.000','abertura fixa e vendas da automação aparecem sem pedir nova escolha');
await flush();
check(!element('save-book').disabled&&element('book-pump-opening-13').textContent==='1.000,00'&&run('bookState.requestBody.pumps[0].counterSource')==='automation','origem padrão é enviada ao servidor e permite salvar após prévia sem clique adicional');
pending={};run('scheduleBookPreview()');const previousSource=flush();
run('selectBookPumpSource(13,"shifts")');
check(element('save-book').disabled&&element('book-pump-closing-13').value===2020&&element('book-pump-opening-13').textContent==='1.000,00'&&element('total-sales').textContent==='1.020','trocar para turnos muda apenas fechamento, conserva abertura e invalida a prévia');
pending.resolve();await previousSource;pending=null;
check(element('save-book').disabled,'prévia antiga da automação não habilita o salvamento dos turnos');await flush();
element('book-pump-closing-13').value='2021.1234';element('book-pump-calibration-13').value='1.1234';run('scheduleBookPreview()');await flush();
check(run('bookState.requestBody.pumps[0].closing')===2021.1234&&element('book-pump-closing-display-13').textContent==='2.021,12'&&element('total-sales').textContent==='1.020'&&element('book-pump-opening-13').textContent==='1.000,00','edição após escolha conserva abertura, precisão e duas casas nos encerrantes');
const countBeforeSave=saves().length;await run('saveCurrentBook()');
check(saves().length===countBeforeSave+1&&saves().at(-1).body.pumps[0].counterSource==='shifts','salvamento transmite a origem conferida junto dos encerrantes');
run('bookState.uncertain=true;selectBookPumpSource(13,"automation")');
check(run('bookState.counterSources[13]')==='shifts','resposta incerta não permite trocar a origem do pedido');
check(run('bookReadingUsable({volume:0,canUse:true,invalidVolume:null})'),'zero medido continua selecionável');
check(run('!bookReadingUsable({volume:null,canUse:false,invalidVolume:"2.22e+029"})&&!bookReadingUsable({volume:1e29})&&!bookReadingUsable({volume:-1})'),'leituras inválidas ou fora da faixa não preenchem os campos');
check(run('bookReadingVolume({volume:null,invalidVolume:"2.22e+029"}).includes("Leitura inválida")&&bookReadingVolume({volume:null,invalidVolume:"2.22e+029"}).includes("2.22e+029")'),'leitura inválida mostra o motivo e o valor original');
check(run('!bookReadingVolume({volume:null,invalidVolume:"<img>"}).includes("<img>")'),'valor textual original é escapado antes da exibição');
console.log(`${checks} verificações passaram; API simulada, nenhum acesso ao banco.`);
