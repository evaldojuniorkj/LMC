"use strict";
const pendingBooksState={items:[],branch:null,request:0,tab:"issues"};
const pendingTabs=[["pending-issues-tab","issues"],["pending-tanks-tab","tanks"],["pending-gaps-tab","gaps"]];
const pendingAmount=new Intl.NumberFormat("pt-BR",{maximumFractionDigits:4});
const pendingCounter=new Intl.NumberFormat("pt-BR",{minimumFractionDigits:2,maximumFractionDigits:2});
const pendingPreciseCounter=new Intl.NumberFormat("pt-BR",{minimumFractionDigits:2,maximumFractionDigits:4});
function pendingValue(value,kind,precise=false) {
  return value==null?"—":(kind==="pump"?(precise?pendingPreciseCounter:pendingCounter):pendingAmount).format(value);
}
function pendingIssueLabel(issue) {
  return ({opening_mismatch:issue.kind==="pump"?"Encerrante inicial diferente do anterior":"Abertura diferente do fechamento anterior",
    counter_order:"Encerrante final menor que o inicial",missing_value:"Abertura ou fechamento não informado",
    duplicate_book:"Mais de um LMC salvo neste dia",duplicate_reading:"Registros repetidos neste LMC",
    ambiguous_previous:"Registros repetidos no dia anterior",missing_previous_closing:"Fechamento anterior não informado",
    incomplete_balance:"Entradas ou saídas ausentes / inválidas para conferir o saldo"})[issue.code]??"Conferir registro salvo";
}
async function loadPendingBooks() {
  const request=++pendingBooksState.request,branch=state.branch;
  pendingBooksState.items=[];pendingBooksState.branch=null;
  $("pending-list").innerHTML='<p class="empty">Conferindo os dias, os tanques e os bicos dos LMCs salvos…</p>';
  $("pending-list").setAttribute("aria-busy","true");$("pending-summary").textContent="Consultando…";
  $("pending-product").disabled=true;
  for(const id of ["pending-issues-count","pending-tanks-count","pending-gaps-count"])$(id).textContent="—";
  try {
    if(!branch)throw new Error("Selecione uma unidade para consultar as pendências.");
    const items=await api(`/api/lmc/pending?${new URLSearchParams({branch})}`);
    if(request!==pendingBooksState.request||branch!==state.branch)return;
    const selected=$("pending-product").value;
    pendingBooksState.items=items;pendingBooksState.branch=branch;
    $("pending-product").innerHTML='<option value="">Todos os combustíveis</option>'+items.map(p=>`<option value="${p.productId}">${esc(p.product)}</option>`).join("");
    $("pending-product").value=items.some(p=>String(p.productId)===selected)?selected:"";
    renderPendingBooks();
  } catch(error) {
    if(request!==pendingBooksState.request)return;
    $("pending-list").innerHTML=`<p class="notice error" role="alert">${esc(error.message)}</p>`;$("pending-summary").textContent="Consulta não concluída. Use Atualizar para tentar novamente.";
  } finally {
    if(request===pendingBooksState.request){$("pending-list").setAttribute("aria-busy","false");$("pending-product").disabled=false;}
  }
}
function renderPendingBooks() {
  const selected=Number($("pending-product").value);
  const items=pendingBooksState.items.filter(p=>!selected||p.productId===selected);
  const rows=items.flatMap(p=>p.missingPeriods.map(period=>({...period,productId:p.productId,product:p.product}))).sort((a,b)=>a.from.localeCompare(b.from)||a.productId-b.productId);
  const issues=items.flatMap(p=>(p.inconsistencies??[]).map(issue=>({...issue,productId:p.productId,product:p.product}))).sort((a,b)=>a.date.localeCompare(b.date)||a.productId-b.productId||(a.equipmentId??0)-(b.equipmentId??0));
  const tanks=items.flatMap(p=>(p.tankReviews??[]).map(review=>({...review,productId:p.productId,product:p.product}))).sort((a,b)=>a.date.localeCompare(b.date)||a.productId-b.productId);
  const days=rows.reduce((total,p)=>total+p.days,0);
  $("pending-issues-count").textContent=number.format(issues.length);$("pending-tanks-count").textContent=number.format(tanks.length);$("pending-gaps-count").textContent=number.format(days);
  for(const [id,tab] of pendingTabs) {
    $(id).classList.toggle("active",pendingBooksState.tab===tab);$(id).setAttribute("aria-pressed",String(pendingBooksState.tab===tab));
  }
  if(pendingBooksState.tab==="tanks") {
    $("pending-summary").textContent=tanks.length?`${tanks.length} LMC(s) com saldo por tanque para conferir`:"Nenhuma diferença acima da referência nos saldos calculáveis";
    $("pending-list").innerHTML=`<p class="pending-tank-intro">Confira o destino da descarga com as medições reais. O total do combustível pode estar correto mesmo quando os tanques estão errados. Referência da conferência: 0,6% do estoque escritural de cada tanque.</p>`+(tanks.length?tanks.map(p=>`<article class="pending-tank-review"><div class="pending-tank-heading"><div><h2>${date(p.date)} · ${esc(p.product)}</h2><p>${p.possibleMisallocation?"Possível distribuição incorreta da entrada entre tanques.":"Saldo do tanque precisa de conferência."} ${p.totalWithinReference?"O total do combustível está dentro da referência, mas há diferença por tanque.":"O total do combustível também está fora da referência."}</p></div><div class="pending-tank-actions"><button type="button" class="button review-button" data-pending-issue="${p.bookId}" data-pending-product="${p.productId}" data-pending-date="${p.date}">Conferir LMC</button><button type="button" class="button secondary" data-pending-notes="${p.bookId}" data-pending-product="${p.productId}" data-pending-date="${p.date}">Revisar entradas</button></div></div><div class="pending-scroll"><table class="pending-table pending-tank-table"><thead><tr><th>Tanque</th><th class="numeric">Abertura</th><th class="numeric">Entradas</th><th class="numeric">Saídas</th><th class="numeric">Escritural</th><th class="numeric">Fechamento</th><th class="numeric">Perda / sobra</th><th class="numeric">%</th></tr></thead><tbody>${p.tanks.map(t=>`<tr${t.aboveReference?' class="pending-tank-alert"':""}><td><strong>Tanque ${t.tankId}</strong>${t.aboveReference?'<small>Conferir</small>':""}</td>${[t.opening,t.received,t.sales,t.bookStock,t.closing,t.difference].map(v=>`<td class="numeric">${pendingValue(v,"tank")}</td>`).join("")}<td class="numeric">${t.percent==null?"—":pendingCounter.format(t.percent)}</td></tr>`).join("")}</tbody></table></div><p class="pending-tank-caption">Escritural = abertura + entradas − saídas. Perda / sobra = fechamento − escritural. Confira também vendas, medições e eventuais transferências. O alerta não determina como a descarga foi dividida.</p></article>`).join(""):'<p class="empty"><strong>Nenhum saldo por tanque para revisar.</strong>A conferência depende dos volumes e fechamentos salvos; não confirma a descarga física.</p>');
    return;
  }
  if(pendingBooksState.tab==="issues") {
    const books=new Set(issues.map(p=>p.bookId));
    $("pending-summary").textContent=issues.length?`${issues.length} ponto(s) para conferir em ${books.size} LMC(s)`:"Nenhuma inconsistência encontrada nos valores conferidos";
    $("pending-list").innerHTML=issues.length?`<div class="pending-scroll"><table class="pending-table pending-issues-table"><thead><tr><th>Data / combustível</th><th>Ponto para conferir</th><th class="numeric">Fechamento anterior</th><th class="numeric">Abertura salva</th><th class="numeric">Diferença (L)</th><th><span class="sr-only">Ações</span></th></tr></thead><tbody>${issues.map(p=>{
      const precise=p.kind==="pump"&&p.difference!=null&&Math.abs(p.difference)<.01;
      const point=p.kind==="tank"?`Tanque ${p.equipmentId}`:p.kind==="pump"?`Bico ${p.equipmentId}`:"Livro";
      return `<tr><td><strong>${date(p.date)}</strong><span>${esc(p.product)}</span></td><td><strong>${point}</strong><span>${pendingIssueLabel(p)}</span>${p.code==="counter_order"?`<small>Fechamento salvo: ${pendingValue(p.closing,p.kind)} L</small>`:""}</td><td class="numeric">${pendingValue(p.previousClosing,p.kind,precise)}${p.previousDate?`<small>${date(p.previousDate)}</small>`:""}</td><td class="numeric">${pendingValue(p.opening,p.kind,precise)}</td><td class="numeric pending-difference">${p.difference==null?"—":`${p.difference>0?"+":""}${pendingValue(p.difference,p.kind,precise)}`}</td><td><button type="button" class="button review-button" data-pending-issue="${p.bookId}" data-pending-product="${p.productId}" data-pending-date="${p.date}" aria-label="Conferir ${esc(p.product)} em ${date(p.date)}, ${point}">Conferir LMC</button></td></tr>`;
    }).join("")}</tbody></table></div>`:`<p class="empty"><strong>${items.length?"Nenhuma diferença encontrada.":"Nenhum histórico de LMC disponível."}</strong>${items.length?`Tanques e bicos foram comparados com o mesmo equipamento no dia anterior, quando disponível.${days?" Há dias sem LMC; confira a outra aba.":""}`:"As inconsistências serão identificadas a partir dos livros salvos nesta unidade."}</p>`;
    return;
  }
  $("pending-summary").textContent=rows.length?`${number.format(days)} LMC(s) pendente(s) · ${rows.length} período(s)`:"Nenhum intervalo pendente";
  $("pending-list").innerHTML=rows.length?`<div class="pending-scroll"><table class="pending-table"><thead><tr><th>Combustível</th><th>Período sem LMC</th><th class="numeric">Dias</th><th><span class="sr-only">Ações</span></th></tr></thead><tbody>${rows.map(p=>`<tr><td><strong>${esc(p.product)}</strong></td><td>${date(p.from)}${p.until!==p.from?` a ${date(p.until)}`:""}</td><td class="numeric">${p.days}</td><td><button type="button" class="button review-button" data-pending-date="${p.from}" data-pending-product="${p.productId}" aria-label="Preencher ${esc(p.product)} em ${date(p.from)}">Preencher</button></td></tr>`).join("")}</tbody></table></div>`:`<p class="empty"><strong>${items.length?"A sequência dos livros está completa.":"Nenhum histórico de LMC disponível."}</strong>${items.length?"Não há dias faltando entre o primeiro e o último livro salvo dos combustíveis consultados.":"As pendências serão identificadas a partir dos livros salvos nesta unidade."}</p>`;
}
async function openPendingBook(product,day,bookId=null) {
  if(bookState.saving||bookState.uncertain||pendingBooksState.branch!==state.branch||$("pending-view").hidden)return;
  const item=pendingBooksState.items.find(p=>p.productId===product);
  if(bookId===null?!item?.missingPeriods.some(p=>p.from===day):![...(item?.inconsistencies??[]),...(item?.tankReviews??[])].some(p=>p.bookId===bookId&&p.date===day))return;
  if(!switchView("books",false))return;
  await openBookEditor(day,product);
}
async function openPendingNotes(product,day,bookId) {
  if(pendingBooksState.branch!==state.branch||$("pending-view").hidden)return;
  const item=pendingBooksState.items.find(p=>p.productId===product);
  if(!item?.tankReviews?.some(p=>p.bookId===bookId&&p.date===day)||!switchView("notes",false))return;
  $("from").value=day;$("until").value=day;$("date-field").value="operational";$("search").value="";
  state.status="all";state.page=1;
  document.querySelectorAll("[data-status]").forEach(b=>{b.classList.toggle("active",b.dataset.status==="all");b.setAttribute("aria-pressed",String(b.dataset.status==="all"));});
  showError("pending-notes-context",`Confira a distribuição de ${item.product} entre os tanques nas entradas de ${date(day)}. A lista mostra todas as notas desse dia operacional.`);
  await loadNotes();
}
$("refresh-pending").addEventListener("click",loadPendingBooks);
$("pending-product").addEventListener("change",renderPendingBooks);
for(const [id,tab] of pendingTabs)$(id).addEventListener("click",()=>{
  pendingBooksState.tab=tab;if(pendingBooksState.branch===state.branch)renderPendingBooks();
});
$("pending-list").addEventListener("click",event=>{
  const button=event.target.closest('[data-pending-date]');
  if(button?.dataset.pendingNotes){openPendingNotes(Number(button.dataset.pendingProduct),button.dataset.pendingDate,Number(button.dataset.pendingNotes));return;}
  if(button)openPendingBook(Number(button.dataset.pendingProduct),button.dataset.pendingDate,button.dataset.pendingIssue?Number(button.dataset.pendingIssue):null);
});
