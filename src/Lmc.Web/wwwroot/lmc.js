"use strict";
const conference = { day:null, product:null, drafts:new Map(), request:0, calculation:0, timer:null, queue:[] };
const timestamp = value => value ? `${date(value.slice(0,10))} ${value.slice(11,19)}` : "—";
const quantity = value => value == null ? "—" : `${volume(value)} L`;
const sameCounter = (a,b) => a != null && b != null && Math.abs(a-b)<=0.020000001;
function offsetDay(value,days) {
  if(!/^\d{4}-\d{2}-\d{2}$/.test(value)) return "";
  const d=new Date(`${value}T12:00:00`);d.setDate(d.getDate()+days);
  return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,"0")}-${String(d.getDate()).padStart(2,"0")}`;
}
function clearBalance() {
  clearTimeout(conference.timer);++conference.calculation;
  if($("balance-result")) $("balance-result").innerHTML='<p class="muted">Informe as medições para conferir o resultado.</p>';
}
async function loadLmcDay() {
  const request=++conference.request;
  clearBalance();conference.day=null;conference.drafts.clear();
  $("review-queue").hidden=true;
  if(!state.branch || !$("book-date").value || !$("book-date").checkValidity()) {$("books-list").setAttribute("aria-busy","false");$("books-list").innerHTML='<p class="empty">Escolha uma data válida para o LMC.</p>';return;}
  $("books-list").setAttribute("aria-busy","true");
  $("books-list").innerHTML='<p class="empty">Conferindo entradas, bicos e tanques…</p>';
  try {
    const [day,queue]=await Promise.all([api(`/api/lmc/day?${new URLSearchParams({branch:state.branch,date:$("book-date").value})}`),api(`/api/review-queue?branch=${state.branch}`)]);
    if(request!==conference.request) return;
    conference.day=day;conference.queue=queue.items.filter(x=>x.reviewFrom<=day.date);
    if(!day.products.some(x=>x.id===conference.product)) conference.product=day.products[0]?.id;
    $("review-queue").hidden=!conference.queue.length;
    $("review-queue").innerHTML=conference.queue.length?`<div><strong>${conference.queue.length} correção(ões) de notas afetam a conferência deste dia</strong><p>Os livros ainda não foram recalculados. Abrir a conferência não encerra essa pendência.</p><details><summary>Ver alterações</summary>${conference.queue.map(x=>`<p>Documento ${x.documentId} · ${date(x.sourceDate)} → ${date(x.targetDate)} · revisar desde ${date(x.reviewFrom)}</p>`).join("")}</details></div>`:"";
    renderConference();
    $("updated").textContent=`LMC consultado às ${new Date().toLocaleTimeString("pt-BR",{hour:"2-digit",minute:"2-digit"})}`;
  } catch(error) {if(request===conference.request) $("books-list").innerHTML=`<div class="notice error" role="alert">${esc(error.message)} Use Atualizar para tentar novamente.</div>`;}
  finally {if(request===conference.request) $("books-list").setAttribute("aria-busy","false");}
}
function productDraft(id) {
  if(!conference.drafts.has(id)) conference.drafts.set(id,{});
  return conference.drafts.get(id);
}
function renderConference() {
  clearBalance();
  const d=conference.day,p=d?.products.find(x=>x.id===conference.product);
  if(!p) {$("books-list").innerHTML='<p class="empty">Nenhum combustível ou registro localizado nesta data.</p>';return;}
  const tanks=d.tanks.filter(x=>x.productId===p.id);
  const pumps=d.pumps.filter(x=>x.productId===p.id);
  const receipts=d.receipts.filter(x=>x.productId===p.id);
  const books=d.books.filter(x=>x.productId===p.id);
  const pending=conference.queue.some(x=>x.productIds.includes(p.id));
  const draft=productDraft(p.id);
  const alerts=[...d.alerts,...p.alerts];
  $("books-list").innerHTML=`
    <div class="fuel-tabs" role="group" aria-label="Combustível">${d.products.map(x=>`<button class="fuel-tab ${x.id===p.id?"selected":""}" data-product="${x.id}" aria-pressed="${x.id===p.id}"><span>${esc(x.name)}</span><small>${x.alerts.length?`${x.alerts.length} alerta(s)` : "Conferir medição"}</small></button>`).join("")}</div>
    <div class="day-heading"><div><h2>${esc(p.name)}</h2><p>${date(d.date)} · ${books.length?books.map(x=>`Livro ${x.number??"sem número"} · ID ${x.id}`).join(" / "):"LMC ainda não registrado"}</p></div><span class="badge ${pending||alerts.length?"amber":"neutral"}">${pending?"Revisão após correção":alerts.length?"Há pontos para conferir":"Aguardando medição"}</span></div>
    ${alerts.length?`<div class="notice warning day-alerts"><details open><summary>${alerts.length} ponto(s) para conferir</summary><ul>${alerts.map(x=>`<li>${esc(x)}</li>`).join("")}</ul></details></div>`:""}
    <div class="day-metrics"><article><span>Entradas atuais</span><strong>${quantity(p.receipts)}</strong><small>Pela data operacional</small></article><article><span>Vendas no livro</span><strong>${quantity(p.sales)}</strong><small>Conferência por bico abaixo</small></article><article><span>Tanques / bicos</span><strong>${tanks.length} <i>/</i> ${pumps.length}</strong><small>${books.length?"Vínculos registrados no LMC":"Conferir cadastro e automação"}</small></article></div>
    <section class="panel physical-panel"><div class="section-heading"><div><h3>Estoque físico e resultado</h3><p>A abertura é o fechamento físico de ${date(offsetDay(d.date,-1))}.</p></div><span class="draft-tag">Simulação</span></div>
    <div class="physical-grid">${tanks.map(t=>`<article class="tank-input"><div class="tank-title"><strong>Tanque ${t.tankId}</strong><small>Capacidade atual: ${quantity(t.capacity)}</small></div><label for="opening-${t.tankId}">Fechamento anterior · abertura (L)</label><input id="opening-${t.tankId}" data-tank="${t.tankId}" data-reading="previousClosing" type="number" min="0" max="1000000000" step="any" inputmode="decimal" placeholder="Informe a medição" value="${esc(draft[t.tankId]?.previousClosing??"")}"><small id="opening-origin-${t.tankId}" class="reading-origin">${esc(draft[t.tankId]?.previousClosingOrigin||"Informe o fechamento físico do dia anterior.")}</small><label for="closing-${t.tankId}">Fechamento de ${date(d.date)} (L)</label><input id="closing-${t.tankId}" data-tank="${t.tankId}" data-reading="closing" type="number" min="0" max="1000000000" step="any" inputmode="decimal" placeholder="Manual ou leitura conferida" value="${esc(draft[t.tankId]?.closing??"")}"><small id="closing-origin-${t.tankId}" class="reading-origin">${esc(draft[t.tankId]?.closingOrigin||"Nenhuma leitura escolhida.")}</small></article>`).join("")||'<p class="muted">Tanques não localizados.</p>'}</div>
    <div id="balance-result" class="balance-result" role="status" aria-live="polite"></div>
    <details class="reading-details"><summary>Consultar leituras próximas e valores do livro</summary><p>As fontes têm diferenças de data. Confira o dia da medição antes de usar. A origem manual ou eletrônica ainda não está identificada nestes registros.</p>
    <div class="lmc-scroll"><table><thead><tr><th>Tanque</th><th>Data na fonte</th><th>Fonte</th><th class="numeric">Volume</th><th>Usar na simulação</th></tr></thead><tbody>${d.readings.map((r,index)=>({r,index})).filter(({r})=>tanks.some(t=>t.tankId===r.tankId)).sort((a,b)=>a.r.tankId-b.r.tankId||a.r.at.localeCompare(b.r.at)).map(({r,index})=>`<tr><td>${r.tankId}</td><td>${timestamp(r.at)}</td><td><code>${esc(r.source)}</code><small class="liters">Registro ${r.id}</small></td><td class="numeric">${quantity(r.volume)}</td><td>${r.volume!=null&&r.volume>=0?`<div class="reading-buttons"><button class="button review-button" data-source="${index}" data-as="previousClosing">Anterior</button><button class="button review-button" data-source="${index}" data-as="closing">Fechamento</button></div>`:"Conferir valor"}</td></tr>`).join("")||'<tr><td colspan="5" class="empty">Sem leituras próximas nesta fonte de dados.</td></tr>'}</tbody></table></div>
    <p>Valores existentes no livro, ainda sem interpretação homologada como medição física:</p><div class="legacy-stocks">${tanks.map(t=>`<span>Tanque ${t.tankId} · QTDETANQUE ${quantity(t.legacyOpening)} · QTDECONCILIACAO ${quantity(t.legacyClosing)}</span>`).join("")}</div></details>
    <p class="simulation-note">Esta prévia não grava o fechamento. Ao atualizar ou mudar o dia, as medições digitadas são descartadas.</p></section>
    <section class="panel conference-section"><div class="section-heading"><div><h3>Entradas do dia</h3><p>Emissão e entrada preservadas. O LMC segue a data operacional.</p></div><span class="badge neutral">${receipts.length} parcela(s)</span></div>${renderDayReceipts(receipts,p.id)}</section>
    <section class="panel conference-section"><div class="section-heading"><div><h3>Encerrantes da automação</h3><p>Último registro de cada bico entre 00h e 24h. Sem somar novamente as vendas da automação.</p></div></div>${renderDayPumps(pumps,p.id)}</section>
    <p class="source-note">A conferência ajuda a localizar divergências. A gravação, o recálculo e a emissão do LMC ainda não estão disponíveis nesta tela.</p>`;
  scheduleBalance();
}
function renderDayReceipts(receipts,productId) {
  const d=conference.day,saved=d.savedReceipts.filter(x=>x.productId===productId);
  return `<div class="lmc-scroll"><table><thead><tr><th>Nota / fornecedor</th><th>Emissão / entrada</th><th>Data do LMC</th><th>Tanque</th><th class="numeric">Volume</th><th>Conferência</th></tr></thead><tbody>${receipts.map(r=>`<tr><td><strong>NF ${r.number??"sem número"}</strong><small class="liters">${esc(r.supplier)}</small></td><td>${date(r.documentDate)}<small class="liters">Entrada ${date(r.entryDate)}</small></td><td>${date(r.operationalDate)}${r.parcelDate!==r.operationalDate?`<small class="negative">Parcela: ${date(r.parcelDate)}</small>`:""}</td><td>${r.tankId}</td><td class="numeric">${quantity(r.volume)}</td><td><span class="badge ${r.canceled||!r.usable?"amber":"neutral"}">${r.canceled?"Cancelada · excluída":r.usable?"Incluída no cálculo":"Revisar vínculos"}</span>${r.documentId?`<button class="button review-button" data-note="${d.branch}/${r.databaseId}/${r.documentId}">Revisar nota</button>`:""}</td></tr>`).join("")||'<tr><td colspan="6" class="empty">Nenhuma entrada atual atribuída a este combustível neste dia.</td></tr>'}</tbody></table></div>
    <details class="reading-details"><summary>Recebimentos já gravados no LMC (${saved.length})</summary><p>Referência para comparação. O livro não guarda a chave completa da nota; número e volume não comprovam sozinhos o vínculo.</p><div class="lmc-scroll"><table><thead><tr><th>Nota</th><th>Tanque</th><th>Data gravada</th><th class="numeric">Volume</th></tr></thead><tbody>${saved.map(r=>`<tr><td>${r.number??"—"}</td><td>${r.tankId}</td><td>${date(r.date)}</td><td class="numeric">${quantity(r.volume)}</td></tr>`).join("")||'<tr><td colspan="4" class="empty">Sem recebimentos gravados.</td></tr>'}</tbody></table></div></details>`;
}
function renderDayPumps(pumps,productId) {
  const d=conference.day;
  const extra=d.archive.filter(a=>a.productId===productId&&!d.pumps.some(p=>p.pumpId===a.pumpId));
  return `<div class="lmc-scroll"><table><thead><tr><th>Bico / tanque</th><th class="numeric">Abertura LMC</th><th class="numeric">Fechamento automação</th><th class="numeric">Fechamento LMC</th><th class="numeric">Aferição</th><th class="numeric">Vendas LMC</th><th>Conferência</th></tr></thead><tbody>${[...pumps,...extra.map(a=>({pumpId:a.pumpId,tankId:a.tankId}))].map(p=>{
    const a=d.archive.find(x=>x.pumpId===p.pumpId),ok=a&&!a.ambiguous&&sameCounter(a.opening,p.opening)&&sameCounter(a.closing,p.closing);
    return `<tr><td><strong>Bico ${p.pumpId}</strong><small class="liters">Tanque ${p.tankId??"—"}</small></td><td class="numeric">${volume(p.opening)}</td><td class="numeric">${a?.ambiguous?"Ambíguo":volume(a?.closing)}<small class="liters">${a?timestamp(a.lastAt):"Sem registro no dia"}</small></td><td class="numeric">${volume(p.closing)}</td><td class="numeric">${quantity(p.calibration)}</td><td class="numeric">${quantity(p.sales)}</td><td><span class="badge ${ok?"green":"amber"}">${ok?"Encerrantes conferem":"Conferir bico"}</span><small class="liters">${a?`Abertura arquivo: ${volume(a.opening)}`:"Sem presumir venda zero"}</small></td></tr>`;
  }).join("")||'<tr><td colspan="7" class="empty">Nenhum bico localizado.</td></tr>'}</tbody></table></div><p class="simulation-note">Fonte: CONSOLEARQUIVO. A comparação usa 0,02 L apenas para diferenças numéricas dos campos. Viradas e trocas de contador exigem conferência.</p>`;
}
function readPhysicalInputs() {
  const p=conference.product,draft=productDraft(p);
  return conference.day.tanks.filter(x=>x.productId===p).map(t=>({tankId:t.tankId,
    previousClosing:draft[t.tankId]?.previousClosing==null||draft[t.tankId].previousClosing===""?null:Number(draft[t.tankId].previousClosing),
    closing:draft[t.tankId]?.closing==null||draft[t.tankId].closing===""?null:Number(draft[t.tankId].closing)}));
}
function scheduleBalance() {
  clearBalance();
  if(!conference.day || !$("balance-result")) return;
  const d=conference.day,p=d.products.find(x=>x.id===conference.product),request=conference.calculation;
  if(!p?.canCalculate) {$("balance-result").innerHTML='<p class="muted">Há pendências de entradas, livros ou bicos. Confira os alertas antes de calcular o balanço.</p>';return;}
  const tanks=readPhysicalInputs();
  if(tanks.some(x=>[x.previousClosing,x.closing].some(v=>v!=null&&(!Number.isFinite(v)||v<0||v>1000000000)))) {$("balance-result").innerHTML='<p class="negative">Confira os valores das medições. Use números positivos ou zero.</p>';return;}
  if(tanks.some(x=>x.previousClosing==null||x.closing==null)) {$("balance-result").innerHTML='<p class="muted">Aguardando o fechamento anterior e o atual de todos os tanques. Um campo vazio não significa zero.</p>';return;}
  $("balance-result").innerHTML='<p class="muted">Conferindo o balanço…</p>';
  conference.timer=setTimeout(async()=>{
    try {
      const result=await api('/api/lmc/balance',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({branch:d.branch,date:d.date,productId:p.id,tanks})});
      if(request!==conference.calculation || !$("balance-result")) return;
      const signed=result.difference==null?"—":`${result.difference>0?"+":""}${quantity(result.difference)}`;
      $("balance-result").innerHTML=`<div class="balance-values"><div><small>Escritural calculado</small><strong>${quantity(result.bookStock)}</strong><span>Abertura + entradas − vendas</span></div><div><small>Fechamento físico informado</small><strong>${quantity(result.physical)}</strong><span>Soma das medições dos tanques</span></div><div class="${result.aboveReference?"negative":""}"><small>${result.difference<0?"Perda":result.difference>0?"Sobra":"Diferença"}</small><strong>${signed}</strong><span>${result.percent==null?"Percentual indisponível":`${volume(result.percent)}% do estoque escritural`}</span></div></div>
      <p class="balance-status ${result.aboveReference?"negative":""}">${result.aboveReference===true?"Acima da referência de 0,6% — apurar a causa.":result.aboveReference===false?"Dentro da referência numérica de 0,6%. Confira a origem das medições.":"Sem classificação percentual."}</p>
      ${result.alerts.length?`<ul class="balance-alerts">${result.alerts.map(x=>`<li>${esc(x)}</li>`).join("")}</ul>`:""}
      <details class="reference-details"><summary>Critério e faixa de referência</summary><p>Diferença = físico − escritural. Percentual = diferença ÷ estoque escritural × 100. Estoque escritural zero não recebe classificação. Referência operacional interpretada a partir do art. 5º da <a href="https://ceaf.mpba.mp.br/wp-content/uploads/2022/10/Resolucao-884-2022-da-ANP-Agencia-Nacional-do-Petroleo-Gas-Natural-e-Biocombustiveis-BR.pdf" target="_blank" rel="noopener noreferrer">Resolução ANP 884/2022</a>, sujeita à homologação para emissão.</p>${result.referenceMin!=null?`<p>Para este saldo escritural, a faixa matemática é ${quantity(result.referenceMin)} a ${quantity(result.referenceMax)}. Serve para análise; mantenha o valor efetivamente medido.</p>`:""}</details>`;
    } catch(error) {if(request===conference.calculation&&$("balance-result")) $("balance-result").innerHTML=`<p class="negative">${esc(error.message)}</p>`;}
  },300);
}
$("books-list").addEventListener("input",event=>{
  const input=event.target;
  if(!input.dataset.reading) return;
  const tank=input.dataset.tank,field=input.dataset.reading,draft=productDraft(conference.product);
  draft[tank]??={};draft[tank][field]=input.validity.badInput?"invalid":input.value;draft[tank][`${field}Origin`]="Informado manualmente nesta simulação.";
  $(`${field==="closing"?"closing":"opening"}-origin-${tank}`).textContent=draft[tank][`${field}Origin`];
  scheduleBalance();
});
$("books-list").addEventListener("click",event=>{
  const product=event.target.closest('[data-product]');
  if(product) {conference.product=Number(product.dataset.product);renderConference();return;}
  const note=event.target.closest('[data-note]');
  if(note) {openReview(note.dataset.note);return;}
  const source=event.target.closest('[data-source]');
  if(source) {
    const r=conference.day.readings[Number(source.dataset.source)],field=source.dataset.as,draft=productDraft(conference.product);
    const expected=field==="closing"?conference.day.date:offsetDay(conference.day.date,-1);
    draft[r.tankId]??={};draft[r.tankId][field]=String(r.volume);
    draft[r.tankId][`${field}Origin`]=`${r.source} · ${timestamp(r.at)}${r.at.slice(0,10)!==expected?" · Data diferente: confira a correspondência antes de usar.":" · Confira se corresponde ao fechamento."}`;
    const prefix=field==="closing"?"closing":"opening";
    $(`${prefix}-${r.tankId}`).value=r.volume;$(`${prefix}-origin-${r.tankId}`).textContent=draft[r.tankId][`${field}Origin`];
    scheduleBalance();
  }
});
for(const [id,step] of [["day-before",-1],["day-after",1]]) $(id).addEventListener("click",()=>{
  const next=offsetDay($("book-date").value,step);
  if(next && next>="1900-01-01" && next<="2078-12-31") {$("book-date").value=next;loadBooks();}
});
$("book-date").addEventListener("change",loadBooks);
$("review-dialog").addEventListener("close",()=>{if(!$("books-view").hidden)loadBooks();});
