// API simulada: nenhuma exclusão real ou consulta ao banco.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {randomUUID} from 'node:crypto';
import vm from 'node:vm';
let checks=0;
function check(ok,label){assert.ok(ok,label);checks++;console.log(`OK: ${label}`);}
const elements=new Map();
function element(id){
  if(!elements.has(id))elements.set(id,{value:'',textContent:'',innerHTML:'',hidden:false,disabled:false,open:false,events:{},
    addEventListener(name,fn){this.events[name]=fn;},showModal(){this.open=true;},close(){this.open=false;this.events.close?.();}});
  return elements.get(id);
}
let status=503,blocked=false,pending=null,loaded=0;
const calls=[],response=(data,status=200)=>({ok:status<400,status,text:async()=>JSON.stringify(data)});
const preview=()=>({branch:17581,bookId:4428,productId:1,product:'GASOLINA C COMUM',date:'2026-09-24',number:25,tankCount:2,pumpCount:7,receiptCount:0,
  version:'A'.repeat(64),followingBooks:[{id:4432,date:'2026-09-25'}],blockers:blocked?['Livro vinculado a envio externo.']:[]});
const context=vm.createContext({console,Intl,URLSearchParams,crypto:{randomUUID},document:{getElementById:element},clearTimeout(){},
  fetch:async(url,options={})=>{
    const body=options.body?JSON.parse(options.body):null;calls.push({url,body});
    if(url.startsWith('/api/lmc/deletion?')){
      if(pending)return new Promise(resolve=>{pending.resolve=()=>resolve(response(preview()));});
      return response(preview());
    }
    if(url==='/api/lmc/deletion-batch')return response({branch:17581,books:body.bookIds.map(id=>({...preview(),bookId:id})),followingBooks:[],blockers:blocked?['Vínculo de envio externo.']:[]});
    if(url==='/api/lmc/delete-batch')return status===200?response({branch:17581,books:body.books.map(b=>({...preview(),bookId:b.bookId})),followingBooks:[],lastBookDate:'2026-08-31',alreadyDeleted:false}):response({message:'Sem confirmação.'},status);
    if(url==='/api/lmc/delete')return status===200?response({...preview(),alreadyDeleted:false,lastBookDate:'2026-09-28'}):response({message:status===409?'Livro alterado.':'Sem confirmação.'},status);
    return response({token:'novo-token'});
  },onLoad(){loaded++;}});
const run=code=>vm.runInContext(code,context);
const app=readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js',import.meta.url),'utf8');run(app.slice(0,app.indexOf('$("filters").addEventListener')));
run(`state.branch=17581;state.csrf='csrf';state.context={branches:[{id:17581,name:'Unidade de teste',lastBookDate:'2026-09-30'}]};
  const bookState={saving:false,uncertain:false,preparation:null};function invalidateBookPreview(){};async function loadBookLedger(){onLoad();}`);
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-delete.js',import.meta.url),'utf8'));
const deletes=()=>calls.filter(c=>c.url==='/api/lmc/delete');
await run('openBookDeletion(17581,4428)');
check(element('delete-book-dialog').open&&!element('confirm-delete-book').disabled&&deletes().length===0,'abrir confirmação não exclui automaticamente');
check(element('delete-book-content').innerHTML.includes('24/09/2026')&&element('delete-book-content').innerHTML.includes('GASOLINA C COMUM'),'confirmação identifica data e combustível');
run('closeBookDeletion()');
check(!element('delete-book-dialog').open&&run('bookDeleteState.payload===null')&&deletes().length===0,'cancelar não grava e descarta a confirmação');
await run('openBookDeletion(17581,4428)');await run('confirmBookDeletion()');
check(run('bookDeleteState.uncertain')&&element('cancel-delete-book').disabled,'falha sem confirmação mantém a exclusão pendente');
run('closeBookDeletion()');
check(element('delete-book-dialog').open,'exclusão pendente não pode ser descartada pelo botão de fechar');
await run('confirmBookDeletion()');
check(JSON.stringify(deletes()[0].body)===JSON.stringify(deletes()[1].body),'repetição usa o mesmo livro, versão e identificador');
status=409;await run('confirmBookDeletion()');
check(run('bookDeleteState.payload===null')&&!element('cancel-delete-book').disabled&&!element('retry-delete-book').hidden,'conflito exige atualizar a confirmação antes de excluir');
await run('openBookDeletion(17581,4428)');status=200;await run('confirmBookDeletion()');
check(!element('delete-book-dialog').open&&loaded===1&&element('book-feedback').textContent.includes('excluído'),'exclusão confirmada retorna à grade com o resultado');
check(run('state.context.branches[0].lastBookDate')==='2026-09-28','última data disponível é atualizada após excluir');
blocked=true;await run('openBookDeletion(17581,4428)');const before=deletes().length;await run('confirmBookDeletion()');
check(element('confirm-delete-book').disabled&&deletes().length===before,'envio externo bloqueia exclusão pela interface');
run('closeBookDeletion()');blocked=false;pending={};const loading=run('openBookDeletion(17581,4428)');run('closeBookDeletion()');pending.resolve();await loading;
check(!element('delete-book-dialog').open&&run('bookDeleteState.payload===null'),'resposta atrasada não reabre confirmação cancelada');
pending=null;status=503;await run('openBookDeletion(17581,[4428,4432])');
check(run('bookDeleteState.payload.books.length')===2&&element('delete-book-content').innerHTML.includes('2 LMC(s) selecionado(s)'),'confirmação do lote inclui a lista exata dos livros selecionados');
await run('confirmBookDeletion()');await run('confirmBookDeletion()');
const batches=calls.filter(c=>c.url==='/api/lmc/delete-batch');
check(batches.length===2&&JSON.stringify(batches[0].body)===JSON.stringify(batches[1].body),'resposta incerta repete o lote inteiro com o mesmo UUID e versões');
status=200;await run('confirmBookDeletion()');
check(!element('delete-book-dialog').open&&element('book-feedback').textContent.includes('2 LMC(s)')&&run('state.context.branches[0].lastBookDate')==='2026-08-31','lote confirmado atualiza lista, quantidade excluída e última data');
blocked=true;await run('openBookDeletion(17581,[4428,4432])');const batchCount=calls.filter(c=>c.url==='/api/lmc/delete-batch').length;await run('confirmBookDeletion()');
check(element('confirm-delete-book').disabled&&calls.filter(c=>c.url==='/api/lmc/delete-batch').length===batchCount,'um impedimento impede enviar a exclusão de todo o lote');
run('closeBookDeletion()');
console.log(`${checks} verificações passaram; nenhuma exclusão real.`);
