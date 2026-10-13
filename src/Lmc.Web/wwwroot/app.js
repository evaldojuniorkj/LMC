"use strict";
const $ = (id) => document.getElementById(id);
const state = { branch: null, context: null, page: 1, status: "attention", total: 0, detail: null,
  listRequest: 0, detailRequest: 0, shiftRequest: 0, previewRequest: 0, bookRequest: 0, preview: null,
  operationId: null, applying: false, uncertain: false, csrf: null, previewTimer: null, allocations:[], allocationSequence:0 };
const number = new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 0 });
const liters = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 4 });
const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));
const date = (value) => value ? value.split("-").reverse().join("/") : "Não informada";
const volume = (value) => value == null ? "Não informado" : liters.format(value);
const key = (note) => `${note.branchId}/${note.databaseId}/${note.id}`;
const shiftName = (number, id) => number === 0 ? "Turno geral" : number != null ? `Turno ${number}` : id != null ? `Turno ID ${id}` : "Turno não localizado";
const alertLabels = { intervalo: "Intervalo prolongado", data_anterior: "Data anterior", vinculos: "Vínculos divergentes",
  data_ausente: "Data ausente", turno_ausente: "Turno ausente", turno_data: "Conferir turno" };

function previousMonthPeriod(today = new Date()) {
  const first = new Date(today.getFullYear(), today.getMonth() - 1, 1);
  const last = new Date(today.getFullYear(), today.getMonth(), 0);
  const calendarDate = value => `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(value.getDate()).padStart(2, "0")}`;
  return { from: calendarDate(first), until: calendarDate(last) };
}

async function api(url, options = {}, retrySession=true) {
  const response = await fetch(url, { ...options, headers: { "Accept": "application/json", ...options.headers } });
  if(response.status===401&&retrySession&&typeof authenticateSession==='function'){
    await authenticateSession();
    const headers={...options.headers};if(headers['X-LMC-CSRF'])headers['X-LMC-CSRF']=state.csrf;
    return api(url,{...options,headers},false);
  }
  const text = await response.text();
  let result;
  try { result = text ? JSON.parse(text) : {}; } catch { result = {}; }
  if (!response.ok) { const error=new Error(result.message || result.detail || "Não foi possível concluir a consulta. Tente novamente.");error.status=response.status;throw error; }
  return result;
}
function showError(id, message) { $(id).textContent = message; $(id).hidden = !message; }

function allocationOptions(productId) {
  const chosenDate=$("target-date").value;
  return (state.detail?.allocationTanks||[]).filter(t=>t.productId===productId&&t.active&&(!t.activatedAt||t.activatedAt<=chosenDate));
}
function allocationProposal() {
  if(!state.detail)return null;
  const rows=state.allocations.map(({parcelId,productId,tankId,volume})=>({parcelId,productId,tankId,volume}));
  const changed=rows.some(r=>r.parcelId==null?r.volume>0:state.detail.parcels.some(p=>p.id===r.parcelId&&(p.tankId!==r.tankId||p.volume!==r.volume)));
  return changed?rows.filter(r=>r.parcelId!=null||r.volume>0):null;
}
function allocationTotals() {
  return [...new Set(state.detail.parcels.map(p=>p.productId))].map(id=>{
    const original=state.detail.parcels.filter(p=>p.productId===id),rows=state.allocations.filter(r=>r.productId===id);
    const missing=original.some(p=>p.volume==null)||rows.some(r=>r.volume==null||!Number.isFinite(r.volume)||r.volume<0);
    const expected=original.reduce((s,p)=>s+Math.round((p.volume||0)*10000),0);
    const allocated=rows.reduce((s,r)=>s+Math.round((r.volume||0)*10000),0);
    return {id,product:original[0].product,expected:expected/10000,allocated:allocated/10000,difference:(expected-allocated)/10000,missing};
  });
}
function updateAllocationTotals() {
  const totals=allocationTotals();
  for(const t of totals) {
    const e=$("allocation-total-"+t.id);
    e.className="allocation-total "+(t.missing||t.difference!==0?"unbalanced":"balanced");
    e.textContent=t.missing?"Preencha todos os volumes.":`${volume(t.allocated)} de ${volume(t.expected)} L · ${t.difference===0?"Total conferido":t.difference>0?`Faltam ${volume(t.difference)} L`:`Excedeu ${volume(-t.difference)} L`}`;
  }
  return totals.every(t=>!t.missing&&t.difference===0);
}
function renderAllocationEditor() {
  if(!state.detail)return;
  const products=[...new Map(state.detail.parcels.map(p=>[p.productId,p.product])).entries()];
  $("allocation-editor").innerHTML=`<div class="allocation-heading"><h3>Volume por tanque</h3><span>Total da nota preservado</span></div><p class="allocation-help">Ajuste os litros ou divida a entrada entre tanques do mesmo combustível.</p>`+products.map(([id,name])=>{
    const options=allocationOptions(id);
    return `<div class="allocation-product"><h4>${esc(name)}</h4><div class="allocation-columns"><span>Tanque de destino</span><span>Volume recebido (L)</span><span></span></div>${state.allocations.filter(r=>r.productId===id).map(r=>{
      const original=state.detail.parcels.find(p=>p.id===r.parcelId),choices=[...options];
      if(!choices.some(t=>t.tankId===r.tankId))choices.push({tankId:r.tankId,tank:original?.tank||r.tankId});
      return `<div class="allocation-row"><div><select name="tank-${r.key}" autocomplete="off" data-allocation-key="${r.key}" data-allocation-field="tankId" aria-label="Tanque da parcela ${r.key}">${choices.map(t=>`<option value="${t.tankId}" ${t.tankId===r.tankId?"selected":""}>Tanque ${esc(t.tank)}</option>`).join("")}</select><small>${original?`Atual: tanque ${esc(original.tank)} · ${volume(original.volume)} L`:"Nova divisão desta nota"}</small></div><input id="allocation-volume-${r.key}" name="volume-${r.key}" autocomplete="off" type="number" inputmode="decimal" min="0" max="1000000000" step="0.0001" required data-allocation-key="${r.key}" data-allocation-field="volume" aria-label="Volume da parcela ${r.key} em litros" value="${r.volume??""}"><button type="button" class="allocation-remove" data-remove-allocation="${r.key}" aria-label="Retirar volume da parcela ${r.key}" title="Retirar volume desta parcela">×</button></div>`;
    }).join("")}<div id="allocation-total-${id}" class="allocation-total" role="status"></div><button type="button" class="button secondary" data-add-allocation="${id}" ${options.length<2||state.allocations.length>=100?"disabled":""}>＋ Dividir em outro tanque</button></div>`;
  }).join("");
  updateAllocationTotals();
}
function editAllocation(key,field,value) {
  if(state.applying||state.uncertain)return;
  const row=state.allocations.find(r=>r.key===key);if(!row)return;
  row[field]=value===""?null:Number(value);schedulePreview();
}
function addAllocation(productId) {
  if(state.applying||state.uncertain||state.allocations.length>=100)return;
  const choices=allocationOptions(productId);if(choices.length<2)return;
  const tank=choices.find(t=>!state.allocations.some(r=>r.productId===productId&&r.tankId===t.tankId))||choices[0];
  const remaining=allocationTotals().find(t=>t.id===productId)?.difference||0;
  state.allocations.push({key:++state.allocationSequence,parcelId:null,productId,tankId:tank.tankId,volume:Math.max(0,remaining)});
  renderAllocationEditor();schedulePreview();
}
function allocationHistory(revision) {
  if(!revision)return "";
  const format=rows=>rows.map(p=>`Tanque ${esc(p.tank)}: ${volume(p.volume)} L`).join(" · ");
  return `<p><strong>Distribuição anterior:</strong> ${format(revision.before)}</p><p><strong>Distribuição salva:</strong> ${format(revision.after)}</p>`;
}

async function initialize() {
  try {
    state.context = await api("/api/context");
    state.csrf = (await api("/api/security")).token;
    const branches = state.context.branches;
    if (!branches.length) throw new Error("Nenhuma filial com entradas de combustível foi encontrada.");
    $("branch").innerHTML = branches.map(b => `<option value="${b.id}">${esc(b.name)}</option>`).join("");
    $("branch").disabled = false;
    state.branch = branches[0].id;
    $("connection").textContent = `${state.context.database} conectado`;
    $("deployment-label").textContent=`${state.context.deploymentLabel||"Desenvolvimento local"} · v${state.context.version||"1.0.0"}`;
    $("logout").hidden=!state.context.requiresLogin;
    $("book-date").value = branches[0].lastBookDate || "";
    await loadNotes();
  } catch (error) {
    showError("global-error", error.message);
    $("connection").textContent = "Conexão indisponível";
    $("notes-body").innerHTML = '<tr><td colspan="6" class="empty">Não foi possível carregar as notas. Use “Atualizar dados” para tentar novamente.</td></tr>';
    $("notes-region").setAttribute("aria-busy", "false");
    $("result-count").textContent = "Consulta indisponível";
  }
}

async function loadNotes() {
  if (!state.branch) return initialize();
  const requestId = ++state.listRequest;
  const params = new URLSearchParams({ branch: state.branch, search: $("search").value.trim(),
    from: $("from").value, until: $("until").value, dateField: $("date-field").value,
    status: state.status, threshold: $("threshold").value, sort: $("sort").value, page: state.page });
  showError("global-error", "");
  $("notes-region").setAttribute("aria-busy", "true");
  $("notes-body").innerHTML = '<tr><td colspan="6" class="empty">Consultando as entradas…</td></tr>';
  $("previous").disabled = $("next").disabled = true;
  $("result-count").textContent = "Atualizando…";
  try {
    const data = await api(`/api/notes?${params}`);
    if (requestId !== state.listRequest) return;
    state.page = data.page; state.total = data.total;
    for (const name of ["total", "attention", "delayed", "earlier"]) $("metric-" + name).textContent = number.format(data.summary[name]);
    $("tab-total").textContent = number.format(data.summary.total);
    $("tab-attention").textContent = number.format(data.summary.attention);
    $("threshold-caption").textContent = `${$("threshold").value} dias ou mais entre datas`;
    $("notes-body").innerHTML = data.items.map(({ note: n, alerts }) => {
      const primary = alerts[0];
      const shift = shiftName(n.shiftNumber, n.shiftId);
      const status = primary ? `<span class="badge ${primary === "data_anterior" || primary === "vinculos" ? "red" : "amber"}">${esc(alertLabels[primary])}</span>` : '<span class="badge neutral">Sem alerta de triagem</span>';
      return `<tr><td><div class="note-number">NF ${esc(n.number)}</div><div class="note-supplier" title="${esc(n.supplier)}">${esc(n.supplier)}</div><span class="product-text" title="${esc(n.products)}">${esc(n.products)}</span></td>
        <td><div class="date-pair"><span>Emissão</span><time>${date(n.documentDate)}</time><span>Entrada</span><time>${date(n.issueFieldDate)}</time></div></td>
        <td><span class="operational-date">${date(n.operationalDate)}</span><span class="legacy">${esc(shift)}</span></td>
        <td class="numeric">${volume(n.volume)}<span class="liters">litros · ${n.parcels} ${n.parcels === 1 ? "parcela" : "parcelas"}</span></td>
        <td>${status}${primary === "intervalo" || primary === "data_anterior" ? `<span class="status-note">${Math.abs(n.gapDays)} ${Math.abs(n.gapDays) === 1 ? "dia" : "dias"} entre entrada e LMC</span>` : ""}${alerts.length > 1 ? `<span class="status-note">+ ${alerts.length - 1} outro(s) alerta(s)</span>` : ""}</td>
        <td><button class="button review-button" data-key="${key(n)}" aria-label="Revisar nota ${n.number}">Revisar <span>↗</span></button></td></tr>`;
    }).join("") || `<tr><td colspan="6" class="empty"><strong>${state.status === "attention" ? "Nenhuma nota para conferir nesta busca" : "Nenhuma nota encontrada"}</strong>${state.status === "attention" ? "Use “Todas as notas” para consultar os demais lançamentos." : "Tente outro número ou amplie o período."}</td></tr>`;
    $("result-count").textContent = data.total ? `${number.format((data.page-1)*data.pageSize+1)}–${number.format(Math.min(data.page*data.pageSize,data.total))} de ${number.format(data.total)} notas` : "0 notas nesta busca";
    $("page-number").textContent = `${data.page} / ${Math.max(1, Math.ceil(data.total/data.pageSize))}`;
    $("previous").disabled = data.page <= 1;
    $("next").disabled = data.page*data.pageSize >= data.total;
    $("next-context").textContent = data.summary.attention ? `${number.format(data.summary.attention)} notas sinalizadas nesta busca. Confira as entradas antes do fechamento.` : "Nenhum alerta de datas nesta busca. Continue para a conferência do LMC.";
    $("updated").textContent = `Última consulta às ${new Date(data.updatedAt).toLocaleTimeString("pt-BR", {hour:"2-digit",minute:"2-digit"})}`;
  } catch (error) {
    if (requestId !== state.listRequest) return;
    showError("global-error", error.message);
    $("notes-body").innerHTML = '<tr><td colspan="6" class="empty">Consulta não concluída. Ajuste os filtros ou tente novamente.</td></tr>';
    $("result-count").textContent = "Consulta indisponível";
    for (const name of ["total", "attention", "delayed", "earlier"]) $("metric-" + name).textContent = "—";
  } finally { if (requestId === state.listRequest) $("notes-region").setAttribute("aria-busy", "false"); }
}

function invalidatePreview() {
  if(state.applying||state.uncertain) return;
  clearTimeout(state.previewTimer);
  ++state.previewRequest;
  state.preview = null;
  state.operationId = null;
  $("preview-content").hidden = true;
  $("preview-content").innerHTML = "";
  $("apply-button").disabled = true;
  $("apply-button").textContent = "Salvar correção";
  $("retry-review").hidden = true;
  $("drawer-state").textContent = "Escolha a data e o turno para conferir o ajuste.";
}

function schedulePreview() {
  if(state.applying) return;
  invalidatePreview();
  showError("review-error", "");
  if (!state.detail) return;
  if(!updateAllocationTotals()) {$("drawer-state").textContent="Distribua o total de cada combustível entre os tanques.";return;}
  const n = state.detail.note;
  $("date-suggestion").textContent = $("target-date").value === n.issueFieldDate
    ? "Sugestão: usar a entrada registrada na nota. Você pode escolher outra data."
    : "Data escolhida para o LMC. A emissão e a entrada da nota serão preservadas.";
  if ($("target-shift").value) $("shift-hint").textContent = n.generalShift && Number($("target-shift").value) === n.shiftId
    ? "Turno geral mantido. Você pode ajustar a data do LMC e os volumes por tanque."
    : "O turno selecionado será usado na correção.";
  if ($("target-shift").disabled || !$("review-form").checkValidity()) return;
  if ($("target-date").value === n.operationalDate && Number($("target-shift").value) === n.shiftId && !allocationProposal()) {
    $("drawer-state").textContent = "Data, turno e volumes iguais ao lançamento. Nada a alterar.";
    return;
  }
  $("drawer-state").textContent = "Conferindo o ajuste automaticamente…";
  state.previewTimer = setTimeout(preview, 300);
}

async function openReview(noteKey) {
  if(state.applying||state.uncertain)return;
  const requestId = ++state.detailRequest;
  ++state.shiftRequest;
  state.detail = null;
  invalidatePreview();
  $("review-title").textContent = "Carregando nota…";
  $("detail-content").innerHTML = '<p class="muted">Consultando a nota, os movimentos e a distribuição por tanque…</p>';
  $("review-form").hidden = true;
  $("review-form").reset();
  document.querySelectorAll('#review-form input,#review-form select,#review-form textarea').forEach(e=>e.disabled=false);
  $("optional-observation").open = false;
  $("drawer-state").textContent = "Carregando a nota…";
  showError("review-error", "");
  $("review-dialog").showModal();
  try {
    const [detail,history] = await Promise.all([api(`/api/notes/${noteKey}`),api(`/api/notes/${noteKey}/history`)]);
    if (requestId !== state.detailRequest || !$("review-dialog").open) return;
    state.detail = detail;
    state.allocationSequence=0;
    state.allocations=detail.parcels.map(p=>({key:++state.allocationSequence,parcelId:p.id,productId:p.productId,tankId:p.tankId,volume:p.volume}));
    const n = detail.note;
    $("review-title").textContent = `Nota ${n.number}`;
    const alerts = [];
    if (n.gapDays >= Number($("threshold").value)) alerts.push(`${n.gapDays} dias entre a entrada da nota e a data do LMC. Confira em qual dia a nota deve compor o LMC.`);
    if (n.gapDays < 0) alerts.push(`A data do LMC é ${Math.abs(n.gapDays)} dia(s) anterior à entrada registrada. Confira a data operacional do lançamento.`);
    if (n.inconsistent) alerts.push("Há divergências entre a nota e seus movimentos ou parcelas. A simulação ficará indisponível até a conferência desses vínculos.");
    $("detail-content").innerHTML = `<h3 class="supplier-detail">${esc(n.supplier)}</h3><p class="review-volume">${esc(n.products)} · ${volume(n.volume)} L</p>
      ${alerts.length ? `<div class="notice warning"><div>${alerts.map(a => `<p>${esc(a)}</p>`).join("")}</div></div>` : ""}
      <div class="detail-dates"><div><small>Emissão da nota</small><strong>${date(n.documentDate)}</strong></div><div><small>Entrada da nota</small><strong>${date(n.issueFieldDate)}</strong></div><div class="highlight"><small>Data do LMC</small><strong>${date(n.operationalDate)}</strong></div></div>
      <details class="preview-books"><summary>Tanques e volumes (${detail.parcels.length})</summary><table class="parcel-table"><thead><tr><th>Combustível</th><th>Tanque</th><th>Data da parcela</th><th class="numeric">Litros</th></tr></thead><tbody>${detail.parcels.map(p => `<tr><td>${esc(p.product)}</td><td>${esc(p.tank)}</td><td>${date(p.operationalDate)}</td><td class="numeric">${volume(p.volume)}</td></tr>`).join("")}</tbody></table><p class="detail-caption">${detail.movements.length} movimento(s) vinculado(s) · Filial ${n.branchId} · Documento ${n.id}</p></details>
      <details class="preview-books"><summary>Histórico de correções (${history.length})</summary>${history.map(h=>`<article class="history-item"><strong>${date(h.sourceDate)} → ${date(h.targetDate)}</strong><p>Turno ID ${h.sourceShiftId ?? "ausente"} → ${h.targetShiftId}</p>${allocationHistory(h.allocation)}${h.reason ? `<p>${esc(h.reason)}</p>` : ""}<small>${esc(h.appliedAt.replace("T"," "))} · ${esc(h.actor)} · Registro ${h.historyId}</small></article>`).join("") || '<p>Nenhuma correção por esta interface.</p>'}</details>`;
    const suggestEntry = n.generalShift && !n.inconsistent && n.operationalDate && n.issueFieldDate
      && (!n.documentDate || n.issueFieldDate >= n.documentDate);
    $("target-date").value = (suggestEntry ? n.issueFieldDate : n.operationalDate) || "";
    renderAllocationEditor();
    $("review-form").hidden = false;
    await loadShifts();
  } catch (error) { if (requestId === state.detailRequest) { showError("review-error", error.message);$("drawer-state").textContent="Não foi possível carregar a nota. Feche e tente novamente."; } }
}

async function loadShifts() {
  const requestId = ++state.shiftRequest;
  invalidatePreview();
  $("target-shift").disabled = true;
  $("target-shift").innerHTML = '<option value="">Consultando turnos…</option>';
  $("drawer-state").textContent = "Consultando o turno…";
  $("shift-hint").textContent = "Consultando os turnos da data selecionada…";
  showError("review-error", "");
  if (!$("target-date").value || !state.detail) {
    $("target-shift").innerHTML = '<option value="">Selecione a data</option>';
    $("shift-hint").textContent = "Informe a nova data do LMC para o lançamento.";
    return;
  }
  try {
    renderAllocationEditor();
    const shifts = await api(`/api/shifts?${new URLSearchParams({ branch: state.detail.note.branchId, date: $("target-date").value })}`);
    if (requestId !== state.shiftRequest || !state.detail) return;
    $("target-shift").innerHTML = shifts.length ? '<option value="">Selecione um turno</option>' + shifts.map(s => `<option value="${s.id}">${shiftName(s.number, s.id)}</option>`).join("") : '<option value="">Nenhum turno disponível</option>';
    $("target-shift").disabled = !shifts.length;
    const currentShift = shifts.find(s => s.id === state.detail.note.shiftId);
    const currentGeneral = currentShift?.isGeneral;
    if (currentShift) $("target-shift").value = String(currentShift.id);
    $("shift-hint").textContent = currentGeneral ? "Turno geral mantido. Você pode ajustar a data do LMC e os volumes por tanque."
      : shifts.length ? "Escolha o turno geral da filial ou um turno da data selecionada."
      : "Não há turno geral nem turno diário disponível nesta filial e data.";
    schedulePreview();
  } catch (error) {
    if (requestId !== state.shiftRequest) return;
    $("target-shift").innerHTML = '<option value="">Consulta indisponível</option>';
    $("shift-hint").textContent = "Altere a data para consultar os turnos novamente.";
    showError("review-error", error.message);
    $("drawer-state").textContent = "Não foi possível consultar o turno.";
    $("retry-review").hidden = false;
  }
}

async function preview() {
  if (state.applying || state.uncertain || !state.detail || !$("review-form").checkValidity() || $("target-shift").disabled || !updateAllocationTotals()) return;
  const requestId = ++state.previewRequest;
  showError("review-error", "");
  $("preview-content").hidden = true;
  $("apply-button").disabled = true;
  try {
    const result = await api(`/api/notes/${key(state.detail.note)}/preview`, { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({ operationalDate:$("target-date").value,
      shiftId:Number($("target-shift").value), reason:$("reason").value, version:state.detail.version,allocations:allocationProposal() }) });
    if (requestId !== state.previewRequest) return;
    state.preview = result;
    state.operationId = crypto.randomUUID();
    const multipleBookDates = new Set((result.bookReceipts || []).map(r=>r.bookDate)).size > 1;
    $("preview-content").innerHTML = `
      <div class="preview-summary"><div><small>SITUAÇÃO ATUAL</small><strong>${date(result.sourceDate)}</strong><small>${shiftName(state.detail.note.shiftNumber, result.sourceShiftId)}</small></div><span class="preview-arrow">→</span><div><small>DESTINO PROPOSTO</small><strong>${date(result.targetDate)}</strong><small>${shiftName(result.targetShift.number, result.targetShift.id)}</small></div></div>
      ${result.allocations?'<p class="allocation-preview">Distribuição por tanque será atualizada. O total de cada combustível permanece igual.</p>':""}
      ${multipleBookDates ? '<p class="receipt-alert">Esta nota aparece em livros de mais de um dia. Esses recebimentos precisam de conferência no LMC.</p>' : ""}
      <details class="preview-books"><summary>Ver impacto e livros a revisar (${result.booksToReview.length})</summary>
      ${result.effects.length ? `<table class="effects-table"><thead><tr><th>Dia</th><th>Combustível / tanque</th><th class="numeric">Efeito nas entradas</th></tr></thead><tbody>${result.effects.map(e => `<tr><td>${date(e.date)}</td><td>${esc(e.product)} · ${esc(e.tank)}</td><td class="numeric ${e.liters < 0 ? "negative" : "positive"}">${e.liters > 0 ? "+" : ""}${liters.format(e.liters)} L</td></tr>`).join("")}</tbody></table>` : '<p class="preview-note">A mudança é apenas de turno. O volume diário de entradas permanece igual.</p>'}
      <p class="preview-note">O total da nota e as medições físicas serão preservados. Os livros abaixo precisam de revisão após salvar.</p><ul>${result.booksToReview.map(b => `<li>${date(b.date)} · ${esc(b.product)} · LMC ${b.number ?? b.id}</li>`).join("") || "<li>Nenhum livro registrado nesse intervalo.</li>"}</ul>
      ${(result.bookReceipts||[]).length ? `<p>Recebimentos já encontrados nos livros:</p><ul>${result.bookReceipts.map(r=>`<li>${date(r.bookDate)} · tanque ${r.tankId} · ${volume(r.volume)} L · ${r.supplierMatches ? "fornecedor confere" : "fornecedor não confirmado"}</li>`).join("")}</ul>` : ""}
      <button class="button secondary full" type="button" id="download-preview">Baixar conferência</button><p class="field-hint">Prévia sem gravação; não é comprovante fiscal.</p></details>
      ${result.canApply ? "" : `<div class="notice error" role="alert">${esc(result.blockers.join(" "))}</div>`}`;
    $("preview-content").hidden = false;
    $("download-preview").addEventListener("click", downloadPreview);
    $("apply-button").disabled = !result.canApply;
    $("drawer-state").textContent = result.canApply ? "Conferência concluída. Confira o destino e salve." : "Resolva o impedimento indicado antes de salvar.";
  } catch (error) {
    if (requestId !== state.previewRequest) return;
    state.preview = null;
    state.operationId = null;
    showError("review-error", error.message);
    $("drawer-state").textContent = "Não foi possível concluir a conferência.";
    $("retry-review").hidden = false;
  }
}

async function applyCorrection() {
  if(state.applying || !state.detail || !state.preview?.canApply || !state.operationId) return;
  const note=state.detail.note;
  const proposal=state.preview;
  const payload={operationalDate:proposal.targetDate,shiftId:proposal.targetShift.id,reason:proposal.reason,
    version:proposal.version,operationId:state.operationId,acceptBookReview:true,allocations:proposal.allocations??null};
  state.applying=true;
  showError("review-error","");
  $("apply-button").disabled=true;
  $("apply-button").textContent="Gravando a correção…";
  $("close-review").disabled=true;
  document.querySelectorAll('#review-form input,#review-form select,#review-form textarea,#allocation-editor button').forEach(e=>e.disabled=true);
  let keepLocked=false;
  try {
    const receipt=await api(`/api/notes/${key(note)}/apply`,{method:"POST",headers:{"Content-Type":"application/json","X-LMC-CSRF":state.csrf},body:JSON.stringify(payload)});
    $("review-feedback").textContent=`Nota ${note.number}: correção salva em ${date(receipt.targetDate)} · ${shiftName(proposal.targetShift.number,receipt.targetShiftId)}.${proposal.allocations?" Volumes redistribuídos entre os tanques, mantendo o total da nota.":""} LMCs afetados pendentes de revisão. Pode seguir para a próxima nota.`;
    $("review-feedback").hidden=false;
    state.applying=false;
    state.uncertain=false;
    state.preview=null;
    $("review-dialog").close();
    await loadNotes();
  } catch(error) {
    showError("review-error",error.message);
    if(error.status===400 || error.status===409 || error.status===422) {
      state.applying=false;state.uncertain=false;invalidatePreview();
      $("drawer-state").textContent="Confira novamente a nota antes de salvar.";
      $("retry-review").hidden=false;
    } else {
      keepLocked=true;
      state.uncertain=true;
      if(error.status===403) {
        try { state.csrf=(await api("/api/security")).token; } catch { }
      }
      $("apply-button").disabled=false;
      $("apply-button").textContent="Tentar salvar novamente";
      $("drawer-state").textContent="A confirmação não foi recebida. Tente novamente para consultar ou concluir a mesma correção.";
    }
  } finally {
    state.applying=false;
    $("close-review").disabled=keepLocked;
    if(!keepLocked) {
      document.querySelectorAll('#review-form input,#review-form select,#review-form textarea').forEach(e=>e.disabled=false);
      renderAllocationEditor();
    }
  }
}

function downloadPreview() {
  if (!state.preview || !state.detail) return;
  const exportData = { type:"previa_nao_aplicada", generatedAt:new Date().toISOString(), note:state.detail.note, preview:state.preview };
  const url = URL.createObjectURL(new Blob([JSON.stringify(exportData,null,2)], {type:"application/json"}));
  const link = document.createElement("a"); link.href = url; link.download = `previa-nota-${state.detail.note.number}.json`;
  link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
}

function switchView(view,load=true) {
  if(bookState.saving||bookState.uncertain||state.applying||state.uncertain)return false;
  const titles={notes:"Revisão de notas",books:"Conferência do LMC",pending:"Pendências do LMC",tables:"Tabelas dos tanques"};
  if(!titles[view])return false;
  ++bookState.editorRequest;++bookState.listRequest;++pendingBooksState.request;
  invalidateBookPreview();$("branch").disabled=!state.branch;
  for(const name of Object.keys(titles)) {
    $(name+"-view").hidden=name!==view;
    $("nav-"+name).classList.toggle("active",name===view);
    $("nav-"+name).removeAttribute("aria-current");
  }
  $("nav-"+view).setAttribute("aria-current","page");$("mobile-view").value=view;
  $("breadcrumb-current").textContent=titles[view];document.title=`${titles[view]} · LMC`;
  showError("global-error", "");
  if(load&&view==="books")loadBooks();
  if(load&&view==="pending")loadPendingBooks();
  if(load&&view==="tables")loadTankTables();
  window.scrollTo({top:0,behavior:"smooth"});
  return true;
}
async function loadBooks() {
  return loadBookLedger();
}

$("filters").addEventListener("submit", e => {e.preventDefault();state.page=1;loadNotes();});
$("refresh").addEventListener("click", () => state.context ? loadNotes() : initialize());
$("branch").addEventListener("change", () => {state.branch=Number($("branch").value);state.page=1;$("book-date").value=state.context.branches.find(b=>b.id===state.branch)?.lastBookDate || "";loadNotes();if(!$("books-view").hidden)loadBooks();if(!$("pending-view").hidden)loadPendingBooks();if(!$("tables-view").hidden)loadTankTables();});
document.querySelectorAll("[data-status]").forEach(button => button.addEventListener("click", () => {
  state.status=button.dataset.status;state.page=1;
  document.querySelectorAll("[data-status]").forEach(b => {b.classList.toggle("active",b===button);b.setAttribute("aria-pressed",String(b===button));});
  loadNotes();
}));
$("sort").addEventListener("change", () => {state.page=1;loadNotes();});
$("threshold").addEventListener("change", () => {if($("threshold").reportValidity()){state.page=1;loadNotes();}});
$("previous").addEventListener("click", () => {state.page--;loadNotes();});
$("next").addEventListener("click", () => {state.page++;loadNotes();});
$("notes-body").addEventListener("click", event => {const button=event.target.closest("[data-key]");if(button)openReview(button.dataset.key);});
$("close-review").addEventListener("click", () => $("review-dialog").close());
$("review-dialog").addEventListener("close", () => {++state.detailRequest;++state.shiftRequest;state.detail=null;invalidatePreview();});
$("review-dialog").addEventListener("cancel",event=>{if(state.applying||state.uncertain)event.preventDefault();});
$("target-date").addEventListener("input", () => {invalidatePreview();++state.shiftRequest;$("target-shift").disabled=true;$("target-shift").value="";});
$("target-date").addEventListener("change", loadShifts);
$("target-shift").addEventListener("change", schedulePreview);
$("reason").addEventListener("input", schedulePreview);
$("allocation-editor").addEventListener("input",event=>{const e=event.target;if(e.dataset.allocationField)editAllocation(Number(e.dataset.allocationKey),e.dataset.allocationField,e.value);});
$("allocation-editor").addEventListener("click",event=>{
  if(state.applying||state.uncertain)return;
  const add=event.target.closest('[data-add-allocation]');if(add){addAllocation(Number(add.dataset.addAllocation));return;}
  const remove=event.target.closest('[data-remove-allocation]');if(!remove)return;
  const row=state.allocations.find(r=>r.key===Number(remove.dataset.removeAllocation));if(!row)return;
  if(row.parcelId!=null)row.volume=0;else state.allocations=state.allocations.filter(r=>r!==row);
  renderAllocationEditor();schedulePreview();
});
$("review-form").addEventListener("submit", e=>{e.preventDefault();schedulePreview();});
$("apply-button").addEventListener("click", applyCorrection);
$("retry-review").addEventListener("click",()=>{if(state.detail)openReview(key(state.detail.note));});
$("continue").addEventListener("click", () => switchView("books"));
$("nav-books").addEventListener("click", () => switchView("books"));
$("nav-notes").addEventListener("click", () => switchView("notes"));
$("nav-pending").addEventListener("click", () => switchView("pending"));
$("mobile-view").addEventListener("change",()=>switchView($("mobile-view").value));
$("back-notes").addEventListener("click", () => switchView("notes"));
$("book-filter").addEventListener("submit", e => {e.preventDefault();loadBooks();});
const initialPeriod = previousMonthPeriod();
$("from").value = initialPeriod.from;
$("until").value = initialPeriod.until;
initialize();
