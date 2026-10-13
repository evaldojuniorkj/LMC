"use strict";
const bookState={rows:[],queue:[],selectedIds:new Set(),newEntry:false,listRequest:0,editorRequest:0,previewRequest:0,timer:null,preparation:null,day:null,preview:null,
  operationId:null,requestBody:null,saving:false,uncertain:false,saveAndNext:false,nextBook:null,counterSources:{}};
const money=new Intl.NumberFormat("pt-BR",{style:"currency",currency:"BRL"});
const bookVolume=value=>value==null||!Number.isFinite(value)?"—":number.format(Math.abs(value)<0.5?0:value);
const bookDecimal=value=>value==null?"—":volume(value);
const bookCounterFormat=new Intl.NumberFormat("pt-BR",{minimumFractionDigits:2,maximumFractionDigits:2});
const bookCounter=value=>value==null||!Number.isFinite(value)?"—":bookCounterFormat.format(value);
const bookReadingUsable=r=>r?.invalidVolume==null&&r?.canUse!==false&&Number.isFinite(r?.volume)&&r.volume>=0&&r.volume<=1000000000;
function bookReadingVolume(r) {
  return r.invalidVolume!=null?`<strong class="negative">Leitura inválida</strong><small class="liters">Valor original: ${esc(r.invalidVolume)}</small>`:bookVolume(r.volume);
}
function bookMeasurementMethod() {return $("measurement-electronic").checked?"eletronica":$("measurement-manual").checked?"manual":"";}
function setBookMeasurementMethod(value) {
  $("measurement-electronic").checked=value==="eletronica";
  $("measurement-manual").checked=value!=="eletronica";
}
function bookMeasurementInput(t,field,value) {
  const label=field==="opening"?"Abertura":"Fechamento";
  return `<div class="book-volume-field"><input id="book-${field}-${t.tankId}" data-measurement="true" type="number" inputmode="decimal" step="0.0001" min="0" max="${t.capacity||1000000000}" aria-label="${label} tanque ${t.tankId}" value="${value??""}" ${field==="opening"&&t.inherited?"readonly":""} placeholder="${field==="opening"?"Conferir":"Medição real"}"><span id="book-${field}-display-${t.tankId}" aria-hidden="true">${value==null?"":bookVolume(value)}</span></div>`;
}
function bookDateOffset(value,days) {
  const d=new Date(`${value}T12:00:00`);d.setDate(d.getDate()+days);
  return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,"0")}-${String(d.getDate()).padStart(2,"0")}`;
}
function bookPumpInput(b,field) {
  const label=field==="closing"?"Encerrante final":"Aferição";
  return `<div class="book-volume-field pump-volume-field"><input id="book-pump-${field}-${b.pumpId}" data-pump-edit="true" type="number" inputmode="decimal" step="0.0001" min="0" max="1000000000" aria-label="${label} bico ${b.pumpId}" value="${b[field]}"><span id="book-pump-${field}-display-${b.pumpId}" aria-hidden="true">${field==="closing"?bookCounter(b[field]):bookVolume(b[field])}</span></div>`;
}
function readBookNumber(id) {const e=$(id);return e.validity.badInput?NaN:e.value===""?null:Number(e.value);}
function bookPumpCutoff(b) {
  const c=b.cutoff;if(!c)return "";
  if(c.automationAmbiguous)return `<div class="pump-conference-item"><fieldset class="pump-cutoff"><legend>Bico ${b.pumpId}: automação com registros conflitantes</legend><p>Abertura mantida em ${bookCounter(b.opening)} L. Confira ou altere a origem do fechamento.</p><div class="pump-cutoff-options">${(c.automationCandidates||[]).map((value,index)=>`<label><input type="radio" name="book-pump-source-${b.pumpId}" data-pump-source="${b.pumpId}" value="automation:${index}" ${value===Math.max(...c.automationCandidates)?"checked":""}><span><strong>Automação</strong><span>${bookCounter(value)} L</span></span></label>`).join("")}<label><input type="radio" name="book-pump-source-${b.pumpId}" data-pump-source="${b.pumpId}" value="shifts"><span><strong>Turnos do dia</strong><span>Fechamento: ${bookCounter(c.shiftClosing)} L</span></span></label></div><p id="book-pump-source-status-${b.pumpId}" class="pump-cutoff-status" role="status">Maior encerrante da automação selecionado por padrão.</p></fieldset></div>`;
  const at=value=>`${date(value.slice(0,10))} ${value.slice(11,19)}`;
  return `<div class="pump-conference-item"><fieldset class="pump-cutoff"><legend>Bico ${b.pumpId}: diferença no fechamento</legend><p><strong>Abertura fixa: ${bookCounter(b.opening)} L.</strong> ${esc(b.openingSource??"Abertura já conferida")}. A escolha abaixo altera somente o fechamento.</p><div class="pump-cutoff-options">
    ${[["automation","Automação",c.automationClosing],["shifts","Turnos do dia",c.shiftClosing]].map(([source,label,closing])=>`<label><input type="radio" name="book-pump-source-${b.pumpId}" data-pump-source="${b.pumpId}" value="${source}" ${bookState.counterSources[b.pumpId]===source?"checked":""}><span><strong>${label}</strong><span>Fechamento: ${bookCounter(closing)} L</span></span></label>`).join("")}</div>
    <p class="pump-cutoff-difference">Diferença entre os fechamentos (turnos − automação): ${bookCounter(c.shiftClosing-c.automationClosing)} L.</p><small>Registros da automação: ${esc(at(c.firstAt))} até ${esc(at(c.lastAt))}. Aferição e preço vêm dos turnos e podem ser conferidos na grade.</small><p id="book-pump-source-status-${b.pumpId}" class="pump-cutoff-status" role="status">Fechamento da automação selecionado por padrão. Se necessário, escolha Turnos do dia.</p></fieldset></div>`;
}
function selectBookPumpSource(pumpId,source) {
  const candidateIndex=source.startsWith("automation:")?Number(source.split(":")[1]):null;
  if(candidateIndex!==null)source="automation";
  if(bookState.saving||bookState.uncertain||!["automation","shifts"].includes(source))return;
  const b=bookState.preparation?.pumps.find(b=>b.pumpId===pumpId);if(!b?.cutoff)return;
  const candidate=candidateIndex===null?null:b.cutoff.automationCandidates?.[candidateIndex];
  if(b.cutoff.automationAmbiguous&&source==="automation"&&!Number.isFinite(candidate))return;
  bookState.counterSources[pumpId]=source;
  $(`book-pump-closing-${pumpId}`).value=source==="shifts"?b.cutoff.shiftClosing:candidate??b.cutoff.automationClosing;
  $(`book-pump-source-status-${pumpId}`).textContent=`Fechamento ${source==="shifts"?"dos turnos":"da automação"} selecionado. Abertura mantida em ${bookCounter(b.opening)} L. A escolha será registrada ao salvar.`;
  scheduleBookPreview();
}
function currentBookPumps(inputs=null) {
  return bookState.preparation.pumps.map(b=>{
    const edit=inputs?.find(x=>x.pumpId===b.pumpId)??{closing:readBookNumber(`book-pump-closing-${b.pumpId}`),calibration:readBookNumber(`book-pump-calibration-${b.pumpId}`),counterSource:bookState.counterSources[b.pumpId]};
    const confirmed=!b.cutoff||["automation","shifts"].includes(edit.counterSource);
    const opening=b.opening;
    const valid=confirmed&&[edit.closing,edit.calibration].every(v=>v!=null&&Number.isFinite(v)&&v>=0&&v<=1000000000)
      &&edit.closing>=opening&&edit.calibration<=edit.closing-opening;
    const negativeSales=[edit.closing,edit.calibration].every(v=>v!=null&&Number.isFinite(v))&&edit.closing-opening-edit.calibration<0;
    return {...b,...edit,opening,negativeSales,sales:valid?Math.round((edit.closing-opening-edit.calibration)*10000)/10000:NaN};
  });
}
function monthRange(value) {
  const first=`${value}-01`,d=new Date(`${first}T12:00:00`);d.setMonth(d.getMonth()+1);
  return {from:first,until:bookDateOffset(`${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,"0")}-01`,-1)};
}
function bookPending(row) {
  return bookState.queue.some(h=>h.productIds.includes(row.productId)&&h.reviewFrom<=row.date&&(!row.lastSavedAt||new Date(h.appliedAt)>new Date(row.lastSavedAt)));
}
function invalidateBookPreview() {
  clearTimeout(bookState.timer);++bookState.previewRequest;bookState.preview=null;bookState.operationId=null;bookState.requestBody=null;
  bookState.saveAndNext=false;
  $("save-book").disabled=true;$("save-next-book").disabled=true;$("reload-book").hidden=true;$("reload-book").textContent="Atualizar conferência";
}
async function loadBookLedger() {
  if(bookState.saving||bookState.uncertain) return;
  invalidateBookPreview();resetClosingReviews();++bookState.editorRequest;bookState.preparation=null;bookState.nextBook=null;clearBookSelection();
  $("book-list-pane").hidden=false;$("book-editor-pane").hidden=true;$("branch").disabled=false;
  const request=++bookState.listRequest;
  if(!state.branch || !$("book-month").value || !$("book-month").checkValidity()) return;
  $("books-list").setAttribute("aria-busy","true");$("books-list").innerHTML='<p class="empty">Consultando os LMCs do mês…</p>';
  const range=monthRange($("book-month").value);
  try {
    const [rows,queue]=await Promise.all([api(`/api/lmc/ledger?${new URLSearchParams({branch:state.branch,...range})}`),api(`/api/review-queue?branch=${state.branch}`)]);
    if(request!==bookState.listRequest)return;
    bookState.rows=rows;bookState.queue=queue.items;
    const selected=$("ledger-product").value,products=[...new Map(rows.map(x=>[x.productId,x.product])).entries()];
    $("ledger-product").innerHTML='<option value="">Todos os combustíveis</option>'+products.map(([id,name])=>`<option value="${id}">${esc(name)}</option>`).join("");
    if(products.some(([id])=>String(id)===selected))$("ledger-product").value=selected;
    renderBookLedger();
  } catch(error) {if(request===bookState.listRequest)$("books-list").innerHTML=`<p class="notice error" role="alert">${esc(error.message)}</p>`;}
  finally {if(request===bookState.listRequest)$("books-list").setAttribute("aria-busy","false");}
}
function visibleBookRows() {return bookState.rows.filter(x=>!$("ledger-product").value||x.productId===Number($("ledger-product").value));}
function updateBookSelection() {
  const count=bookState.selectedIds.size,rows=visibleBookRows(),all=$("select-all-books");
  $("book-selected-count").textContent=count?`${count} LMC(s) selecionado(s)`:"Nenhum LMC selecionado";
  $("delete-selected-books").disabled=count===0;$("clear-book-selection").disabled=count===0;
  if(all){all.checked=count>0&&count===rows.length;all.indeterminate=count>0&&count<rows.length;all.disabled=rows.length===0;}
}
function clearBookSelection() {bookState.selectedIds.clear();updateBookSelection();}
function renderBookLedger() {
  const rows=bookState.rows.filter(x=>!$("ledger-product").value||x.productId===Number($("ledger-product").value));
  $("books-list").innerHTML=`<div class="ledger-scroll"><table class="ledger-table"><thead><tr><th class="book-check-column"><input type="checkbox" id="select-all-books" aria-label="Selecionar todos os LMCs desta lista"></th><th>Combustível</th><th>Folha</th><th>Data</th><th class="numeric">Abertura</th><th class="numeric">Entradas</th><th class="numeric">Saídas</th><th class="numeric">Escritural</th><th class="numeric">Fechamento</th><th class="numeric">Perdas / sobras</th><th class="numeric">%</th><th>Situação</th><th></th></tr></thead><tbody>${rows.map(r=>`<tr><td class="book-check-column"><input type="checkbox" data-select-book="${r.id}" aria-label="Selecionar LMC de ${date(r.date)}, ${esc(r.product)}" ${bookState.selectedIds.has(r.id)?"checked":""}></td><td><strong>${esc(r.product)}</strong></td><td>${r.number??"—"}</td><td>${date(r.date)}</td><td class="numeric">${bookVolume(r.opening)}</td><td class="numeric">${bookVolume(r.received)}</td><td class="numeric">${bookVolume(r.sales)}</td><td class="numeric ${r.bookStock<0?"negative":""}">${bookVolume(r.bookStock)}</td><td class="numeric ${r.closing<0?"negative":""}">${bookVolume(r.closing)}</td><td class="numeric">${bookVolume(r.difference)}</td><td class="numeric">${bookDecimal(r.percent)}</td><td><span class="badge ${bookPending(r)?"amber":"neutral"}">${bookPending(r)?"Revisar entradas":"Salvo"}</span></td><td><div class="ledger-actions"><button class="button review-button" data-open-book="${r.id}">Abrir</button><button class="button danger-outline" data-delete-book="${r.id}" aria-label="Excluir LMC de ${date(r.date)}, ${esc(r.product)}">Excluir</button></div></td></tr>`).join("")||'<tr><td colspan="13" class="empty">Nenhum livro neste período. Use Inserir LMC para cadastrar um dia.</td></tr>'}</tbody></table></div><div class="table-footer"><span>${rows.length} livro(s) · volumes em litros inteiros, arredondados</span><span>Percentual sobre o estoque escritural</span></div>`;
  updateBookSelection();
}
function lockBookUi(locked) {
  for(const id of ["branch","nav-notes","nav-books","nav-pending","nav-tables","mobile-view","back-notes","close-book","book-date","editor-product","measurement-method","delete-book"]) $(id).disabled=locked;
  document.querySelectorAll('#book-editor-body input,#book-editor-body textarea,#book-editor-body button').forEach(e=>e.disabled=locked);
  if(!locked && bookState.preparation) for(const t of bookState.preparation.tanks) if(t.inherited && $("book-opening-"+t.tankId))$("book-opening-"+t.tankId).readOnly=true;
  $("branch").disabled=!$("book-editor-pane").hidden;
  if(locked){$("save-book").disabled=true;$("save-next-book").disabled=true;}
}
async function openNewBook(product=null,savedMessage="") {
  if(bookState.saving||bookState.uncertain)return;
  invalidateBookPreview();bookState.preparation=null;$("delete-book").hidden=true;
  const request=++bookState.editorRequest,branch=state.branch;
  bookState.nextBook={product,savedMessage};
  $("new-book").disabled=true;showError("book-feedback",savedMessage);
  $("book-save-state").textContent="Consultando o próximo LMC…";
  try {
    const query=new URLSearchParams({branch,fallback:`${$("book-month").value}-01`});
    if(product)query.set("product",product);
    const next=await api(`/api/lmc/next?${query}`);
    if(request!==bookState.editorRequest||branch!==state.branch)return;
    $("book-month").value=next.date.slice(0,7);
    await openBookEditor(next.date,next.productId,true);
  } catch(error) {if(request===bookState.editorRequest){
    showError("book-feedback",[savedMessage,"Não foi possível abrir o próximo LMC.",error.message].filter(Boolean).join(" "));
    $("book-save-state").textContent=savedMessage?"O LMC foi salvo. Tente abrir o próximo novamente.":"Não foi possível consultar o próximo LMC.";
    $("reload-book").hidden=false;$("reload-book").textContent="Tentar abrir próximo";
  }}
  finally {$("new-book").disabled=false;}
}
async function openBookEditor(day,product,newEntry=false) {
  if(bookState.saving||bookState.uncertain)return;
  invalidateBookPreview();resetClosingReviews();const request=++bookState.editorRequest;bookState.preparation=null;bookState.newEntry=newEntry;bookState.nextBook=null;
  $("book-list-pane").hidden=true;$("book-editor-pane").hidden=false;$("branch").disabled=true;$("book-date").value=day;
  $("book-editor-body").innerHTML='<p class="empty">Conferindo as fontes do livro…</p>';
  $("book-save-state").textContent="Carregando dados…";$("book-number").value="";setBookMeasurementMethod("manual");
  $("editor-product").disabled=true;$("save-book").textContent="Salvar LMC";$("delete-book").hidden=true;
  try {
    if(!day||!$("book-date").checkValidity())throw new Error("Informe uma data válida para o movimento.");
    const d=await api(`/api/lmc/day?${new URLSearchParams({branch:state.branch,date:day})}`);
    if(request!==bookState.editorRequest)return;
    if(!d.products.length)throw new Error("Nenhum combustível localizado nesta data.");
    bookState.day=d;product=d.products.some(p=>p.id===Number(product))?Number(product):d.products[0].id;
    $("editor-product").innerHTML=d.products.map(p=>`<option value="${p.id}">${esc(p.name)}</option>`).join("");$("editor-product").value=product;$("editor-product").disabled=false;
    const p=await api(`/api/lmc/prepare?${new URLSearchParams({branch:state.branch,date:day,product})}`);
    if(request!==bookState.editorRequest)return;
    bookState.preparation=p;$("book-number").value=p.number;setBookMeasurementMethod(p.measurementMethod);
    $("save-book").textContent=p.bookId?"Salvar LMC refeito":"Inserir LMC";$("delete-book").hidden=!p.bookId;
    void loadBookTankTables(state.branch,p.productId,request);
    renderBookEditor();scheduleBookPreview();
  } catch(error) {if(request===bookState.editorRequest){$("book-editor-body").innerHTML=`<p class="notice error" role="alert">${esc(error.message)}</p>`;$("book-save-state").textContent="Consulta não concluída.";$("reload-book").hidden=false;}}
}
function initialBookVolumes(p,t) {
  const saved=p.savedMeasurements?.find(x=>x.tankId===t.tankId);
  return {opening:p.bookId?saved?.previousClosing??null:t.inherited?t.previousClosing:saved?.previousClosing??null,
    closing:saved?.closing??null};
}
function usePreviousBookClosing(tankId) {
  if(bookState.saving||bookState.uncertain)return;
  const t=bookState.preparation?.tanks.find(t=>t.tankId===tankId);
  if(!t?.inherited||t.previousClosing==null)return;
  $("book-opening-"+tankId).value=t.previousClosing;
  $("book-opening-source-"+tankId).textContent="Fechamento do dia anterior aplicado";
  $("book-inherit-opening-"+tankId).hidden=true;
  scheduleBookPreview();
}
function renderBookEditor() {
  const p=bookState.preparation,d=bookState.day;
  bookState.counterSources=Object.fromEntries(p.pumps.filter(b=>b.cutoff).map(b=>[b.pumpId,"automation"]));
  p.pumps.filter(b=>b.cutoff?.automationAmbiguous&&b.cutoff.automationCandidates?.length).forEach(b=>{b.closing=Math.max(...b.cutoff.automationCandidates);});
  const warnings=[...p.blockers,...p.warnings];
  $("book-editor-body").innerHTML=`<div class="book-intro"><span class="badge ${p.bookId?"amber":"green"}">${p.bookId?`Refazer livro ${p.bookId}`:"Novo LMC"}</span><span>${p.bookId?"Abertura e fechamento carregados dos valores salvos neste LMC.":`Abertura = fechamento físico de ${date(bookDateOffset(p.date,-1))}. Os encerrantes vêm da automação existente.`}</span></div>
    <details class="book-warnings" id="book-alerts"><summary>Alertas e conferência${p.blockers.length?` · ${p.blockers.length} impedimento(s)`:warnings.length?` · ${warnings.length} aviso(s)`:""}</summary><ul>${warnings.map(w=>`<li>${esc(w)}</li>`).join("")}</ul>${p.pumps.map(bookPumpCutoff).join("")}${p.tanks.map(t=>`<div id="closing-review-row-${t.tankId}" hidden><div id="closing-review-${t.tankId}" class="closing-review" role="status"></div></div>`).join("")}<div id="book-preview-alerts" class="book-inline-note" role="status"></div></details>
    <div class="book-form-grid"><section class="book-block"><h3>Medidas dos tanques <small>litros</small></h3><div class="book-table-scroll"><table class="tank-edit-table"><thead><tr><th>Tanque</th><th>Abertura</th><th class="numeric">Escritural</th><th class="numeric tank-table-heading">Tabela próxima<small>do tanque</small></th><th>Fechamento</th><th class="numeric">Perdas / sobras</th><th class="numeric">%</th></tr></thead><tbody>${p.tanks.map(t=>{
      const values=initialBookVolumes(p,t),differentPrevious=t.inherited&&values.opening!==t.previousClosing;
      return `<tr><td><strong>${t.tankId}</strong><small class="liters">Cap. ${bookVolume(t.capacity)}</small></td><td>${bookMeasurementInput(t,"opening",values.opening)}<small class="liters" id="book-opening-source-${t.tankId}">${p.bookId?"Abertura salva neste LMC":t.inherited?"Herdada do dia anterior":"Fechamento anterior"}</small>${differentPrevious?`<button id="book-inherit-opening-${t.tankId}" class="button review-button" type="button" data-inherit-opening="${t.tankId}">Usar fechamento anterior: ${bookVolume(t.previousClosing)} L</button>`:""}</td><td class="numeric" id="tank-balance-${t.tankId}">—</td><td class="numeric tank-table-reference" id="tank-table-${t.tankId}">—</td><td>${bookMeasurementInput(t,"closing",values.closing)}${p.bookId?'<small class="liters">Fechamento salvo neste LMC</small>':""}</td><td class="numeric" id="tank-difference-${t.tankId}">—</td><td class="numeric" id="tank-percent-${t.tankId}">—</td></tr>`;
    }).join("")}</tbody></table></div><p class="tank-table-note">Referência da tabela vinculada a cada tanque: volume mais próximo, altura em cm e diferença para o escritural. Configure os CSVs no menu Tabelas dos tanques. Informe o fechamento medido no tanque.</p></section>
    <section class="book-block"><h3>Volume recebido no dia <small>${p.documents.length} parcela(s)</small></h3><div class="book-table-scroll"><table><thead><tr><th>Nota fiscal</th><th>Emissão</th><th>Tanque</th><th class="numeric">Recebido (L)</th><th></th></tr></thead><tbody>${p.documents.map(r=>`<tr><td>${r.number}</td><td>${date(r.documentDate)}</td><td>${r.tankId}</td><td class="numeric">${bookVolume(r.volume)}</td><td><button class="button review-button" data-book-note="${p.branch}/${r.databaseId}/${r.documentId}">Revisar</button></td></tr>`).join("")||'<tr><td colspan="5" class="compact-empty">Sem entradas válidas neste dia.</td></tr>'}</tbody></table></div></section>
    <section class="book-block"><h3>Volume vendido no dia <small>encerrantes e aferições</small></h3><div class="book-table-scroll pump-grid"><table><thead><tr><th>Tanque</th><th>Bico</th><th class="numeric">Inicial</th><th>Final · editar</th><th>Aferição · editar</th><th class="numeric">Vendas (L)</th><th class="numeric">Preço (R$)</th></tr></thead><tbody>${p.pumps.map(b=>`<tr><td>${b.tankId}</td><td title="${esc(b.source)}">${b.pumpId}</td><td class="numeric"><span id="book-pump-opening-${b.pumpId}" title="${esc(b.openingSource||"Abertura conferida")}">${bookCounter(b.opening)}</span>${b.openingSource?`<small class="pump-opening-source sr-only">${esc(b.openingSource)}</small>`:""}</td><td>${bookPumpInput(b,"closing")}</td><td>${bookPumpInput(b,"calibration")}</td><td class="numeric" id="book-pump-sales-${b.pumpId}">${bookVolume(b.sales)}</td><td class="numeric">${bookDecimal(b.price)}</td></tr>`).join("")||'<tr><td colspan="7" class="compact-empty">Confira as pendências dos encerrantes.</td></tr>'}</tbody></table></div></section>
    <div class="book-totals">${[["opening","Estoque de abertura"],["received","Total recebido"],["available","Total disponível"],["sales","Litros vendidos no dia"],["day-value","Valor do dia"],["month-sales","Litros registrados no mês"],["month-value","Valor registrado no mês"],["stock","Estoque escritural"],["difference","Perdas / sobras"],["closing","Estoque de fechamento"]].map(([id,label])=>`<div><span>${label}</span><strong id="total-${id}">—</strong></div>`).join("")}</div>
    <section class="book-block book-side"><div><h3>Observações</h3><textarea id="book-observation" rows="3" maxlength="2000" placeholder="Justificativa de variação, quando necessária; demais observações são opcionais.">${esc(p.observation)}</textarea></div><details class="book-readings"><summary>Leituras disponíveis por tanque</summary><p>Confira a data da fonte antes de atribuí-la ao fechamento. A escolha não altera a leitura original.</p><div class="book-table-scroll"><table><thead><tr><th>Tanque / fonte</th><th>Data</th><th class="numeric">Volume (L)</th><th>Usar em</th></tr></thead><tbody>${d.readings.map((r,i)=>({r,i})).filter(({r})=>p.tanks.some(t=>t.tankId===r.tankId)).sort((a,b)=>a.r.tankId-b.r.tankId||a.r.at.localeCompare(b.r.at)).map(({r,i})=>`<tr><td>${r.tankId}<small class="liters">${esc(r.source)}</small></td><td>${date(r.at.slice(0,10))}<small class="liters">${r.at.slice(11,19)}</small></td><td class="numeric">${bookReadingVolume(r)}</td><td>${bookReadingUsable(r)?`<button class="button review-button" data-book-reading="${i}" data-field="opening" ${p.tanks.find(t=>t.tankId===r.tankId).inherited?"disabled":""}>Abertura</button><button class="button review-button" data-book-reading="${i}" data-field="closing">Fechamento</button>`:"Conferir"}</td></tr>`).join("")||'<tr><td colspan="4" class="compact-empty">Nenhuma leitura próxima disponível.</td></tr>'}</tbody></table></div></details></section></div>
    <p class="book-inline-note">Encerrantes exibidos com duas casas decimais; demais volumes em litros inteiros. Clique nos campos para editar o valor exato. Vendas = final − inicial − aferição. Os ajustes dos bicos ficam salvos neste LMC.</p><div id="reading-origin-feedback" class="book-inline-note" hidden></div>
    <details class="book-rules"><summary>Critério do percentual</summary><p>Percentual = (fechamento físico − escritural) ÷ estoque escritural × 100. Referência operacional: 0,6%. A medição real é preservada. Escritural zero não recebe percentual. Os litros mantêm a precisão operacional dos registros; impressão e envio fiscal ainda não estão disponíveis.</p></details>`;
}
function collectBookRequest() {
  const p=bookState.preparation;
  const read=readBookNumber;
  return {branch:p.branch,date:p.date,productId:p.productId,version:p.version,
    tanks:p.tanks.map(t=>({tankId:t.tankId,previousClosing:read(`book-opening-${t.tankId}`),closing:read(`book-closing-${t.tankId}`)})),
    pumps:p.pumps.map(b=>({pumpId:b.pumpId,closing:read(`book-pump-closing-${b.pumpId}`),calibration:read(`book-pump-calibration-${b.pumpId}`),...(b.cutoff?{counterSource:bookState.counterSources[b.pumpId]??null}:{})})),
    observation:$("book-observation").value.trim(),measurementMethod:bookMeasurementMethod()};
}
function updateBookTotals(input) {
  const p=bookState.preparation,pumps=currentBookPumps(input.pumps),received=p.documents.reduce((sum,r)=>sum+r.volume,0),sales=pumps.reduce((sum,b)=>sum+b.sales,0),value=pumps.reduce((sum,b)=>sum+b.sales*b.price,0);
  const opening=input.tanks.every(t=>t.previousClosing!=null&&Number.isFinite(t.previousClosing))?input.tanks.reduce((sum,t)=>sum+t.previousClosing,0):null;
  const closing=input.tanks.every(t=>t.closing!=null&&Number.isFinite(t.closing))?input.tanks.reduce((sum,t)=>sum+t.closing,0):null;
  const stock=opening==null?null:opening+received-sales,difference=stock==null||closing==null?null:closing-stock;
  for(const [id,v] of Object.entries({opening,received,available:opening==null?null:opening+received,sales,stock,difference,closing,"month-sales":p.monthSalesBefore+sales}))$("total-"+id).textContent=bookVolume(v);
  $("total-day-value").textContent=Number.isFinite(value)?money.format(value):"—";$("total-month-value").textContent=Number.isFinite(value)?money.format(p.monthValueBefore+value):"—";
  for(const b of pumps) {
    $(`book-pump-opening-${b.pumpId}`).textContent=bookCounter(b.opening);
    for(const field of ["closing","calibration"])$(`book-pump-${field}-display-${b.pumpId}`).textContent=b[field]==null||!Number.isFinite(b[field])?"":field==="closing"?bookCounter(b[field]):bookVolume(b[field]);
    const salesCell=$(`book-pump-sales-${b.pumpId}`);
    salesCell.textContent=b.negativeSales?`${bookCounter(b.closing-b.opening-b.calibration)}`:bookVolume(b.sales);
    salesCell.closest?.('tr')?.classList.toggle('pump-negative',b.negativeSales);
  }
  for(const t of input.tanks) {
    for(const [field,value] of [["opening",t.previousClosing],["closing",t.closing]])
      $(`book-${field}-display-${t.tankId}`).textContent=value==null||!Number.isFinite(value)?"":bookVolume(value);
    const inflow=p.documents.filter(r=>r.tankId===t.tankId).reduce((sum,r)=>sum+r.volume,0),outflow=pumps.filter(b=>b.tankId===t.tankId).reduce((sum,b)=>sum+b.sales,0);
    const balance=t.previousClosing==null||!Number.isFinite(t.previousClosing)?null:t.previousClosing+inflow-outflow;
    const diff=t.closing==null||!Number.isFinite(t.closing)||balance==null||!Number.isFinite(balance)?null:t.closing-balance;
    $(`tank-table-${t.tankId}`).innerHTML=renderTankTableReference(balance,t.tankId);
    $(`tank-balance-${t.tankId}`).textContent=bookVolume(balance);$(`tank-difference-${t.tankId}`).textContent=bookVolume(diff);$(`tank-percent-${t.tankId}`).textContent=bookDecimal(balance>0&&diff!=null?diff/balance*100:null);
  }
}
function scheduleBookPreview() {
  if(bookState.saving||bookState.uncertain)return;
  invalidateBookPreview();if(!bookState.preparation)return;
  const input=collectBookRequest(),p=bookState.preparation,request=bookState.previewRequest;
  updateBookTotals(input);$("book-preview-alerts").textContent="";
  refreshClosingReviews();
  const missingMeasurements=input.tanks.some(t=>t.previousClosing==null||t.closing==null);
  $("book-save-description").textContent=p.followingBooks.length?`Depois, confira ${p.followingBooks.length} livro(s) posteriores.`:"Livro, tanques, bicos e notas serão gravados juntos.";
  if(p.blockers.length){$("book-save-state").textContent="Confira os impedimentos indicados acima.";return;}
  const pendingPumps=p.pumps.filter(b=>b.cutoff&&!bookState.counterSources[b.pumpId]);
  if(pendingPumps.length){$("book-save-state").textContent=`Confira o fechamento na grade: bico(s) ${pendingPumps.map(b=>b.pumpId).join(", ")}. A abertura permanece fixa.`;return;}
  if(missingMeasurements){$("book-save-state").textContent="Preencha a abertura e o fechamento de todos os tanques para salvar.";return;}
  if(!input.measurementMethod){$("book-save-state").textContent="Falta selecionar a origem do fechamento: Manual ou Medidor eletrônico.";return;}
  if(input.tanks.some(t=>[t.previousClosing,t.closing].some(x=>!Number.isFinite(x)||x<0))){$("book-save-state").textContent="Confira os volumes informados.";return;}
  if(input.pumps.some(b=>[b.closing,b.calibration].some(v=>v==null||!Number.isFinite(v)))){$("book-save-state").textContent="Preencha o encerrante final e a aferição de todos os bicos (zero quando não houver).";return;}
  $("book-save-state").textContent="Conferindo a gravação…";
  bookState.timer=setTimeout(async()=>{
    try {
      const preview=await api('/api/lmc/preview',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(input)});
      if(request!==bookState.previewRequest)return;
      bookState.preview=preview;bookState.requestBody=input;bookState.operationId=crypto.randomUUID();
      $("save-book").disabled=!preview.canSave;$("save-next-book").disabled=!preview.canSave;$("book-save-state").textContent=preview.canSave?(p.bookId?"Pronto para refazer o LMC deste dia.":"Pronto para inserir o LMC deste dia."):"Confira os pontos abaixo antes de salvar.";
      const messages=[...preview.blockers,...(preview.balance?.aboveReference?["Variação acima da referência de 0,6%. A justificativa e a medição serão registradas."]:[])];
      $("book-preview-alerts").innerHTML=messages.map(x=>`<p>${esc(x)}</p>`).join("");
    }catch(error){if(request===bookState.previewRequest){$("book-save-state").textContent=error.message;$("reload-book").hidden=error.status!==409;}}
  },300);
}
async function saveCurrentBook(andNext=false) {
  if(bookState.saving||!bookState.preview?.canSave||!bookState.requestBody)return;
  if(!bookState.uncertain)bookState.saveAndNext=andNext===true;
  bookState.saving=true;lockBookUi(true);$("save-book").disabled=true;$("book-save-state").textContent="Gravando o LMC…";
  const input={...bookState.requestBody,operationId:bookState.operationId};
  let result;
  try {
    result=await api('/api/lmc/save',{method:'POST',headers:{'Content-Type':'application/json','X-LMC-CSRF':state.csrf},body:JSON.stringify(input)});
  }catch(error){
    bookState.saving=false;
    if([400,409,422].includes(error.status)){
      bookState.uncertain=false;lockBookUi(false);invalidateBookPreview();$("reload-book").hidden=false;
      $("book-save-state").textContent=error.message;
    }else{
      bookState.uncertain=true;lockBookUi(true);$("save-book").disabled=false;$("save-book").textContent="Tentar novamente";
      $("book-save-state").textContent="A confirmação não chegou. Você pode tentar novamente. Se o livro já foi salvo, será necessário atualizar a conferência.";
      if(error.status===403) {try{state.csrf=(await api('/api/security')).token;}catch{}}
    }
    return;
  }
  const continueNext=bookState.saveAndNext;
  bookState.saving=false;bookState.uncertain=false;lockBookUi(false);invalidateBookPreview();
  const branch=state.context.branches.find(x=>x.id===state.branch);if(branch&&(!branch.lastBookDate||result.date>branch.lastBookDate))branch.lastBookDate=result.date;
  const savedMessage=`LMC ${result.bookId} ${result.alreadyApplied?"já gravado":result.mode} em ${date(result.date)}. ${result.followingBooks.length?`Confira a abertura dos ${result.followingBooks.length} livro(s) posteriores.`:"Tanques, bicos e entradas salvos."}${result.auditWarning?` ${result.auditWarning}`:""}`;
  $("book-month").value=result.date.slice(0,7);
  if(continueNext)await openNewBook(result.productId,savedMessage);
  else {showError("book-feedback",savedMessage);await loadBookLedger();}
}
function reloadCurrentBook() {
  if(bookState.nextBook)return openNewBook(bookState.nextBook.product,bookState.nextBook.savedMessage);
  return openBookEditor($("book-date").value,Number($("editor-product").value));
}
$("book-month").value=previousMonthPeriod().from.slice(0,7);
$("ledger-product").addEventListener("change",()=>{clearBookSelection();renderBookLedger();});
$("books-list").addEventListener("change",event=>{
  const input=event.target;
  if(input.id==="select-all-books") {
    bookState.selectedIds=new Set(input.checked?visibleBookRows().map(x=>x.id):[]);renderBookLedger();
  } else if(input.dataset.selectBook) {
    const id=Number(input.dataset.selectBook);if(input.checked)bookState.selectedIds.add(id);else bookState.selectedIds.delete(id);updateBookSelection();
  }
});
$("clear-book-selection").addEventListener("click",()=>{clearBookSelection();renderBookLedger();});
$("books-list").addEventListener("click",event=>{const b=event.target.closest('[data-open-book]');if(b){const row=bookState.rows.find(x=>x.id===Number(b.dataset.openBook));openBookEditor(row.date,row.productId);}});
$("new-book").addEventListener("click",()=>openNewBook(Number($("ledger-product").value)||null));
$("close-book").addEventListener("click",loadBookLedger);
$("reload-book").addEventListener("click",reloadCurrentBook);
$("book-date").addEventListener("change",()=>openBookEditor($("book-date").value,Number($("editor-product").value)));
$("book-date").addEventListener("input",()=>{if(!bookState.saving&&!bookState.uncertain){invalidateBookPreview();++bookState.editorRequest;$("book-save-state").textContent="Confira a nova data do movimento.";}});
$("editor-product").addEventListener("change",()=>bookState.newEntry?openNewBook(Number($("editor-product").value)):openBookEditor($("book-date").value,Number($("editor-product").value)));
$("measurement-method").addEventListener("change",scheduleBookPreview);
$("book-editor-body").addEventListener("input",event=>{
  if(event.target.matches('[data-pump-source]')){selectBookPumpSource(Number(event.target.dataset.pumpSource),event.target.value);return;}
  if(event.target.matches('input[data-measurement][id^="book-closing-"]'))clearClosingReview(Number(event.target.id.split('-').pop()));
  if(event.target.matches('[data-measurement],[data-pump-edit],#book-observation'))scheduleBookPreview();
});
$("book-editor-body").addEventListener("focusout",event=>{
  if(event.target.matches('input[data-measurement][id^="book-closing-"]'))reviewClosingInput(Number(event.target.id.split('-').pop()));
});
$("book-editor-body").addEventListener("click",event=>{
  const correction=event.target.closest('[data-closing-suggestion]');if(correction){applyClosingSuggestion(Number(correction.dataset.closingSuggestion));return;}
  const keep=event.target.closest('[data-keep-closing]');if(keep){keepClosingValue(Number(keep.dataset.keepClosing));return;}
  const inherit=event.target.closest('[data-inherit-opening]');if(inherit){usePreviousBookClosing(Number(inherit.dataset.inheritOpening));return;}
  const note=event.target.closest('[data-book-note]');if(note){openReview(note.dataset.bookNote);return;}
  const b=event.target.closest('[data-book-reading]');if(!b||bookState.saving||bookState.uncertain)return;
  const r=bookState.day.readings[Number(b.dataset.bookReading)],field=b.dataset.field;
  if(!bookReadingUsable(r)||!["opening","closing"].includes(field))return;
  if(field==="opening"&&bookState.preparation.tanks.find(t=>t.tankId===r.tankId)?.inherited)return;
  $(`book-${field}-${r.tankId}`).value=r.volume;
  const expected=field==="opening"?bookDateOffset(bookState.preparation.date,-1):bookState.preparation.date;
  $("reading-origin-feedback").hidden=false;$("reading-origin-feedback").textContent=`Tanque ${r.tankId}: ${r.source}, ${date(r.at.slice(0,10))}, ${bookVolume(r.volume)} L. ${r.at.slice(0,10)!==expected?"A data da fonte difere do fechamento escolhido. Confirme a correspondência da medição.":"Confira se esta leitura corresponde ao fechamento."}`;
  scheduleBookPreview();
});
$("save-book").addEventListener("click",()=>saveCurrentBook());
$("save-next-book").addEventListener("click",()=>saveCurrentBook(true));
$("review-dialog").addEventListener("close",()=>{if(!$("books-view").hidden&&!$("book-editor-pane").hidden&&!bookState.saving&&!bookState.uncertain)openBookEditor($("book-date").value,Number($("editor-product").value));});
