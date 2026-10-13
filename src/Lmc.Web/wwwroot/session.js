"use strict";
let sessionPromise=null,sessionResolve=null,sessionToken=null;
async function refreshOperatorMenu(){
  const response=await fetch('/api/session');if(!response.ok)return;
  const info=await response.json();
  document.getElementById('operator-menu').hidden=!(info.requiresLogin&&info.authenticated);
  document.getElementById('operator-name').textContent=info.username||'Operador';
  document.getElementById('change-password').hidden=!info.canChangePassword;
  document.getElementById('logout').hidden=!(info.requiresLogin&&info.authenticated);
  return info;
}
async function authenticateSession() {
  if(sessionPromise)return sessionPromise;
  sessionPromise=new Promise(resolve=>{sessionResolve=resolve;});
  const dialog=document.getElementById('session-dialog');
  dialog.showModal();document.getElementById('session-error').textContent='Carregando acesso…';
  try {
    const response=await fetch('/api/session');if(!response.ok)throw new Error('Não foi possível consultar a sessão.');
    const info=await response.json();sessionToken=info.token;document.getElementById('session-error').textContent='';
  } catch(error){document.getElementById('session-error').textContent=error.message;}
  return sessionPromise;
}
document.getElementById('session-dialog').addEventListener('cancel',event=>event.preventDefault());
document.getElementById('session-form').addEventListener('submit',async event=>{
  event.preventDefault();const button=document.getElementById('session-submit');button.disabled=true;
  const password=document.getElementById('session-password');
  try {
    if(!sessionToken){const session=await fetch('/api/session');if(!session.ok)throw new Error('Não foi possível consultar a sessão.');sessionToken=(await session.json()).token;}
    const response=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-LMC-CSRF':sessionToken},body:JSON.stringify({username:document.getElementById('session-user').value,password:password.value})});
    password.value='';
    if(!response.ok){if(response.status===403)sessionToken=null;throw new Error(response.status===429?'Muitas tentativas. Aguarde um minuto antes de tentar novamente.':(await response.json()).message||'Não foi possível entrar.');}
    // O token antiforgery muda de identidade depois do login.
    const info=await fetch('/api/security');if(!info.ok)throw new Error('Não foi possível preparar a sessão. Tente entrar novamente.');
    state.csrf=(await info.json()).token;
    await refreshOperatorMenu().catch(()=>{});
    document.getElementById('session-dialog').close();document.getElementById('session-error').textContent='';
    const resolve=sessionResolve;sessionResolve=null;sessionPromise=null;sessionToken=null;resolve?.();
  } catch(error){document.getElementById('session-error').textContent=error.message;}
  finally{button.disabled=false;}
});
let changingPassword=false;
const passwordDialog=document.getElementById('password-dialog');
function closePasswordDialog(){if(!changingPassword)passwordDialog.close();}
document.getElementById('password-close').addEventListener('click',closePasswordDialog);
document.getElementById('password-cancel').addEventListener('click',closePasswordDialog);
passwordDialog.addEventListener('cancel',event=>{if(changingPassword)event.preventDefault();});
passwordDialog.addEventListener('close',()=>{document.getElementById('password-form').reset();document.getElementById('password-error').textContent='';});
document.getElementById('change-password').addEventListener('click',async()=>{
  document.getElementById('operator-menu').open=false;
  document.getElementById('account-feedback').hidden=true;
  document.getElementById('password-form').reset();document.getElementById('password-error').textContent='';
  document.getElementById('password-operator').textContent=document.getElementById('operator-name').textContent;
  passwordDialog.showModal();document.getElementById('password-current').focus();
  try{
    const info=await refreshOperatorMenu();
    if(!info?.authenticated){closePasswordDialog();await authenticateSession();return;}
    state.csrf=info.token;
  }catch{document.getElementById('password-error').textContent='Não foi possível conferir a sessão. Feche e tente novamente.';}
});
document.getElementById('password-form').addEventListener('submit',async event=>{
  event.preventDefault();if(changingPassword)return;
  const current=document.getElementById('password-current'),next=document.getElementById('password-new'),confirm=document.getElementById('password-confirm');
  const error=document.getElementById('password-error');error.textContent='';
  if(next.value!==confirm.value){error.textContent='A confirmação da nova senha não confere.';confirm.focus();return;}
  const body=JSON.stringify({currentPassword:current.value,newPassword:next.value,confirmPassword:confirm.value});
  changingPassword=true;
  const controls=[...document.getElementById('password-form').querySelectorAll('input,button')];controls.forEach(control=>control.disabled=true);
  document.getElementById('password-submit').textContent='Salvando…';
  try{
    // Não repetir a troca automaticamente após reautenticação ou falha de rede.
    const response=await fetch('/api/account/password',{method:'POST',headers:{'Content-Type':'application/json','X-LMC-CSRF':state.csrf},body});
    const result=await response.json().catch(()=>({}));
    if(!response.ok){
      if(response.status===401){changingPassword=false;passwordDialog.close();await authenticateSession();return;}
      if(response.status===429)throw new Error('Muitas tentativas. Aguarde um minuto antes de tentar novamente.');
      throw new Error(result.message||'Não foi possível salvar a nova senha.');
    }
    current.value=next.value=confirm.value='';changingPassword=false;passwordDialog.close();
    const feedback=document.getElementById('account-feedback');feedback.textContent=result.message;feedback.hidden=false;
    // Uma falha ao atualizar o token não transforma uma troca confirmada em erro.
    try{const info=await refreshOperatorMenu();if(info?.token)state.csrf=info.token;}catch{}
  }catch(failure){
    current.value='';
    error.textContent=failure instanceof TypeError?'A conexão falhou e não foi possível confirmar a troca. Se a sessão expirar, tente entrar com a nova senha.':failure.message;
  }finally{
    changingPassword=false;controls.forEach(control=>control.disabled=false);document.getElementById('password-submit').textContent='Salvar nova senha';
  }
});
refreshOperatorMenu().catch(()=>{});
document.getElementById('logout').addEventListener('click',async()=>{
  if(state.applying||state.uncertain||bookState.saving||bookState.uncertain||bookDeleteState.deleting||bookDeleteState.uncertain)return;
  try{await api('/api/logout',{method:'POST',headers:{'X-LMC-CSRF':state.csrf}});location.reload();}
  catch(error){document.getElementById('connection').textContent=error.message;}
});
