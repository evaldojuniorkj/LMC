// API simulada: navegação e consultas sem acesso ao banco.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
let checks=0;
function check(ok,label){assert.ok(ok,label);checks++;console.log(`OK: ${label}`);}
const elements=new Map(),calls=[];
function element(id){if(!elements.has(id))elements.set(id,{value:'',innerHTML:'',textContent:'',hidden:false,disabled:false,
  classList:{toggle(){}},setAttribute(){},removeAttribute(){},addEventListener(){}});return elements.get(id);}
const items=[{productId:1,product:'Gasolina',lastBookDate:'2026-09-03',missingPeriods:[{from:'2026-09-02',until:'2026-09-02',days:1}],inconsistencies:[
    {bookId:1,date:'2026-09-01',kind:'tank',equipmentId:1,code:'opening_mismatch',previousDate:'2026-08-31',previousClosing:10000,opening:11000,closing:9000,difference:1000},
    {bookId:1,date:'2026-09-01',kind:'pump',equipmentId:13,code:'opening_mismatch',previousDate:'2026-08-31',previousClosing:589675.17,opening:589676.40,closing:590600,difference:1.23}],
    tankReviews:[{bookId:1,date:'2026-09-01',possibleMisallocation:true,totalWithinReference:true,tanks:[
      {tankId:1,opening:10000,received:10000,sales:2000,bookStock:18000,closing:13000,difference:-5000,percent:-27.78,aboveReference:true},
      {tankId:2,opening:10000,received:0,sales:2000,bookStock:8000,closing:13000,difference:5000,percent:62.5,aboveReference:true}]}]},
  {productId:2,product:'Diesel',lastBookDate:'2026-09-05',missingPeriods:[{from:'2026-09-03',until:'2026-09-04',days:2}]}];
let held=null,fail=false;
const response=data=>({ok:true,text:async()=>JSON.stringify(data)});
const context=vm.createContext({console,Intl,URLSearchParams,clearTimeout(){},window:{scrollTo(){}},
  document:{getElementById:element,title:'',querySelectorAll:()=>[]},
  fetch:async(url,options={})=>{calls.push({url,method:options.method||'GET'});if(fail)throw new Error('Falha de consulta');if(held)return new Promise(resolve=>{held.resolve=()=>resolve(response(items));});return response(items);}});
const run=code=>vm.runInContext(code,context);
const app=readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js',import.meta.url),'utf8');run(app.slice(0,app.indexOf('$("filters").addEventListener')));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-closing.js',import.meta.url),'utf8'));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-tank-table.js',import.meta.url),'utf8'));
const books=readFileSync(new URL('../src/Lmc.Web/wwwroot/book-ui.js',import.meta.url),'utf8');run(books.slice(0,books.indexOf('$("book-month").value=previousMonthPeriod()')));
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/book-pending.js',import.meta.url),'utf8'));
run('state.branch=17581;var opened=null,ledgerLoads=0;openBookEditor=async(day,product)=>{opened={day,product};};loadBooks=()=>ledgerLoads++;');
await run('loadPendingBooks()');
check(element('pending-summary').textContent.includes('2 ponto(s)')&&element('pending-list').innerHTML.includes('Tanque 1')&&element('pending-list').innerHTML.includes('Bico 13'),'aba inicial apresenta diferenças por tanque e por bico');
check(element('pending-list').innerHTML.includes('589.675,17')&&element('pending-list').innerHTML.includes('+1,23')&&element('pending-list').innerHTML.includes('31/08/2026'),'encerrantes têm duas casas e mostram valor anterior, data e diferença');
check(element('pending-tanks-count').textContent==='1'&&element('pending-gaps-count').textContent==='3','contadores revelam distribuição para revisar e dias faltantes em abas próprias');
await run(`openPendingBook(1,'2026-09-01',1)`);
check(run('opened.day')==='2026-09-01'&&run('opened.product')===1,'conferir inconsistência abre o livro e combustível correspondentes');
element('pending-view').hidden=false;run('opened=null;');
await run(`openPendingBook(1,'2026-09-01',999)`);check(run('opened===null'),'ID não consultado não abre livro pelas pendências');
run('pendingBooksState.tab="tanks";renderPendingBooks()');
check(element('pending-list').innerHTML.includes('Possível distribuição incorreta')&&element('pending-list').innerHTML.includes('-5.000')&&element('pending-list').innerHTML.includes('O total do combustível está dentro'),'sobra e falta entre tanques aparecem mesmo com total correto');
check(element('pending-list').innerHTML.includes('Revisar entradas')&&element('pending-list').innerHTML.includes('não determina como a descarga foi dividida'),'ação leva à conferência sem inventar uma divisão de descarga');
run('var notesLoads=0;loadNotes=async()=>{notesLoads++;};');
await run(`openPendingNotes(1,'2026-09-01',1)`);
check(run('notesLoads')===1&&element('from').value==='2026-09-01'&&element('until').value==='2026-09-01'&&element('date-field').value==='operational'&&run('state.status')==='all','revisar entradas filtra dia operacional e inclui notas fora da lista de atenção');
element('pending-view').hidden=false;
run('pendingBooksState.tab="gaps";renderPendingBooks()');
check(element('pending-summary').textContent.includes('3 LMC(s)')&&element('pending-list').innerHTML.includes('Gasolina')&&element('pending-list').innerHTML.includes('Diesel'),'menu próprio lista os períodos de todos os combustíveis');
element('pending-product').value='1';run('renderPendingBooks()');
check(element('pending-summary').textContent.includes('1 LMC(s)')&&!element('pending-list').innerHTML.includes('Diesel'),'filtro limita a grade e a contagem ao combustível escolhido');
await run(`openPendingBook(1,'2026-09-02')`);
check(run('opened.day')==='2026-09-02'&&run('opened.product')===1&&!element('books-view').hidden&&element('pending-view').hidden&&run('ledgerLoads')===0,'preencher abre o editor correto sem iniciar consulta concorrente da grade');
check(calls.every(c=>c.method==='GET'),'consulta e abertura da pendência não gravam dados');
run('bookState.uncertain=true;opened=null;');
check(run('switchView("pending")')===false,'gravação com resposta incerta impede abandonar o livro');
await run(`openPendingBook(1,'2026-09-02')`);
check(run('opened===null'),'pendência não substitui livro aguardando confirmação de gravação');
run('bookState.uncertain=false;');element('pending-view').hidden=false;
await run(`openPendingBook(1,'2026-09-01')`);check(run('opened===null'),'ação não abre data fora das pendências consultadas');
run('state.branch=222;');await run(`openPendingBook(1,'2026-09-02')`);
check(run('opened===null'),'mudança de filial invalida os links da consulta anterior');
await run(`openPendingNotes(1,'2026-09-01',1)`);check(run('notesLoads')===1,'mudança de filial invalida também o atalho de revisão de entradas');
held={};run('state.branch=17581;');const older=run('loadPendingBooks()');
run('state.branch=222;');held.resolve();held=null;await older;
check(run('pendingBooksState.branch===null')&&!element('pending-list').innerHTML.includes('data-pending-date'),'resposta atrasada de outra filial não repõe links antigos');
fail=true;await run('loadPendingBooks()');
check(element('pending-list').innerHTML.includes('Falha de consulta')&&run('pendingBooksState.items.length')===0,'falha limpa a seleção anterior e permite atualizar a consulta');
run('pendingBooksState.items=[{productId:1,product:"Gasolina",missingPeriods:[]}];renderPendingBooks()');
check(element('pending-list').innerHTML.includes('sequência dos livros está completa'),'sequência completa aparece como estado vazio sem aviso de bloqueio');
run('pendingBooksState.tab="issues";renderPendingBooks()');
check(element('pending-list').innerHTML.includes('Nenhuma diferença encontrada'),'sem divergência mantém estado vazio explícito');
run(`pendingBooksState.items=[{productId:1,product:'<img src=x onerror=1>',missingPeriods:[],inconsistencies:[{bookId:2,date:'2026-09-01',kind:'pump',equipmentId:1,code:'opening_mismatch',previousDate:'2026-08-31',previousClosing:1000,opening:1000.0001,difference:.0001}]}];renderPendingBooks()`);
check(!element('pending-list').innerHTML.includes('<img')&&element('pending-list').innerHTML.includes('&lt;img'),'nome do combustível é escapado na grade de inconsistências');
check(element('pending-list').innerHTML.includes('1.000,0001')&&element('pending-list').innerHTML.includes('+0,0001'),'diferença menor que centésimo revela precisão necessária em vez de apresentar valores iguais');
console.log(`${checks} verificações passaram; nenhuma gravação.`);
