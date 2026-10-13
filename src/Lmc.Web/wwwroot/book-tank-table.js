"use strict";
// Only an explicit branch/tank/product assignment may supply a reference table.
const bookTankTables={branch:null,product:null,tables:[],bindings:[],loading:false,error:false};
const tankTableNumber=new Intl.NumberFormat("pt-BR",{maximumFractionDigits:4});
function nearestTankTableEntries(stock,table) {
  if(!Number.isFinite(stock)||stock<0||!table?.rows?.length)return [];
  const target=Math.round(stock*10000);let distance=Infinity,nearest=[];
  for(const {height,volume} of table.rows) {
    const difference=Math.abs(Math.round(volume*10000)-target);
    if(difference<distance){distance=difference;nearest=[{height,volume}];}
    else if(difference===distance)nearest.push({height,volume});
  }
  return nearest;
}
function renderTankTableReference(stock,tankId) {
  if(bookTankTables.loading)return '<span>—</span><small>Consultando tabela…</small>';
  if(bookTankTables.error)return '<span>—</span><small>Tabela indisponível. Confira o menu Tabelas dos tanques.</small>';
  const current=bookState.preparation;
  const sameContext=bookTankTables.branch===(current?.branch??state.branch)&&bookTankTables.product===current?.productId;
  const binding=sameContext?bookTankTables.bindings.find(b=>b.tankId===tankId):null;
  const table=bookTankTables.tables.find(t=>t.id===binding?.tableId);
  if(!table)return '<span>Sem tabela</span><small>Escolha em Tabelas dos tanques</small>';
  const name=`<small class="tank-table-name">${esc(table.name)}</small>`;
  const nearest=nearestTankTableEntries(stock,table);
  if(!nearest.length)return '<span>—</span><small>Aguardando escritural válido</small>'+name;
  const rows=nearest.map(({height,volume})=>`<strong>${tankTableNumber.format(volume)} L</strong><small>${tankTableNumber.format(height)} cm · ${volume>stock?"+":""}${bookDecimal(volume-stock)} L</small>`).join("");
  const outside=stock<table.rows[0].volume||stock>table.rows.at(-1).volume;
  return rows+(nearest.length>1?'<small>Mesma proximidade</small>':outside?'<small>Escritural fora da faixa da tabela</small>':"")+name;
}
async function loadBookTankTables(branch,product,editorRequest) {
  Object.assign(bookTankTables,{branch,product,tables:[],bindings:[],loading:true,error:false});
  try {
    const result=await api(`/api/tank-tables/references?${new URLSearchParams({branch,product})}`);
    if(editorRequest!==bookState.editorRequest||branch!==state.branch)return;
    if(!Array.isArray(result.tables)||!Array.isArray(result.bindings))throw new Error('Tabela indisponível');
    Object.assign(bookTankTables,{tables:result.tables,bindings:result.bindings});
  } catch(error) {
    if(editorRequest!==bookState.editorRequest||branch!==state.branch)return;
    bookTankTables.error=true;
  } finally {
    if(editorRequest===bookState.editorRequest&&branch===state.branch){
      bookTankTables.loading=false;
      // Recalculate using current form values, without rerendering or resetting edits.
      if(bookState.preparation)updateBookTotals(collectBookRequest());
    }
  }
}
