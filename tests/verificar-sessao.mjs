// Sessão expirada durante um rascunho: servidor simulado, sem banco.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const elements=new Map(),calls=[];let logged=false,loginStatus=200,checks=0;
function element(id){if(!elements.has(id))elements.set(id,{value:'',textContent:'',hidden:false,disabled:false,open:false,listeners:{},addEventListener(n,f){this.listeners[n]=f;},showModal(){this.open=true;},close(){this.open=false;}});return elements.get(id);}
const context=vm.createContext({console,Intl,URLSearchParams,document:{getElementById:element},fetch:async(url,options={})=>{
  calls.push({url,body:options.body,headers:options.headers});let status=200,data={};
  if(url==='/api/session')data={token:'anonymous',requiresLogin:true};
  else if(url==='/api/login'){status=loginStatus;data={message:'Usuário ou senha inválidos.'};if(status===200)logged=true;}
  else if(!logged){status=401;data={message:'Entre para continuar.'};}
  else if(url==='/api/security')data={token:'authenticated'};
  else data={ok:true};
  return {ok:status<400,status,text:async()=>JSON.stringify(data),json:async()=>data};
}});
const run=code=>vm.runInContext(code,context),check=(ok,label)=>{assert.ok(ok,label);checks++;console.log(`OK: ${label}`);};
run(readFileSync(new URL('../src/Lmc.Web/wwwroot/session.js',import.meta.url),'utf8'));
const app=readFileSync(new URL('../src/Lmc.Web/wwwroot/app.js',import.meta.url),'utf8');run(app.slice(0,app.indexOf('$("filters").addEventListener')));
element('book-closing-1').value='19406';
const first=run(`api('/api/lmc/save',{method:'POST',headers:{'X-LMC-CSRF':'expired'},body:'{"operationId":"same-operation","tanks":[{"closing":19406}]}'})`);
const second=run(`api('/api/context')`);await new Promise(resolve=>setImmediate(resolve));
check(element('session-dialog').open&&calls.filter(c=>c.url==='/api/session').length===1,'consultas concorrentes abrem apenas uma tela de login');
element('session-user').value='operador';element('session-password').value='incorreta';loginStatus=401;
await element('session-form').listeners.submit({preventDefault(){}});
check(element('session-dialog').open&&element('session-error').textContent.includes('inválidos')&&element('session-password').value==='','senha errada mantém a tela e limpa a senha digitada');
check(element('book-closing-1').value==='19406','rascunho do LMC permanece intacto durante o login');
loginStatus=200;element('session-password').value='senha de teste';await element('session-form').listeners.submit({preventDefault(){}});await Promise.all([first,second]);
const writes=calls.filter(c=>c.url==='/api/lmc/save');
check(!element('session-dialog').open&&writes.length===2&&writes[0].body===writes[1].body,'login retoma solicitação rejeitada com o mesmo identificador e dados');
check(writes[1].headers['X-LMC-CSRF']==='authenticated','retomada usa o token da nova identidade autenticada');
console.log(`${checks} verificações passaram; nenhuma gravação.`);
