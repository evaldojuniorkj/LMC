"use strict";
const bookDeleteState={request:0,branch:null,id:null,batch:false,preview:null,payload:null,deleting:false,uncertain:false};
function lockBookDeletion(locked) {
  $("close-delete-book").disabled=locked;$("cancel-delete-book").disabled=locked;
  $("retry-delete-book").disabled=locked;
}
async function openBookDeletion(branch,id) {
  if(bookDeleteState.deleting||bookDeleteState.uncertain||bookState.saving||bookState.uncertain)return;
  const request=++bookDeleteState.request;
  const batch=Array.isArray(id);
  if(batch&&(id.length===0||id.length>200)){showError("book-feedback","Selecione de 1 a 200 livros para excluir.");return;}
  Object.assign(bookDeleteState,{branch,id,batch,preview:null,payload:null});
  $("delete-book-title").textContent=batch?`Excluir ${id.length} LMC(s)?`:"Excluir LMC?";
  $("delete-book-description").textContent=batch?"Confira todos os livros selecionados. A exclusão do lote é feita de uma vez; se houver impedimento em algum livro, nenhum será excluído.":"Confira o livro antes de confirmar. A exclusão remove este LMC e seus detalhes; as notas de entrada e a automação permanecem disponíveis.";
  lockBookDeletion(false);$("confirm-delete-book").disabled=true;$("confirm-delete-book").textContent="Excluir este LMC";
  $("retry-delete-book").hidden=true;$("delete-book-content").innerHTML="";
  $("delete-book-state").textContent="Conferindo o livro e seus vínculos…";
  if(!$("delete-book-dialog").open)$("delete-book-dialog").showModal();
  try {
    const p=batch?await api('/api/lmc/deletion-batch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({branch,bookIds:id})})
      :await api(`/api/lmc/deletion?${new URLSearchParams({branch,id})}`);
    if(request!==bookDeleteState.request)return;
    bookDeleteState.preview=p;
    bookDeleteState.payload=batch?{branch:p.branch,books:p.books.map(b=>({bookId:b.bookId,version:b.version})),operationId:crypto.randomUUID()}
      :{branch:p.branch,bookId:p.bookId,version:p.version,operationId:crypto.randomUUID()};
    const unit=state.context.branches.find(x=>x.id===p.branch)?.name||`Filial ${p.branch}`;
    $("delete-book-content").innerHTML=(batch?`<div class="delete-book-summary"><span>${esc(unit)}</span><strong>${p.books.length} LMC(s) selecionado(s)</strong></div><div class="delete-batch-list"><table><thead><tr><th>Data</th><th>Combustível</th><th>Folha</th></tr></thead><tbody>${p.books.map(b=>`<tr><td>${date(b.date)}</td><td>${esc(b.product)}</td><td>${b.number??"—"}</td></tr>`).join("")}</tbody></table></div><p>Serão excluídos somente os livros desta lista e seus detalhes. As notas de entrada e a automação permanecem disponíveis.</p>`
      :`<div class="delete-book-summary"><span>${esc(unit)}</span><strong>${esc(p.product)}</strong><p>${date(p.date)} · Folha ${p.number??"—"} · LMC ${p.bookId}</p></div><p>Serão removidos o livro, ${p.tankCount} registro(s) de tanque, ${p.pumpCount} registro(s) de bico e ${p.receiptCount} recebimento(s) deste livro.</p>`)+`
      ${p.followingBooks.length?`<p class="notice warning">Há ${p.followingBooks.length} livro(s) posteriores ${batch?"dos combustíveis selecionados":"deste combustível"}. Após excluir, confira a sequência a partir de ${date(p.followingBooks[0].date)}.</p>`:""}
      <p>A cópia anterior fica no histórico. A recuperação exige assistência; não há botão de desfazer.</p>
      ${p.blockers.length?`<div class="notice error" role="alert">${p.blockers.map(esc).join(" ")}</div>`:""}`;
    $("confirm-delete-book").disabled=p.blockers.length>0;
    $("confirm-delete-book").textContent=batch?`Excluir ${p.books.length} LMC(s)`:"Excluir este LMC";
    $("delete-book-state").textContent=p.blockers.length?"A exclusão está bloqueada pelo vínculo indicado.":batch?"Confirme somente se deseja excluir todos os livros desta lista.":"Confirme somente se deseja excluir este livro.";
  } catch(error) {
    if(request!==bookDeleteState.request)return;
    $("delete-book-state").textContent=error.message;$("retry-delete-book").hidden=false;
  }
}
function closeBookDeletion() {
  if(bookDeleteState.deleting||bookDeleteState.uncertain)return;
  $("delete-book-dialog").close();
}
async function confirmBookDeletion() {
  if(bookDeleteState.deleting||!bookDeleteState.payload||bookDeleteState.preview?.blockers.length)return;
  bookDeleteState.deleting=true;lockBookDeletion(true);$("confirm-delete-book").disabled=true;
  $("delete-book-state").textContent=bookDeleteState.batch?"Excluindo os LMCs e preservando seus históricos…":"Excluindo o LMC e preservando seu histórico…";
  try {
    const batch=bookDeleteState.batch;
    const result=await api(batch?'/api/lmc/delete-batch':'/api/lmc/delete',{method:'POST',headers:{'Content-Type':'application/json','X-LMC-CSRF':state.csrf},body:JSON.stringify(bookDeleteState.payload)});
    bookDeleteState.deleting=false;bookDeleteState.uncertain=false;lockBookDeletion(false);
    $("delete-book-dialog").close();
    invalidateBookPreview();
    const branch=state.context.branches.find(x=>x.id===result.branch);if(branch)branch.lastBookDate=result.lastBookDate;
    $("book-month").value=(batch?result.books[0].date:result.date).slice(0,7);
    const summary=batch?`${result.books.length} LMC(s): ${result.alreadyDeleted?"exclusão do lote já confirmada":"excluídos em lote"}.`:`LMC ${result.bookId} de ${date(result.date)} · ${result.product}: ${result.alreadyDeleted?"exclusão já confirmada":"excluído"}.`;
    showError("book-feedback",`${summary} ${result.followingBooks.length?`Confira a sequência dos ${result.followingBooks.length} livro(s) posteriores.`:"A lista foi atualizada."}`);
    await loadBookLedger();
  } catch(error) {
    bookDeleteState.deleting=false;
    if([400,404,409,422].includes(error.status)) {
      bookDeleteState.uncertain=false;bookDeleteState.payload=null;lockBookDeletion(false);
      $("confirm-delete-book").disabled=true;$("retry-delete-book").hidden=false;
      $("delete-book-state").textContent=error.message;
    } else {
      bookDeleteState.uncertain=true;lockBookDeletion(true);
      $("confirm-delete-book").disabled=false;$("confirm-delete-book").textContent="Confirmar exclusão / tentar novamente";
      $("delete-book-state").textContent="A confirmação não chegou. Tente novamente para consultar ou concluir a mesma exclusão.";
      if(error.status===403) {try{state.csrf=(await api('/api/security')).token;}catch{}}
    }
  }
}
$("books-list").addEventListener("click",event=>{const b=event.target.closest('[data-delete-book]');if(b)openBookDeletion(state.branch,Number(b.dataset.deleteBook));});
$("delete-book").addEventListener("click",()=>{const p=bookState.preparation;if(p?.bookId)openBookDeletion(p.branch,p.bookId);});
$("delete-selected-books").addEventListener("click",()=>openBookDeletion(state.branch,[...bookState.selectedIds]));
$("confirm-delete-book").addEventListener("click",confirmBookDeletion);
$("close-delete-book").addEventListener("click",closeBookDeletion);
$("cancel-delete-book").addEventListener("click",closeBookDeletion);
$("retry-delete-book").addEventListener("click",()=>openBookDeletion(bookDeleteState.branch,bookDeleteState.id));
$("delete-book-dialog").addEventListener("cancel",event=>{if(bookDeleteState.deleting||bookDeleteState.uncertain)event.preventDefault();});
$("delete-book-dialog").addEventListener("close",()=>{++bookDeleteState.request;bookDeleteState.preview=null;bookDeleteState.payload=null;});
