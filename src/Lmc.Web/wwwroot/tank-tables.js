"use strict";
const tankSettings={catalog:null,branch:null,request:0,busy:false,preview:null,previewRequest:0};
function tankTableSummary(table) {
  const first=table.rows[0],last=table.rows.at(-1),n=v=>tankTableNumber.format(v);
  return `${table.rows.length} medidas · ${n(first.height)} a ${n(last.height)} cm · ${n(first.volume)} a ${n(last.volume)} L`;
}
function tankTablePreview(table) {
  const rows=table.rows.length<=6?table.rows:[...table.rows.slice(0,3),null,...table.rows.slice(-3)];
  return `<p class="csv-summary">${esc(tankTableSummary(table))}</p><table class="csv-sample"><thead><tr><th>Altura (cm)</th><th class="numeric">Volume (L)</th></tr></thead><tbody>${rows.map(r=>r?`<tr><td>${tankTableNumber.format(r.height)}</td><td class="numeric">${tankTableNumber.format(r.volume)}</td></tr>`:'<tr><td colspan="2" class="csv-ellipsis">… demais medidas do arquivo …</td></tr>').join("")}</tbody></table>`;
}
function setTankSettingsBusy(busy) {
  tankSettings.busy=busy;
  document.querySelectorAll('#tables-view input,#tables-view button,#tables-view select').forEach(e=>e.disabled=busy);
  for(const id of ['nav-notes','nav-books','nav-pending','nav-tables','mobile-view'])$(id).disabled=busy;
  $("branch").disabled=busy||!state.branch||(!$("books-view").hidden&&!$("book-editor-pane").hidden);
  if(!busy)updateTankBindingButtons();
}
function invalidateTankCsvPreview() {
  ++tankSettings.previewRequest;tankSettings.preview=null;
  $("tank-csv-preview").hidden=true;$("import-tank-csv").hidden=true;
}
async function loadTankTables() {
  if(!state.branch||tankSettings.busy)return;
  const request=++tankSettings.request,branch=state.branch;
  tankSettings.catalog=null;tankSettings.branch=branch;
  $("tank-bindings").setAttribute('aria-busy','true');
  $("tank-bindings").innerHTML='<p class="empty">Consultando os tanques da unidade…</p>';
  $("tank-table-library").innerHTML='';$("tank-binding-summary").textContent='Carregando…';
  showError("tank-tables-error","");
  try {
    const catalog=await api(`/api/tank-tables?${new URLSearchParams({branch})}`);
    if(request!==tankSettings.request||branch!==state.branch)return;
    tankSettings.catalog=catalog;renderTankSettings();
  }catch(error){if(request===tankSettings.request&&branch===state.branch){
    showError("tank-tables-error",error.message);$("tank-binding-summary").textContent='Consulta não concluída';
    $("tank-bindings").innerHTML='<p class="empty">Use Atualizar para tentar novamente.</p>';
  }}finally{if(request===tankSettings.request)$("tank-bindings").setAttribute('aria-busy','false');}
}
function renderTankSettings() {
  const c=tankSettings.catalog;
  if(!c)return;
  const configured=c.tanks.filter(t=>t.tableId).length;
  $("tank-binding-summary").textContent=`${configured} de ${c.tanks.length} tanque(s) com tabela escolhida`;
  $("tank-bindings").innerHTML=c.tanks.map(t=>{
    const table=c.tables.find(x=>x.id===t.tableId);
    return `<article class="tank-binding" data-tank-row="${t.tankId}"><div class="tank-binding-label"><div><strong>Tanque ${t.tankId}</strong><span>${esc(t.product)}</span></div><span class="badge ${table?'green':'amber'}">${table?'Configurado':'Escolher tabela'}</span></div><small>Capacidade cadastrada: ${t.capacity==null?'não informada':tankTableNumber.format(t.capacity)+' L'}${t.active?'':' · Inativo'}</small><div class="tank-binding-choice"><div><label for="tank-table-choice-${t.tankId}">Tabela de referência</label><select id="tank-table-choice-${t.tankId}" data-tank-choice="${t.tankId}"><option value="">Sem tabela vinculada</option>${c.tables.map(x=>`<option value="${esc(x.id)}" ${x.id===t.tableId?'selected':''}>${esc(x.name)} · até ${tankTableNumber.format(x.rows.at(-1).volume)} L</option>`).join('')}</select></div><button class="button secondary" type="button" data-save-tank-table="${t.tankId}" disabled>Salvar escolha</button></div><p class="tank-choice-detail" id="tank-choice-detail-${t.tankId}">${table?esc(tankTableSummary(table)):'Importe o CSV e selecione a tabela correspondente a este tanque.'}</p></article>`;
  }).join('')||'<p class="empty">Nenhum tanque cadastrado nesta unidade.</p>';
  $("tank-table-library").innerHTML=c.tables.length?c.tables.map(t=>`<details class="tank-library-item"><summary><span><strong>${esc(t.name)}</strong><small>${esc(t.fileName)}</small></span><span>${t.rows.length} medidas · até ${tankTableNumber.format(t.rows.at(-1).volume)} L</span></summary>${tankTablePreview(t)}</details>`).join(''):'<p class="empty">Nenhuma tabela importada. Comece escolhendo um arquivo CSV.</p>';
  updateTankBindingButtons();
}
function updateTankBindingButtons() {
  document.querySelectorAll('[data-save-tank-table]').forEach(button=>{
    const tank=tankSettings.catalog?.tanks.find(t=>t.tankId===Number(button.dataset.saveTankTable));
    button.disabled=tankSettings.busy||!tank||$("tank-table-choice-"+tank.tankId).value===(tank.tableId??'');
  });
}
async function tankSettingsPost(path,payload) {
  if(!state.csrf)state.csrf=(await api('/api/security')).token;
  return api(`/api/tank-tables/${path}`,{method:'POST',headers:{'Content-Type':'application/json','X-LMC-CSRF':state.csrf},body:JSON.stringify(payload)});
}
async function previewTankCsv(event) {
  event.preventDefault();if(tankSettings.busy||!$("tank-import-form").reportValidity())return;
  invalidateTankCsvPreview();showError('tank-tables-feedback','');showError('tank-tables-error','');
  const file=$("tank-csv-file").files[0],request=tankSettings.previewRequest;
  if(!file||file.size>512*1024){showError('tank-tables-error','Escolha um CSV com no máximo 512 KB.');return;}
  setTankSettingsBusy(true);
  try {
    const bytes=await file.arrayBuffer();let csv;
    try{csv=new TextDecoder('utf-8',{fatal:true}).decode(bytes);}catch{csv=new TextDecoder('windows-1252').decode(bytes);}
    const payload={name:$("tank-csv-name").value.trim(),fileName:file.name,csv};
    const table=await tankSettingsPost('preview',payload);
    if(request!==tankSettings.previewRequest)return;
    tankSettings.preview=payload;
    $("tank-csv-preview").innerHTML=`<h3>Confira as medidas</h3>${tankTablePreview(table)}<p class="field-hint">Os valores serão mantidos como estão no CSV.</p>`;
    $("tank-csv-preview").hidden=false;$("import-tank-csv").hidden=false;
  }catch(error){showError('tank-tables-error',error.message);}finally{setTankSettingsBusy(false);}
}
async function importTankCsv() {
  if(tankSettings.busy||!tankSettings.preview)return;
  if(!tankSettings.catalog){showError('tank-tables-error','Atualize a lista de tabelas antes de importar.');return;}
  setTankSettingsBusy(true);showError('tank-tables-error','');
  try {
    const table=await tankSettingsPost('import',{...tankSettings.preview,revision:tankSettings.catalog.revision});
    invalidateTankCsvPreview();$("tank-import-form").reset();
    showError('tank-tables-feedback',`Tabela “${table.name}” disponível. Agora escolha em quais tanques ela será usada.`);
    setTankSettingsBusy(false);await loadTankTables();
  }catch(error){showError('tank-tables-error',error.message);}finally{setTankSettingsBusy(false);}
}
async function saveTankTable(tankId) {
  if(tankSettings.busy||tankSettings.branch!==state.branch||!tankSettings.catalog)return;
  const tank=tankSettings.catalog.tanks.find(t=>t.tankId===tankId);if(!tank)return;
  const branch=state.branch,tableId=$("tank-table-choice-"+tankId).value||null;
  setTankSettingsBusy(true);showError('tank-tables-error','');showError('tank-tables-feedback','');
  try {
    await tankSettingsPost(`assign?${new URLSearchParams({branch})}`,{tankId,productId:tank.productId,tableId,revision:tankSettings.catalog.revision});
    if(branch===state.branch)showError('tank-tables-feedback',`Tanque ${tankId}: ${tableId?'tabela salva. Ao abrir o LMC, a referência usará esta tabela.':'vínculo removido.'}`);
    setTankSettingsBusy(false);await loadTankTables();
  }catch(error){showError('tank-tables-error',error.message);}finally{setTankSettingsBusy(false);}
}
$("nav-tables").addEventListener('click',()=>switchView('tables'));
$("refresh-tank-tables").addEventListener('click',loadTankTables);
$("tank-import-form").addEventListener('submit',previewTankCsv);
$("import-tank-csv").addEventListener('click',importTankCsv);
$("tank-csv-file").addEventListener('change',()=>{invalidateTankCsvPreview();$("tank-csv-name").value=($("tank-csv-file").files[0]?.name??'').replace(/\.csv$/i,'').slice(0,100);});
$("tank-csv-name").addEventListener('input',invalidateTankCsvPreview);
$("tank-bindings").addEventListener('click',event=>{const button=event.target.closest('[data-save-tank-table]');if(button)saveTankTable(Number(button.dataset.saveTankTable));});
$("tank-bindings").addEventListener('change',event=>{
  const select=event.target.closest('[data-tank-choice]');if(!select)return;
  const table=tankSettings.catalog?.tables.find(t=>t.id===select.value);
  $("tank-choice-detail-"+select.dataset.tankChoice).textContent=table?tankTableSummary(table):'Sem referência da tabela para este tanque.';
  updateTankBindingButtons();
});
