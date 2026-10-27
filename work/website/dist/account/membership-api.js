const knownStatuses = new Set(['free','active','canceled','expired','revoked']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function publicConfiguration(value) {
  if (!value?.SupabaseUrl && !value?.PublishableKey) return null;
  let url; try { url = new URL(value.SupabaseUrl); } catch { throw new Error('인증 서버 설정을 확인해 주세요.'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/' || ['localhost','127.0.0.1','[::1]'].includes(url.hostname)) throw new Error('인증 서버 설정을 확인해 주세요.');
  const key = String(value.PublishableKey || '');
  let publishable = /^sb_publishable_[A-Za-z0-9_-]{8,}$/.test(key);
  if (!publishable && /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(key)) {
    try { publishable = JSON.parse(atob(key.split('.')[1].replace(/-/g,'+').replace(/_/g,'/'))).role === 'anon'; } catch {}
  }
  if (!publishable) throw new Error('공개 인증 키 설정을 확인해 주세요.');
  return {origin:url.origin,key,emailEnabled:value.EmailEnabled===undefined||value.EmailEnabled===true,googleEnabled:value.GoogleEnabled===true};
}
export function cleanEmail(value) {
  const email = String(value).trim().toLowerCase();
  if (email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new Error('올바른 이메일 주소를 입력해 주세요.');
  return email;
}
export function checkedMembership(value, userId) {
  if (!value || value.user_id !== userId || !uuid.test(userId) || !['free','pro'].includes(value.plan) || !knownStatuses.has(value.status) || typeof value.is_admin !== 'boolean') throw new Error('이용 권한 응답을 확인하지 못했습니다. 다시 로그인해 주세요.');
  const serverTime = Date.parse(value.server_time), expiry = value.expires_at == null ? null : Date.parse(value.expires_at);
  if (!Number.isFinite(serverTime) || (expiry !== null && !Number.isFinite(expiry))) throw new Error('이용기간 응답을 확인하지 못했습니다.');
  const pro = value.plan === 'pro' && ['active','canceled'].includes(value.status) && expiry !== null && expiry > serverTime;
  return {...value,plan:pro?'pro':'free',status:!pro&&['active','canceled'].includes(value.status)?'expired':value.status,remainingMilliseconds:pro?expiry-serverTime:0};
}
export function membershipFreshness(value, elapsedMilliseconds) {
  if (elapsedMilliseconds>=900000) return 'stale';
  if (value.plan==='pro' && elapsedMilliseconds>=value.remainingMilliseconds) return 'expired';
  return 'current';
}
function responseError(status, code) {
  const billingErrors={BILLING_UNAVAILABLE:'결제 연결을 준비하고 있습니다. 아직 청구되지 않습니다.',CONSENT_REQUIRED:'정기결제 안내를 확인하고 동의해 주세요.',CHECKOUT_EXPIRED:'카드 등록 요청이 만료되었습니다. 처음부터 다시 진행해 주세요.',BILLING_PENDING:'결제 결과를 확인 중입니다. 새 결제 대신 결과 다시 확인을 눌러 주세요.',PAYMENT_REVIEW:'결제 결과 확인이 필요합니다. 추가 결제 없이 문의해 주세요.',PAYMENT_FAILED:'결제 승인이 완료되지 않았습니다. 결제 상태를 확인해 주세요.',EMAIL_UNVERIFIED:'이메일 인증을 먼저 완료해 주세요.'};
  if(billingErrors[code])return billingErrors[code];
  if(code==='ALREADY_SUBSCRIBED')return '이미 이용 중이거나 확인 중인 구독이 있습니다. 기존 결제 방식에서 구독 상태를 확인해 주세요.';
  if(code==='SERVER_UNAVAILABLE')return '결제 서버에 연결하지 못했습니다. 잠시 후 결과를 다시 확인해 주세요.';
  if (status === 429) return '요청이 많습니다. 잠시 후 다시 시도해 주세요.';
  if (status === 401) return '인증이 만료되었습니다. 다시 로그인해 주세요.';
  if (status === 403) return '이 작업을 수행할 권한이 없습니다.';
  if (code === 'otp_expired') return '인증번호가 만료되었거나 올바르지 않습니다. 새 번호를 받아 주세요.';
  if (code === 'email_address_not_authorized' || code === 'email_address_invalid' || code === 'over_email_send_rate_limit') return '인증 메일을 보내지 못했습니다. 이메일 주소를 확인하거나 잠시 후 다시 시도해 주세요.';
  if (status >= 500) return '인증 서버에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.';
  return '요청을 완료하지 못했습니다. 입력 내용을 확인하고 다시 시도해 주세요.';
}
function retryAfterSeconds(value,serverDate,now) {
  if(typeof value!=='string'||!value.trim())return null;
  value=value.trim();
  const dateValue=text=>typeof text==='string'&&/^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/.test(text.trim())?Date.parse(text):NaN;
  const serverTime=dateValue(serverDate),reference=Number.isFinite(serverTime)?serverTime:now;
  const seconds=/^\d+$/.test(value)?Number(value):(dateValue(value)-reference)/1000;
  return Number.isFinite(seconds)&&seconds>=0?Math.max(1,Math.min(86400,Math.ceil(seconds))):null;
}
const otpRateLimitCodes=new Set(['over_email_send_rate_limit','over_request_rate_limit']);
const otpWaitMessage=action=>action==='send'?'인증번호 발송 요청이 많습니다. 잠시 후 다시 요청해 주세요.':'인증 시도가 많습니다. 잠시 후 다시 인증해 주세요.';
const storageUnavailable='브라우저 저장소를 사용할 수 없어 새로고침하면 다시 로그인해야 합니다.';
const validToken=value=>typeof value==='string'&&value.length>0&&value.length<=16384&&/^[A-Za-z0-9._~-]+$/.test(value);
function tokenPair(value,now) {
  if(!validToken(value?.access_token)||!validToken(value?.refresh_token)||!Number.isFinite(value.expires_in)||value.expires_in<=0)throw new Error('로그인 응답을 확인하지 못했습니다.');
  return {access:value.access_token,refresh:value.refresh_token,expires:now+Math.min(value.expires_in,86400)*1000};
}
function verifiedUser(value,now) {
  try {
    if(!uuid.test(value?.id||'')||!Number.isFinite(Date.parse(value?.email_confirmed_at))||Date.parse(value?.banned_until)>now)throw new Error();
    return {userId:value.id,email:cleanEmail(value.email)};
  } catch { const error=new Error('로그인 계정을 확인하지 못했습니다. 다시 로그인해 주세요.');error.invalidSession=true;throw error; }
}
export class MembershipApi {
  constructor(config, fetcher=(...args)=>globalThis.fetch(...args), now=()=>Date.now(), storage=undefined) {
    this.config=publicConfiguration(config);this.fetcher=fetcher;this.now=now;this.session=null;this.generation=0;this.refreshPromise=null;this.restorePromise=null;
    this.otpState={send:{retryAt:0,pending:false},verify:{retryAt:0,pending:false}};
    this.storageNotice='';this.storage=null;this.storageSnapshot=null;this.storedSession=null;
    this.storageKey=this.config?'nephylo.auth.session.v1:'+this.config.origin:null;
    if(this.config){
      try {
        const candidate=storage===undefined?globalThis.sessionStorage:storage;
        if(candidate&&['getItem','setItem','removeItem'].every(name=>typeof candidate[name]==='function'))this.storage=candidate;
        else if(candidate)this.storageNotice=storageUnavailable;
      } catch { this.storageNotice=storageUnavailable; }
      this.storedSession=this.readStoredSession();
    }
  }
  get hasRestorableSession() { return !!this.storedSession; }
  readStoredSession() {
    if(!this.storage||!this.storageKey)return null;
    let text;
    try { text=this.storage.getItem(this.storageKey);this.storageSnapshot=text; }
    catch { this.storageNotice=storageUnavailable;return null; }
    if(text===null)return null;
    try {
      if(typeof text!=='string'||text.length>40000)throw new Error();
      const value=JSON.parse(text);
      if(value?.schemaVersion!==1||value.origin!==this.config.origin||!validToken(value.access)||!validToken(value.refresh)||!Number.isFinite(value.expires)||value.expires<=0||value.expires>this.now()+86700000)throw new Error();
      // Identity, plan, admin flags and provider tokens are never restored from storage.
      return {access:value.access,refresh:value.refresh,expires:value.expires};
    } catch { this.removeStoredSession();return null; }
  }
  assertStorageCurrent() {
    if(!this.storage)return false;
    let current;
    try { current=this.storage.getItem(this.storageKey); }
    catch { this.storageNotice=storageUnavailable;return false; }
    if(current!==this.storageSnapshot){
      // A restored back/forward page must not overwrite a newer login or logout
      // performed by another document in this same tab.
      this.generation++;this.session=null;this.refreshPromise=null;this.restorePromise=null;this.storedSession=this.readStoredSession();
      throw new Error('요청이 취소되었습니다. 다시 로그인해 주세요.');
    }
    return true;
  }
  persistSession(pair) {
    const readable=this.assertStorageCurrent();
    this.storedSession={access:pair.access,refresh:pair.refresh,expires:pair.expires};
    if(!this.storage)return;
    const text=JSON.stringify({schemaVersion:1,origin:this.config.origin,...this.storedSession});
    try { this.storage.setItem(this.storageKey,text);this.storageSnapshot=text;if(readable)this.storageNotice=''; }
    catch { this.storageNotice=storageUnavailable;this.removeStoredSession(false); }
  }
  removeStoredSession(clearPending=true) {
    if(clearPending)this.storedSession=null;
    if(!this.storage)return;
    try { this.storage.removeItem(this.storageKey);this.storageSnapshot=null; }
    catch {
      // Replacing an existing value can still work when removeItem is blocked.
      try { this.storage.setItem(this.storageKey,'');this.storageSnapshot=''; }
      catch { this.storageNotice='브라우저의 로그인 정보를 지우지 못했습니다. 이 탭을 닫아 주세요.'; }
    }
  }
  async restore() {
    if(!this.config)return null;
    if(this.restorePromise?.generation===this.generation)return this.restorePromise.promise;
    if(this.refreshPromise?.generation===this.generation){
      // An in-flight refresh owns rotation. Capture credentials only after it
      // finishes so restoration cannot write its superseded token pair back.
      const generation=this.generation;
      try { await this.refreshPromise.promise; }
      catch(error){if(generation===this.generation){this.assertStorageCurrent();this.session=null;}throw error;}
      if(generation!==this.generation)throw new Error('로그인 요청이 취소되었습니다.');
      return this.restore();
    }
    const generation=this.generation;
    let pair=this.storedSession;
    this.session=null;
    if(!pair)return null;
    const current=()=>{if(generation!==this.generation)throw new Error('로그인 요청이 취소되었습니다.');this.assertStorageCurrent();};
    const entry={generation,promise:null};
    entry.promise=(async()=>{
      let rotated=false;
      const rotate=async()=>{
        current();
        let data;
        try { data=await this.raw('/auth/v1/token?grant_type=refresh_token',{refresh_token:pair.refresh}); }
        catch(error){if([400,401,403].includes(error.status))error.invalidSession=true;throw error;}
        current();pair=tokenPair(data,this.now());rotated=true;
        // Commit rotated credentials immediately, even if later verification is offline.
        this.persistSession(pair);
      };
      try {
        current();
        if(pair.expires<=this.now()+30000)await rotate();
        for(;;){
          try {
            let user;
            try { user=await this.raw('/auth/v1/user',undefined,pair.access,15000,'GET'); }
            catch(error){if([400,403].includes(error.status))error.invalidSession=true;throw error;}
            const identity=verifiedUser(user,this.now());
            current();
            const response=await this.raw('/rest/v1/rpc/get_my_membership',{},pair.access);
            current();
            const member=checkedMembership(response,identity.userId);
            this.persistSession(pair);this.session={...pair,...identity};
            return member;
          } catch(error){
            current();
            if(error.status===401&&!rotated){await rotate();continue;}
            throw error;
          }
        }
      } catch(error){
        if(generation===this.generation){
          this.assertStorageCurrent();
          this.session=null;
          if(error.invalidSession||[401,403].includes(error.status))this.clear();
        }
        throw error;
      } finally { if(this.restorePromise===entry)this.restorePromise=null; }
    })();
    this.restorePromise=entry;
    return entry.promise;
  }
  async raw(path,body,token,timeoutMilliseconds=15000,method='POST') {
    if (!this.config) throw new Error('로그인은 인증 서버 연결 후 이용할 수 있습니다.');
    const controller=new AbortController(), timeout=setTimeout(()=>controller.abort(),timeoutMilliseconds);
    try {
      const headers={'Content-Type':'application/json',apikey:this.config.key};
      if (token) headers.Authorization='Bearer '+token;
      const response=await this.fetcher(this.config.origin+path,{method,headers,...(method==='GET'?{}:{body:JSON.stringify(body)}),signal:controller.signal,redirect:'error',cache:'no-store',credentials:'omit',referrerPolicy:'no-referrer'});
      const content=await response.text();
      if (content.length>262144) throw new Error('인증 서버 응답을 확인하지 못했습니다.');
      let data; try { data=content?JSON.parse(content):null; } catch { if (response.ok) throw new Error('인증 서버 응답을 확인하지 못했습니다.'); }
      if (!response.ok) {
        const code=[data?.error_code,data?.code,data?.error?.code].find(value=>typeof value==='string'&&value);
        const error=new Error(responseError(response.status,code));error.status=response.status;error.code=code;
        error.retryAfterSeconds=retryAfterSeconds(response.headers?.get?.('Retry-After'),response.headers?.get?.('Date'),this.now());throw error;
      }
      return data;
    } catch(error) {
      if (error.name==='AbortError' || error instanceof TypeError) throw new Error('인터넷 연결을 확인한 뒤 다시 시도해 주세요.');
      throw error;
    } finally { clearTimeout(timeout); }
  }
  setSession(value,expectedEmail,generation) {
    if (generation!==this.generation) throw new Error('로그인 요청이 취소되었습니다.');
    const pair=tokenPair(value,this.now());
    if (!uuid.test(value.user?.id||'') || cleanEmail(value.user.email)!==expectedEmail) throw new Error('로그인 응답을 확인하지 못했습니다.');
    this.persistSession(pair);
    this.session={...pair,userId:value.user.id,email:expectedEmail};
  }
  requireEmail() {
    if (!this.config) throw new Error('로그인은 인증 서버 연결 후 이용할 수 있습니다.');
    if (!this.config.emailEnabled) throw new Error('이메일 로그인은 연결 준비 중입니다. 지금은 로그인 없이 무료 버전을 이용해 주세요.');
  }
  otpStatus(action) { const state=this.otpState[action];return {pending:this.otpState.send.pending||this.otpState.verify.pending,seconds:Math.max(0,Math.min(86400,Math.ceil((state.retryAt-this.now())/1000)))}; }
  // Page-local request pacing survives sign-out. Supabase remains the rate-limit authority.
  async otpRequest(action,request,complete=value=>value) {
    const state=this.otpState[action],status=this.otpStatus(action);
    if(status.pending)throw new Error('인증 요청을 처리하고 있습니다. 완료될 때까지 기다려 주세요.');
    if(status.seconds){const error=new Error(otpWaitMessage(action));error.otpAction=action;error.retryAfterSeconds=status.seconds;throw error;}
    state.pending=true;
    try {
      let data;
      try { data=await request(); }
      catch(error) {
        if(error.status===429||otpRateLimitCodes.has(error.code)){
          const seconds=error.retryAfterSeconds??60;state.retryAt=Math.max(state.retryAt,this.now()+seconds*1000);
          error.otpAction=action;error.retryAfterSeconds=seconds;error.message=otpWaitMessage(action);
        }
        throw error;
      }
      if(action==='send')state.retryAt=this.now()+60000;
      return await complete(data);
    } finally { state.pending=false; }
  }
  // Admin sign-in only: a code goes to existing accounts. An unknown address must not create an account or spend the OTP mail budget.
  async sendCode(email) { this.requireEmail();email=cleanEmail(email);await this.otpRequest('send',()=>this.raw('/auth/v1/otp',{email,create_user:false})); }
  async verify(email,code) {
    this.requireEmail();
    email=cleanEmail(email);
    if (!/^[0-9]{6}$/.test(code)) throw new Error('인증번호 6자리를 입력해 주세요.');
    let generation;
    return this.otpRequest('verify',()=>{this.clear();generation=this.generation;return this.raw('/auth/v1/verify',{email,token:code,type:'email'});},async data=>{
      this.setSession(data,email,generation);
      return this.membership();
    });
  }
  beginGoogle() {
    if(!this.config?.googleEnabled)throw new Error(this.config?.emailEnabled?'Google 로그인은 연결 준비 중입니다. 이메일 인증을 이용해 주세요.':'Google 로그인 연결 준비 중입니다. 연결 후 이 버튼으로 간편하게 로그인할 수 있습니다.');
    this.clear();
    return this.generation;
  }
  cancelGoogle(generation) { if(generation===this.generation)this.clear(); }
  async exchangeGoogle(code,verifier,generation) {
    const current=()=>{if(generation!==this.generation)throw new Error('Google 로그인 요청이 취소되었습니다.');this.assertStorageCurrent();};
    current();
    if(!this.config?.googleEnabled || typeof code!=='string' || !/^[A-Za-z0-9._~-]{1,2048}$/.test(code) || typeof verifier!=='string' || !/^[A-Za-z0-9._~-]{43,128}$/.test(verifier))throw new Error('Google 로그인 응답을 확인하지 못했습니다. 다시 시도해 주세요.');
    try {
      const data=await this.raw('/auth/v1/token?grant_type=pkce',{auth_code:code,code_verifier:verifier});
      current();
      if(typeof data?.access_token!=='string'||!data.access_token||data.access_token.length>16384||typeof data.refresh_token!=='string'||!data.refresh_token||data.refresh_token.length>16384||!uuid.test(data.user?.id||''))throw new Error('Google 로그인 응답을 확인하지 못했습니다.');
      const verified=await this.raw('/auth/v1/user',undefined,data.access_token,15000,'GET');
      current();
      if(!verified||verified.id!==data.user.id||!Number.isFinite(Date.parse(verified.email_confirmed_at))||Date.parse(verified.banned_until)>Date.now()||cleanEmail(verified.email)!==cleanEmail(data.user.email))throw new Error(this.config.emailEnabled?'Google 계정을 확인하지 못했습니다. 이메일 인증을 이용해 주세요.':'Google 계정을 확인하지 못했습니다. 다시 시도해 주세요.');
      this.setSession({...data,user:verified},cleanEmail(verified.email),generation);
      return await this.membership();
    } catch(error) { if(generation===this.generation)this.assertStorageCurrent();this.cancelGoogle(generation); throw error; }
  }
  async refresh() {
    if (!this.session) throw new Error('먼저 로그인해 주세요.');
    this.assertStorageCurrent();
    const generation=this.generation, userId=this.session.userId, email=this.session.email, token=this.session.refresh;
    if (this.refreshPromise?.generation===generation && this.refreshPromise.userId===userId) return this.refreshPromise.promise;
    const entry={generation,userId,promise:null};
    entry.promise=(async()=>{try { const data=await this.raw('/auth/v1/token?grant_type=refresh_token',{refresh_token:token}); if(data?.user?.id!==userId)throw new Error('로그인 계정 응답을 확인하지 못했습니다.'); this.setSession(data,email,generation); } catch(error) { if(generation===this.generation){this.assertStorageCurrent();if([400,401,403].includes(error.status))this.clear();} throw error; } finally { if(this.refreshPromise===entry)this.refreshPromise=null; }})();
    this.refreshPromise=entry;
    return entry.promise;
  }
  async rpc(name,body={}) {
    if (!['get_my_membership','admin_list_members','admin_set_membership','admin_get_membership_audit'].includes(name)) throw new Error('지원하지 않는 작업입니다.');
    return this.authenticated('/rest/v1/rpc/'+name,body,name==='get_my_membership');
  }
  async billing(action,body={}) {
    if(!['config','status','checkout','confirm','cancel','sync'].includes(action))throw new Error('지원하지 않는 결제 작업입니다.');
    if(action==='config')return this.config?this.raw('/functions/v1/billing-api',{action}):{enabled:false,amount:3000,currency:'KRW',interval:'month'};
    return this.authenticated('/functions/v1/billing-api',{...body,action},true,action==='confirm'?90000:20000);
  }
  async internationalBilling(action,body={}) {
    if(!['config','status','checkout','portal','sync'].includes(action))throw new Error('지원하지 않는 결제 작업입니다.');
    if(action==='config')return this.config?this.raw('/functions/v1/international-billing-api',{action}):{enabled:false,provider:'lemonsqueezy',amount:3000,currency:'KRW',interval:'month',testMode:false};
    return this.authenticated('/functions/v1/international-billing-api',{...body,action},true,30000);
  }
  async authenticated(path,body,revokeForbidden=false,timeoutMilliseconds=15000) {
    if (!this.session) throw new Error('먼저 로그인해 주세요.');
    const generation=this.generation,userId=this.session.userId;
    const current=()=>{if(generation!==this.generation||!this.session||this.session.userId!==userId)throw new Error('요청이 취소되었습니다. 다시 로그인해 주세요.');this.assertStorageCurrent();};
    if (this.session.expires<=this.now()+30000) await this.refresh();
    current();
    let result;
    try { result=await this.raw(path,body,this.session.access,timeoutMilliseconds); }
    catch(error) {
      current();
      if(error.status===403&&revokeForbidden)this.clear();
      if (error.status!==401) throw error;
      await this.refresh();
      current();
      try { result=await this.raw(path,body,this.session.access,timeoutMilliseconds); }
      catch(second) { current(); if (second.status===401||(second.status===403&&revokeForbidden)) this.clear(); throw second; }
    }
    current();
    return result;
  }
  async membership() { const value=await this.rpc('get_my_membership'); return checkedMembership(value,this.session.userId); }
  clear() { this.generation++; this.session=null; this.refreshPromise=null; this.restorePromise=null; this.removeStoredSession(); }
  async signOut() {
    const token=this.session?.access||this.storedSession?.access; this.clear();
    if (token) { try { await this.raw('/auth/v1/logout?scope=local',{},token); } catch {} }
  }
}
