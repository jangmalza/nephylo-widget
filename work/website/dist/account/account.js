import {t,text,date,getLocale,onLanguageChange,translateDocument} from './localization.js?v=20261015-free-access';
import {MembershipApi,membershipFreshness,checkedMembership} from './membership-api.js?v=20261027-admin-signin';
import {createGoogleLogin} from './google-auth.js?v=1.16.2';
import {checkedAdminMember,manualEditorValues,checkedManualExpiry,presetManualExpiry,customManualExpiry,manualDescription,billingDescription,internationalDescription,auditDescription} from './admin-members.js?v=20261015-free-access';
const $=id=>document.getElementById(id);
let api=null, busy=false, email='', member=null, memberChecked=0, nextCheck=0, selected=null, pendingGrant=null, grantDraft=null, offset=0, more=false;
const google=createGoogleLogin({getApi:()=>api});
const labels={free:'Free 이용 중',active:'Pro 이용 중',canceled:'해지 예약 · 종료일까지 Pro',expired:'이용기간 종료',revoked:'Pro 권한 해제'};
const grantDurations=['7','30','90','365','custom'];
function renderGrantDuration(){
  const pro=$('grant-plan').value==='pro',disabled=busy||!api?.config||!selected||!pro;
  for(const duration of grantDurations){const button=$('grant-duration-'+duration);button.disabled=disabled;button.setAttribute('aria-pressed',String(pro&&grantDraft?.mode===duration));}
  $('grant-custom-date').hidden=!pro||grantDraft?.mode!=='custom';
  $('grant-expiry').disabled=disabled||grantDraft?.mode!=='custom';
  $('grant-expiry').required=pro&&grantDraft?.mode==='custom';
  text('grant-duration-state',!pro?'Pro를 선택하면 이용기간을 설정할 수 있습니다.':grantDraft?.mode==='keep'?'기존 종료일 유지':grantDraft?.mode==='custom'?'선택한 날짜의 밤 11:59에 종료됩니다.':'선택한 기간은 지금부터 계산합니다.');
  text('grant-expiry-preview',()=>!pro?'':grantDraft?.expiry?t('종료: {date}',{date:date(grantDraft.expiry)}):t(grantDraft?.error||'종료 날짜를 선택해 주세요.'));
}
function controls(){
  for(const control of document.querySelectorAll('input,select:not([data-language-select]),button')) control.disabled=busy||!api?.config;
  const emailDisabled=busy||!api?.config?.emailEnabled;
  for(const id of ['email','code','verify-code','change-email']) $(id).disabled=emailDisabled;
  renderGrantDuration();
  const send=api?.otpStatus('send')||{seconds:0,pending:false},verify=api?.otpStatus('verify')||{seconds:0,pending:false};
  $('resend-code').disabled=$('send-code').disabled=emailDisabled||send.pending||send.seconds>0;
  $('verify-code').disabled=emailDisabled||verify.pending||verify.seconds>0;
  text('resend-code',()=>send.seconds?t('{seconds}초 후 다시 받기',{seconds:send.seconds}):t('다시 받기'));
  text('send-code',()=>send.seconds?t('{seconds}초 후 받기',{seconds:send.seconds}):t('인증번호 받기'));
  text('verify-code',()=>verify.seconds?t('{seconds}초 후 인증',{seconds:verify.seconds}):t('인증하고 로그인'));
  $('send-cooldown').hidden=!send.seconds;$('verify-cooldown').hidden=!verify.seconds;
  text('send-cooldown',()=>send.seconds?t('인증번호 발송 대기 중입니다. {seconds}초 후 다시 요청할 수 있습니다.',{seconds:send.seconds}):'');
  text('verify-cooldown',()=>verify.seconds?t('인증 시도 대기 중입니다. {seconds}초 후 다시 인증할 수 있습니다.',{seconds:verify.seconds}):'');
  $('previous-page').disabled=busy||offset===0;
  $('next-page').disabled=busy||!more;
  $('google-login').disabled=busy||!api?.config?.googleEnabled;
  text('google-note',api?.config?.googleEnabled?'Google 계정 선택 창이 열립니다. 관리자 계정을 선택해 주세요.':'Google 로그인 연결 준비 중입니다. 연결 후 이 버튼으로 간편하게 로그인할 수 있습니다.');
  text('email-note',api?.config?.emailEnabled?'이메일로 받은 6자리 인증번호를 10분 안에 입력하세요. 처음 인증하면 계정이 만들어집니다.':'이메일 로그인은 준비 중입니다.');
  $('cancel-google').hidden=!google.active;
  $('cancel-google').disabled=!google.active;
  $('retry-session').disabled=busy||!api?.hasRestorableSession;
  $('session-storage-note').hidden=!api?.storageNotice;
  text('session-storage-note',api?.storageNotice||'');

}
async function run(target,work){if(busy)return;busy=true;controls();text(target,'');try{await work();}catch(error){const message=error.message||'작업을 완료하지 못했습니다.';if(!api?.session && member){signedOut();text('login-message',message);}else{text(target,message);if(error.status===403){$('admin-panel').hidden=true;$('members').replaceChildren();selected=null;pendingGrant=null;if(member)member.is_admin=false;}}}finally{busy=false;controls();}}
function signedOut(){member=null;selected=null;pendingGrant=null;grantDraft=null;email='';$('login-card').hidden=false;$('member-card').hidden=true;$('admin-panel').hidden=true;$('grant-form').hidden=true;$('grant-review').hidden=true;$('members').replaceChildren();$('code').value='';$('code-form').hidden=true;$('email-form').hidden=false;}
function hideAdmin(){selected=null;pendingGrant=null;grantDraft=null;$('admin-panel').hidden=true;$('grant-form').hidden=true;$('grant-review').hidden=true;$('members').replaceChildren();}
function renderMember(value){member=value;memberChecked=performance.now();nextCheck=memberChecked+300000;$('session-restore-note').hidden=true;$('retry-session').hidden=true;$('login-card').hidden=true;$('member-card').hidden=false;$('admin-panel').hidden=!value.is_admin;if(!value.is_admin)hideAdmin();text('member-email',()=>value.email);text('plan-badge',value.plan==='pro'?'Pro':'Free');text('member-status',labels[value.status]||'Free 이용 중');text('member-expiry',()=>date(value.expires_at));text('member-message',value.is_admin?'서버에서 관리자 권한을 확인했습니다. 아래에서 기존 회원 정보를 관리할 수 있습니다.':'이 계정에는 관리자 권한이 없습니다. 위젯은 로그인 없이 무료로 이용할 수 있습니다.');$('code').value='';}
let listedMembers=[];
const effective=item=>item.plan==='pro'?t('Pro · {date}까지',{date:date(item.expires_at)}):'Free';
const manual=value=>manualDescription(value,t,getLocale());
// Keep genuine payment history visible to admins even while new overseas checkout is hidden.
const paid=item=>t('국내 · Toss Payments')+'\n'+billingDescription(item.billing,t,getLocale())+(item.international&&item.international.status!=='none'?'\n\n'+t('해외 · Lemon Squeezy')+'\n'+internationalDescription(item.international,t,getLocale()):'');
function renderMembers(){
  $('members').replaceChildren();
  for(const item of listedMembers){const row=document.createElement('tr');for(const value of [item.email,effective(item)+'\n'+t(labels[item.status]||item.status),manual(item.manual),paid(item)]){const cell=document.createElement('td');cell.textContent=value;row.append(cell);}const action=document.createElement('td'),button=document.createElement('button');button.type='button';button.className='secondary';button.textContent=t('수동 권한 · 이력');button.addEventListener('click',()=>run('admin-message',()=>selectMember(item)));action.append(button);row.append(action);$('members').append(row);}
  if(!listedMembers.length){const row=document.createElement('tr'),cell=document.createElement('td');cell.colSpan=5;cell.textContent=t('검색된 회원이 없습니다.');row.append(cell);$('members').append(row);}
  text('page-status',()=>t('{page}페이지',{page:Math.floor(offset/20)+1}));
}
async function loadMembers(){
  const result=await api.rpc('admin_list_members',{p_search:$('member-search').value.trim(),p_offset:offset,p_limit:20});
  if(!result||!Array.isArray(result.members)||typeof result.has_more!=='boolean')throw new Error('회원 목록을 확인하지 못했습니다.');
  listedMembers=result.members.map(checkedAdminMember);more=result.has_more;renderMembers();controls();
}
async function selectMember(item){
  const values=manualEditorValues(item);selected=item;pendingGrant=null;grantDraft=values.plan==='pro'&&values.expiry?{mode:'keep',expiry:values.expiry}:null;text('grant-email',()=>item.email);$('grant-plan').value=values.plan;$('grant-expiry').value='';$('grant-reason').value='';
  text('grant-sources',()=>t('현재 적용: {effective}\n수동 부여: {manual}\n결제: {billing}',{effective:effective(item),manual:manual(item.manual),billing:paid(item)}));
  $('grant-form').hidden=false;$('grant-review').hidden=true;text('grant-audit','권한 변경 이력을 불러오고 있습니다.');controls();
  try{const result=await api.rpc('admin_get_membership_audit',{p_user_id:item.user_id,p_limit:10});if(!result||!Array.isArray(result.entries))throw new Error('권한 변경 이력을 확인하지 못했습니다.');text('grant-audit',()=>result.entries.length?result.entries.map(entry=>auditDescription(entry,t,getLocale())).join('\n\n'):t('아직 수동 권한 변경 이력이 없습니다.'));}
  catch(error){text('grant-audit','이력을 불러오지 못했습니다. 회원을 다시 선택해 주세요.');if(error.status===403)throw error;}
}
async function updateMembership(){const value=await api.membership();renderMember(value);if(value.is_admin)await loadMembers();}
async function restoreSession(){
  if(!api?.hasRestorableSession)return;
  $('session-restore-note').hidden=false;$('retry-session').hidden=true;
  text('session-restore-message','로그인 상태와 이용 권한을 확인하고 있습니다.');
  let value;
  try{value=await api.restore();}
  catch(error){
    signedOut();
    const retry=api.hasRestorableSession;
    $('session-restore-note').hidden=!retry;$('retry-session').hidden=!retry;
    text(retry?'session-restore-message':'login-message',retry?'로그인 상태를 확인하지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요.':'로그인이 만료되었습니다. 다시 로그인해 주세요.');
    return;
  }
  $('session-restore-note').hidden=true;$('retry-session').hidden=true;
  if(!value)return;
  renderMember(value);
  if(value.is_admin){try{await loadMembers();}catch(error){if(!api?.session){signedOut();text('login-message',error.message||'로그인이 만료되었습니다. 다시 로그인해 주세요.');return;}hideAdmin();text('member-message',error.message||'회원 목록을 확인하지 못했습니다.');}}
}
$('retry-session').addEventListener('click',()=>run('session-restore-message',restoreSession));
$('email-form').addEventListener('submit',event=>{event.preventDefault();return run('login-message',async()=>{const requestedEmail=$('email').value.trim();await api.sendCode(requestedEmail);email=requestedEmail;$('email-form').hidden=true;$('code-form').hidden=false;text('login-message','인증번호 발송을 요청했습니다. 받은편지함과 스팸함을 확인해 주세요.');$('code').focus();});});
$('code-form').addEventListener('submit',event=>{event.preventDefault();return run('login-message',async()=>{const value=await api.verify(email,$('code').value.trim());renderMember(value);if(value.is_admin)await loadMembers();});});
$('google-login').addEventListener('click',()=>run('login-message',async()=>{const pending=google.start();controls();text('login-message',api?.config?.emailEnabled?'Google 로그인 창에서 계속해 주세요. 창이 닫혔거나 진행되지 않으면 아래에서 취소하고 이메일 인증을 이용할 수 있습니다.':'Google 로그인 창에서 계속해 주세요. 창이 닫혔거나 진행되지 않으면 아래에서 취소한 뒤 다시 시도해 주세요.');const value=await pending;renderMember(value);if(value.is_admin)await loadMembers();}));
$('cancel-google').addEventListener('click',()=>google.cancel());
$('resend-code').addEventListener('click',()=>run('login-message',async()=>{await api.sendCode(email);text('login-message','새 인증번호 발송을 요청했습니다. 가장 최근 번호를 입력해 주세요.');}));
$('change-email').addEventListener('click',()=>{if(busy)return;$('code-form').hidden=true;$('email-form').hidden=false;$('code').value='';text('login-message','');controls();});
$('refresh-membership').addEventListener('click',()=>run('member-message',updateMembership));
$('sign-out').addEventListener('click',()=>run('member-message',async()=>{await api.signOut();signedOut();text('login-message','로그아웃했습니다. 무료 버전은 계속 사용할 수 있습니다.');}));
$('search-form').addEventListener('submit',event=>{event.preventDefault();offset=0;run('admin-message',loadMembers);});
$('previous-page').addEventListener('click',()=>{offset=Math.max(0,offset-20);run('admin-message',loadMembers);});
$('next-page').addEventListener('click',()=>{offset+=20;run('admin-message',loadMembers);});
$('grant-plan').addEventListener('change',()=>{if(busy||!selected)return;if($('grant-plan').value==='pro'&&!grantDraft)grantDraft={mode:'30',expiry:presetManualExpiry(30)};controls();});
for(const duration of grantDurations)$('grant-duration-'+duration).addEventListener('click',()=>{
  if(busy||!api?.config||!selected||$('grant-plan').value!=='pro')return;
  if(duration==='custom'){if(grantDraft?.mode!=='custom'){grantDraft={mode:'custom',expiry:''};$('grant-expiry').value='';}}
  else{grantDraft={mode:duration,expiry:presetManualExpiry(Number(duration))};$('grant-expiry').value='';}
  text('admin-message','');controls();if(duration==='custom')$('grant-expiry').focus();
});
function updateCustomGrantExpiry(){
  if(busy||!selected||$('grant-plan').value!=='pro'||grantDraft?.mode!=='custom')return;
  grantDraft={mode:'custom',expiry:''};
  try{grantDraft.expiry=customManualExpiry($('grant-expiry').value);}catch(error){grantDraft.error=error.message;}
  renderGrantDuration();
}
$('grant-expiry').addEventListener('input',updateCustomGrantExpiry);
$('grant-expiry').addEventListener('change',updateCustomGrantExpiry);
$('cancel-grant').addEventListener('click',()=>{selected=null;pendingGrant=null;grantDraft=null;$('grant-form').hidden=true;$('grant-review').hidden=true;});
$('grant-form').addEventListener('submit',event=>{
  event.preventDefault();if(busy||!selected)return;
  const plan=$('grant-plan').value,reason=$('grant-reason').value.trim();let expiry=null;
  if(!reason){text('admin-message','변경 사유를 입력해 주세요.');return;}
  if(plan==='pro'){try{expiry=checkedManualExpiry(grantDraft?.expiry);}catch(error){text('admin-message',error.message);return;}}
  pendingGrant={p_user_id:selected.user_id,p_plan:plan,p_expires_at:expiry,p_reason:reason};
  const reviewed=selected;
  text('grant-summary',()=>t('{email}\n수동 권한: {before} → {after}\n결제 구독과 이미 결제한 이용기간은 변경하지 않습니다.\n사유: {reason}',{email:reviewed.email,before:manual(reviewed.manual),after:plan==='pro'?t('Pro · {date}까지',{date:date(expiry)}):t('수동 Pro 권한 해제'),reason}));
  $('grant-form').hidden=true;$('grant-review').hidden=false;
});
$('back-grant').addEventListener('click',()=>{pendingGrant=null;$('grant-form').hidden=false;$('grant-review').hidden=true;});
$('confirm-grant').addEventListener('click',()=>run('admin-message',async()=>{if(!pendingGrant)return;await api.rpc('admin_set_membership',pendingGrant);pendingGrant=null;selected=null;grantDraft=null;$('grant-form').hidden=true;$('grant-review').hidden=true;await updateMembership();text('admin-message','이전 버전용 수동 권한 기록을 변경했습니다. 1.25.20 이후 무료 위젯의 기능에는 영향을 주지 않으며 결제 기록은 별도로 유지됩니다.');}));
function checkMembership(){
  if(!member||!api?.session)return;
  const now=performance.now(),freshness=membershipFreshness(member,now-memberChecked);
  if(freshness!=='current'){text('plan-badge','확인 필요');text('member-status',freshness==='expired'?'이용기간이 끝났습니다. 권한을 다시 확인해 주세요.':'서버에서 이용 권한을 다시 확인해야 합니다.');if(freshness==='stale')hideAdmin();}
  if(!busy&&!document.hidden&&now>=nextCheck){nextCheck=now+300000;run('member-message',updateMembership);}
}
setInterval(()=>{controls();checkMembership();},1000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)checkMembership();});
text('grant-free-label','수동 Pro 없음');text('grant-pro-label','수동 Pro 부여');
translateDocument();
onLanguageChange(()=>{controls();if(!$('admin-panel').hidden)renderMembers();});
busy=true;controls();
try{
  const response=await fetch('config.json',{cache:'no-store',credentials:'same-origin',redirect:'error'});
  if(!response.ok)throw new Error('인증 서버 설정을 불러오지 못했습니다.');
  api=new MembershipApi(await response.json());
  if(api.config?.emailEnabled||api.config?.googleEnabled){$('setup-note').hidden=true;}
  else if(api.config){text('setup-title','로그인 준비 중');text('setup-message','로그인 서비스를 준비하고 있습니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.');}
  else{text('setup-title','로그인 연결 준비 중');text('setup-message','아직 인증 서버가 연결되지 않았습니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.');}
  await restoreSession();
}catch(error){$('setup-note').hidden=false;text('setup-title','로그인 연결을 확인해 주세요');text('setup-message',error.message);}
finally{busy=false;controls();}
