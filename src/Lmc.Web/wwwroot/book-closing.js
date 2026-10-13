"use strict";
const closingReview={touched:new Set(),ignored:new Map(),suggestions:new Map(),rendered:new Map()};
const closingLiters=new Intl.NumberFormat("pt-BR",{maximumFractionDigits:4});
function assessClosing(stock,closing,capacity) {
  if(!Number.isFinite(stock)||stock<0||!Number.isFinite(closing)||closing<0)return null;
  const within=value=>{
    if(!Number.isFinite(value)||value<=0||value>1000000000||(capacity>0&&value>capacity))return false;
    const physical=Math.round(value*10000),book=Math.round(stock*10000);
    return Math.abs(physical-book)*1000<=book*6;
  };
  if(within(closing)||(closing===0&&stock===0))return null;
  const book=Math.round(stock*10000),limit=Math.round((capacity>0?capacity:1000000000)*10000);
  const min=stock>0?Math.ceil(book*994/1000)/10000:null;
  const max=stock>0?Math.floor(Math.min(book*1006/1000,limit))/10000:null;
  const result={stock,closing,percent:stock>0?(closing-stock)/stock*100:null,min,max,suggestion:null,ambiguous:false};
  if(stock===0||closing===0||min>max)return result;
  const raw=String(closing);
  if(!/^\d+(\.\d{1,4})?$/.test(raw))return result;
  const strong=new Map(),other=new Map();
  const add=(map,text,reason)=>{
    const s=String(text);
    if(!/^(0|[1-9]\d*)(\.\d{1,4})?$/.test(s))return;
    const value=Number(s);
    if(value!==closing&&within(value)&&!map.has(value))map.set(value,{value,reason});
  };
  for(const shift of [-3,-2,-1,1,2,3]) {
    const shifted=closing*10**shift,rounded=Math.round(shifted*10000)/10000;
    if(Math.abs(shifted-rounded)<1e-8)add(strong,rounded,"possível vírgula deslocada ou zero a mais/menos");
  }
  for(let i=0;i<raw.length-1;i++) {
    if(raw[i]!=='.'&&raw[i+1]!=='.'&&raw[i]!==raw[i+1])add(strong,raw.slice(0,i)+raw[i+1]+raw[i]+raw.slice(i+2),"possível troca de posição entre dois dígitos");
    if(raw[i]===raw[i+1]&&raw[i]!=='.')add(strong,raw.slice(0,i)+raw.slice(i+1),"possível dígito repetido");
  }
  if(!strong.size) {
    for(let i=0;i<raw.length;i++) {
      if(raw[i]==='.')continue;
      add(other,raw.slice(0,i)+raw.slice(i+1),"possível dígito a mais");
      for(let digit=0;digit<=9;digit++)add(other,raw.slice(0,i)+digit+raw.slice(i+1),"possível dígito incorreto");
    }
    for(let i=0;i<=raw.length;i++)for(let digit=0;digit<=9;digit++)add(other,raw.slice(0,i)+digit+raw.slice(i),"possível dígito faltando");
  }
  const candidates=[...(strong.size?strong:other).values()];
  result.suggestion=candidates.length===1?candidates[0]:null;result.ambiguous=candidates.length>1;
  return result;
}
function resetClosingReviews() {for(const value of Object.values(closingReview))value.clear();}
function closingAssessment(tankId) {
  const p=bookState.preparation,t=p?.tanks.find(t=>t.tankId===tankId);
  if(!t||p.blockers.length)return null;
  const opening=$("book-opening-"+tankId),closing=$("book-closing-"+tankId);
  if(!opening||!closing||opening.value===""||closing.value===""||opening.validity.badInput||closing.validity.badInput)return null;
  const stock=Number(opening.value)+p.documents.filter(x=>x.tankId===tankId).reduce((sum,x)=>sum+x.volume,0)-currentBookPumps().filter(x=>x.tankId===tankId).reduce((sum,x)=>sum+x.sales,0);
  const assessment=assessClosing(stock,Number(closing.value),t.capacity);
  return assessment?{...assessment,signature:JSON.stringify([p.branch,p.date,p.productId,p.version,tankId,opening.value,closing.value,stock,t.capacity])}:null;
}
function clearClosingReview(tankId) {
  for(const value of Object.values(closingReview))value.delete(tankId);
  const row=$("closing-review-row-"+tankId);if(row)row.hidden=true;
  $("book-closing-"+tankId)?.setAttribute("data-closing-warning","false");
}
function renderClosingReview(tankId) {
  const row=$("closing-review-row-"+tankId),content=$("closing-review-"+tankId);
  if(!row||!content)return;
  const check=closingAssessment(tankId);
  const show=check&&closingReview.ignored.get(tankId)!==check.signature;
  $("book-closing-"+tankId)?.setAttribute("data-closing-warning",String(!!show));
  if(!show){row.hidden=true;closingReview.suggestions.delete(tankId);closingReview.rendered.delete(tankId);return;}
  if(closingReview.rendered.get(tankId)===check.signature&&!row.hidden)return;
  row.hidden=false;closingReview.rendered.set(tankId,check.signature);closingReview.suggestions.delete(tankId);
  if(check.suggestion)closingReview.suggestions.set(tankId,{signature:check.signature,value:check.suggestion.value});
  const percent=check.percent==null?"O estoque escritural zero não permite calcular o percentual.":`${check.percent<0?"Perda":"Sobra"} de ${bookDecimal(Math.abs(check.percent))}% · referência de 0,6%.`;
  const range=check.min!=null&&check.max>=check.min?`Faixa de referência: ${closingLiters.format(check.min)} a ${closingLiters.format(check.max)} L.`:"Confira a abertura, as entradas, as vendas e a capacidade do tanque.";
  content.innerHTML=`<strong>Tanque ${tankId}: confira o fechamento</strong><p>${percent} ${range}</p>
    ${check.suggestion?`<p>Você digitou <b>${closingLiters.format(check.closing)} L</b>. Confira se a medição era <b>${closingLiters.format(check.suggestion.value)} L</b>: ${check.suggestion.reason}.</p><div class="closing-review-actions"><button type="button" class="button review-button" data-closing-suggestion="${tankId}">Usar ${closingLiters.format(check.suggestion.value)} L</button>`:`<p>${check.ambiguous?"Há mais de uma correção de digitação possível.":"Não foi identificada uma correção única de digitação."} Confira o volume na medição antes de alterar.</p><div class="closing-review-actions">`}
    <button type="button" class="button secondary" data-keep-closing="${tankId}">Manter valor informado</button></div>`;
}
function reviewClosingInput(tankId) {
  if(bookState.saving||bookState.uncertain)return;
  closingReview.touched.add(tankId);renderClosingReview(tankId);
}
function refreshClosingReviews() {for(const tankId of closingReview.touched)renderClosingReview(tankId);}
function applyClosingSuggestion(tankId) {
  if(bookState.saving||bookState.uncertain)return;
  const suggestion=closingReview.suggestions.get(tankId),current=closingAssessment(tankId);
  if(!suggestion||!current||suggestion.signature!==current.signature||suggestion.value!==current.suggestion?.value){renderClosingReview(tankId);return;}
  $("book-closing-"+tankId).value=String(suggestion.value);clearClosingReview(tankId);scheduleBookPreview();
}
function keepClosingValue(tankId) {
  if(bookState.saving||bookState.uncertain)return;
  const current=closingAssessment(tankId);if(current)closingReview.ignored.set(tankId,current.signature);
  renderClosingReview(tankId);
}
