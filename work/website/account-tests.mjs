import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const code = await readFile(new URL('./dist/account/membership-api.js',import.meta.url),'utf8');
const {MembershipApi,publicConfiguration,cleanEmail,checkedMembership,membershipFreshness}=await import('data:text/javascript;base64,'+Buffer.from(code).toString('base64'));
let checks=0;
const check=(actual,expected,message)=>{assert.deepEqual(actual,expected,message);checks++;};
const reject=async(work,pattern)=>{await assert.rejects(work,pattern);checks++;};
const config={SupabaseUrl:'https://member-test.supabase.co',PublishableKey:'sb_publishable_testonlypublic123'};
const user='a0000000-0000-0000-0000-000000000001';
const session={access_token:'access-fixture',refresh_token:'refresh-fixture',expires_in:3600,user:{id:user,email:'tester@example.com'}};
const membership={user_id:user,email:'tester@example.com',plan:'free',status:'free',expires_at:null,server_time:'2026-10-11T09:00:00Z',is_admin:false};
check(membershipFreshness(membership,899999),'current');
check(membershipFreshness(membership,900000),'stale','Admin and Free display expire after 15 minutes without confirmation');
check(membershipFreshness({plan:'pro',remainingMilliseconds:10000},9999),'current');
check(membershipFreshness({plan:'pro',remainingMilliseconds:10000},10000),'expired','Pro display ends exactly at its server deadline');
check(membershipFreshness({plan:'pro',remainingMilliseconds:10000},900000),'stale','Expired Pro must also lose stale admin controls');
check(checkedMembership({...membership,plan:'pro',status:'active',expires_at:'2026-10-11T08:00:00Z'},user).status,'expired');
const reply=(status,data)=>({status,ok:status>=200&&status<300,text:async()=>data===null?'':JSON.stringify(data)});
// Native browser fetch requires the Window receiver. Arrow-function request
// fixtures do not reproduce that browser contract, so exercise the default path.
const originalFetch=Object.getOwnPropertyDescriptor(globalThis,'fetch');
const browserRequests=[];
try {
  globalThis.fetch=async function(url,options) {
    if(this!==globalThis)throw new TypeError('Illegal invocation');
    browserRequests.push({url,options});
    if(url.endsWith('/auth/v1/token?grant_type=pkce'))return reply(200,session);
    if(url.endsWith('/auth/v1/user'))return reply(200,{...session.user,email_confirmed_at:'2026-10-11T09:00:00Z'});
    if(url.endsWith('/rest/v1/rpc/get_my_membership'))return reply(200,membership);
    throw new Error('Unexpected browser request');
  };
  const browserApi=new MembershipApi({...config,GoogleEnabled:true,EmailEnabled:false});
  const browserMember=await browserApi.exchangeGoogle('browser-code','x'.repeat(43),browserApi.beginGoogle());
  check(browserMember.plan,'free','Default browser fetch completes Google exchange and membership without granting Pro');
  check(browserRequests.length,3,'Default fetch reaches token exchange, identity verification and membership');
  check(browserRequests[1].options.method,'GET','Verified identity still comes from the authenticated user endpoint');
  check(browserRequests[2].options.headers.Authorization,'Bearer access-fixture','Membership retains the verified user token');
} finally {
  if(originalFetch)Object.defineProperty(globalThis,'fetch',originalFetch);else delete globalThis.fetch;
}
check(publicConfiguration({SupabaseUrl:'',PublishableKey:''}),null,'Unconfigured is explicit');
check(publicConfiguration(config).emailEnabled,true,'Existing public configurations keep email sign-in enabled');
check(publicConfiguration({...config,EmailEnabled:true}).emailEnabled,true);
for(const value of [false,'true','false',null,0])check(publicConfiguration({...config,EmailEnabled:value}).emailEnabled,false,'Only an explicit boolean true enables a provided readiness flag');
for(const url of ['http://member-test.supabase.co','https://foo.test/path','https://user:password@foo.test','https://foo.test?key=1','https://localhost']){assert.throws(()=>publicConfiguration({...config,SupabaseUrl:url}));checks++;}
for(const key of ['sb_secret_1234567890','bad',`a.${Buffer.from(JSON.stringify({role:'service_role'})).toString('base64url')}.b`]){assert.throws(()=>publicConfiguration({...config,PublishableKey:key}));checks++;}
check(cleanEmail(' TESTER@EXAMPLE.COM '),'tester@example.com');
assert.throws(()=>cleanEmail('bad'));checks++;
let calls=[];
const fake=async(url,options)=>{calls.push({url,options});if(url.endsWith('/otp'))return reply(200,{});if(url.endsWith('/verify'))return reply(200,session);if(url.endsWith('/get_my_membership'))return reply(200,membership);if(url.includes('/logout'))return reply(204,null);throw new Error('Unexpected request');};
const guest=new MembershipApi({SupabaseUrl:'',PublishableKey:''},fake);
await reject(()=>guest.sendCode('tester@example.com'),/서버 연결/);check(calls.length,0,'Guest makes no auth request');
const preparing=new MembershipApi({...config,EmailEnabled:false,GoogleEnabled:false},fake);
check(preparing.config.origin,config.SupabaseUrl,'Connected server configuration is retained while sign-in is being prepared');
await reject(()=>preparing.sendCode('tester@example.com'),/이메일 로그인은 연결 준비/);
await reject(()=>preparing.verify('tester@example.com','123456'),/이메일 로그인은 연결 준비/);
assert.throws(()=>preparing.beginGoogle(),error=>/Google 로그인 연결 준비/.test(error.message)&&!error.message.includes('이메일 인증을 이용'));checks++;
check(calls.length,0,'Disabled email send, verification and Google start make no network requests');
check(preparing.session,null,'Disabled verification cannot create a session');
const api=new MembershipApi(config,fake);
await api.sendCode('tester@example.com');check(JSON.parse(calls[0].options.body),{email:'tester@example.com',create_user:false},'Admin OTP never registers a new account');
check(calls[0].options.headers.Authorization,undefined,'Public key is not a user token');
await reject(()=>api.verify('tester@example.com','12345'),/6자리/);
const result=await api.verify('tester@example.com','123456');check(result.plan,'free','Email verification alone cannot grant Pro');
check(calls.at(-1).options.headers.Authorization,'Bearer access-fixture');
for(const entry of calls){check(entry.options.redirect,'error');check(entry.options.credentials,'omit');}
assert.throws(()=>checkedMembership({...membership,user_id:'b0000000-0000-0000-0000-000000000002'},user));checks++;
assert.throws(()=>checkedMembership({...membership,is_admin:'true'},user));checks++;
check(checkedMembership({...membership,plan:'pro',status:'active',expires_at:'2026-10-12T09:00:00Z'},user).plan,'pro');
check(checkedMembership({...membership,plan:'pro',status:'active',expires_at:'2026-10-10T09:00:00Z'},user).plan,'free');
check(checkedMembership({...membership,plan:'pro',status:'revoked',expires_at:'2026-10-12T09:00:00Z'},user).plan,'free');
await reject(()=>api.rpc('promote_me',{}),/지원하지/);
await api.signOut();check(api.session,null);await reject(()=>api.membership(),/로그인/);
let releaseVerify;
const slow=new MembershipApi(config,async()=>new Promise(resolve=>{releaseVerify=()=>resolve(reply(200,session));}));
const pending=slow.verify('tester@example.com','123456');await slow.signOut();releaseVerify();await reject(()=>pending,/취소/);check(slow.session,null,'Logout wins against late verification');
let retries=0;
const refreshApi=new MembershipApi(config,async(url,options)=>{if(url.includes('/token?')){retries++;return reply(200,{...session,access_token:'new-access'});}check(options.headers.Authorization,'Bearer new-access');return reply(200,membership);});
refreshApi.session={access:'old-access',refresh:'refresh-fixture',email:'tester@example.com',userId:user,expires:0};
check((await refreshApi.membership()).plan,'free');check(retries,1,'Expired access token refreshes once');
const denied=new MembershipApi(config,async()=>reply(401,{message:'secret provider text access-fixture'}));denied.session={...refreshApi.session,expires:0};await reject(()=>denied.membership(),/인증이 만료/);check(denied.session,null);
const blocked=new MembershipApi(config,async()=>reply(403,{}));blocked.session={...refreshApi.session};await reject(()=>blocked.membership(),/권한/);check(blocked.session,null,'Authoritative membership rejection clears session');
let finishOldRequest;const switchedCalls=[];
const switched=new MembershipApi(config,async(url,options)=>{switchedCalls.push({url,options});return new Promise(resolve=>{finishOldRequest=()=>resolve(reply(401,{}));});});
switched.session={...refreshApi.session};const oldMutation=switched.rpc('admin_set_membership',{p_user_id:user,p_plan:'free'});switched.clear();switched.session={access:'other-account-token',refresh:'other-refresh',userId:'b0000000-0000-0000-0000-000000000002',email:'other@example.com',expires:Date.now()+3600000};finishOldRequest();await reject(()=>oldMutation,/취소/);check(switchedCalls.length,1,'Old mutation cannot refresh or retry as a new account');check(switched.session.email,'other@example.com');
const limitedReply=(status,data,retry,serverDate)=>new Response(JSON.stringify(data),{status,headers:{...(retry===undefined?{}:{'Retry-After':retry}),...(serverDate===undefined?{}:{Date:serverDate})}});
const fixedNow=Date.parse('2026-10-12T09:00:00Z');
for(const [data,retry,expectedCode,expectedSeconds,serverDate,clientNow=fixedNow] of [
  [{error_code:'over_email_send_rate_limit'},'91','over_email_send_rate_limit',91],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:02:00 GMT','over_request_rate_limit',120],
  [{error:{code:'BILLING_PENDING'}},undefined,'BILLING_PENDING',null],
  [{error_code:'over_request_rate_limit',code:'other',error:{code:'nested'}},'0','over_request_rate_limit',1],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 08:59:00 GMT','over_request_rate_limit',null],
  [{code:'over_request_rate_limit'},'not-a-date','over_request_rate_limit',null],
  [{code:'over_request_rate_limit'},'1.5','over_request_rate_limit',null],
  [{code:'over_request_rate_limit'},'-5','over_request_rate_limit',null],
  [{code:'over_request_rate_limit'},'99999999999999999999','over_request_rate_limit',86400],
  [{code:'over_request_rate_limit'},'86400','over_request_rate_limit',86400],
  [{code:'over_request_rate_limit'},'86401','over_request_rate_limit',86400],
  [{code:'over_request_rate_limit'},'9'.repeat(400),'over_request_rate_limit',null],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:00 GMT','over_request_rate_limit',1],
  [{code:'over_request_rate_limit'},'Tue, 13 Oct 2026 10:00:00 GMT','over_request_rate_limit',86400],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:30 GMT','over_request_rate_limit',30,'Mon, 12 Oct 2026 09:00:00 GMT',fixedNow+3600000],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:30 GMT','over_request_rate_limit',30,'Mon, 12 Oct 2026 09:00:00 GMT',fixedNow-3600000],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:30 GMT','over_request_rate_limit',30,'invalid'],
  [{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:30 GMT','over_request_rate_limit',30,'1']
]){
  const mapped=new MembershipApi(config,async()=>limitedReply(429,{...data,message:'provider-private-fixture'},retry,serverDate),()=>clientNow);
  await reject(()=>mapped.raw('/auth/v1/otp',{}),error=>{
    check(error.code,expectedCode,'Real response error code shape is retained');
    check(error.retryAfterSeconds,expectedSeconds,'Retry-After is bounded and HTTP dates use valid server time despite client skew');
    check(error.status,429);check(error.message.includes('provider-private-fixture'),false,'Provider error text is not displayed');return true;
  });
}
let now=fixedNow;const limitCalls=[];
const limited=new MembershipApi(config,async url=>{
  limitCalls.push(url);
  return url.endsWith('/otp')?limitedReply(429,{error_code:'over_email_send_rate_limit'},'90'):limitedReply(429,{code:'over_request_rate_limit'},'Mon, 12 Oct 2026 09:00:45 GMT');
},()=>now);
await reject(()=>limited.sendCode('tester@example.com'),error=>error.otpAction==='send'&&error.code==='over_email_send_rate_limit'&&error.retryAfterSeconds===90);
check(limited.otpStatus('send'),{pending:false,seconds:90});check(limited.otpStatus('verify').seconds,0,'Send throttling does not throttle verification');
await reject(()=>limited.sendCode('another@example.com'),/발송 요청/);check(limitCalls.length,1,'Changing email does not bypass the current page send wait');
await reject(()=>limited.verify('tester@example.com','123456'),error=>error.otpAction==='verify'&&error.retryAfterSeconds===45);
check(limitCalls.length,2,'Verification can reach the server while sending is cooling down');
await reject(()=>limited.verify('tester@example.com','123456'),/인증 시도/);check(limitCalls.length,2,'A verification cooldown prevents another verify request');
now+=30000;await limited.signOut();
check(limited.otpStatus('send').seconds,60,'Sign-out does not restart or clear the send deadline');
check(limited.otpStatus('verify').seconds,15,'Independent verification deadline survives sign-out');
now+=15000;check(limited.otpStatus('verify').seconds,0);check(limited.otpStatus('send').seconds,45);
for(const action of ['send','verify']){
  const fallback=new MembershipApi(config,async()=>limitedReply(400,{error_code:action==='send'?'over_email_send_rate_limit':'over_request_rate_limit'},'invalid'),()=>fixedNow);
  await reject(()=>action==='send'?fallback.sendCode('tester@example.com'):fallback.verify('tester@example.com','123456'),error=>error.otpAction===action&&error.retryAfterSeconds===60);
  check(fallback.otpStatus(action).seconds,60,'Known Supabase rate-limit codes receive a fallback even without HTTP 429');
}
const unknownLimit=new MembershipApi(config,async()=>limitedReply(429,{},undefined),()=>fixedNow);
await reject(()=>unknownLimit.verify('tester@example.com','123456'),error=>error.otpAction==='verify'&&error.retryAfterSeconds===60);
for(const [retry,expected] of [['0',1],['99999999999999999999',86400],['Mon, 12 Oct 2026 08:59:00 GMT',60]]){
  const bounded=new MembershipApi(config,async()=>limitedReply(429,{},retry),()=>fixedNow);
  await reject(()=>bounded.sendCode('tester@example.com'),error=>error.retryAfterSeconds===expected);
  check(bounded.otpStatus('send').seconds,expected,'Zero, oversized and past Retry-After values produce safe countdowns');
}
const skewed=new MembershipApi(config,async()=>limitedReply(429,{},'Mon, 12 Oct 2026 09:00:30 GMT','Mon, 12 Oct 2026 09:00:00 GMT'),()=>fixedNow+3600000);
await reject(()=>skewed.verify('tester@example.com','123456'),error=>error.retryAfterSeconds===30);check(skewed.otpStatus('verify').seconds,30,'The relative server wait becomes a local deadline without clock skew');
const releases=new Map();let concurrentCalls=0;
const concurrent=new MembershipApi(config,async url=>{concurrentCalls++;return new Promise(resolve=>releases.set(url.endsWith('/otp')?'send':'verify',resolve));},()=>fixedNow);
const sending=concurrent.sendCode('tester@example.com');
check(concurrent.otpStatus('send').pending,true);check(concurrent.otpStatus('verify').pending,true);
await reject(()=>concurrent.sendCode('tester@example.com'),/인증 요청을 처리/);await reject(()=>concurrent.verify('tester@example.com','123456'),/인증 요청을 처리/);
check(concurrentCalls,1,'Sending blocks both duplicate sends and conflicting verification');check(concurrent.generation,0,'A blocked verification does not invalidate the session generation');
releases.get('send')(reply(200,{}));await sending;check(concurrent.otpStatus('send'),{pending:false,seconds:60},'Successful send keeps the normal resend delay');
const verifying=concurrent.verify('tester@example.com','123456');
const verifyGeneration=concurrent.generation;
await reject(()=>concurrent.sendCode('tester@example.com'),/인증 요청을 처리/);await reject(()=>concurrent.verify('tester@example.com','123456'),/인증 요청을 처리/);
check(concurrentCalls,2,'Verification blocks both new sends and duplicate verification');check(concurrent.generation,verifyGeneration,'Duplicate verify cannot invalidate the in-flight attempt');
releases.get('verify')(limitedReply(403,{error_code:'otp_expired'}));await reject(()=>verifying,/권한/);
check(concurrent.otpStatus('verify'),{pending:false,seconds:0},'An incorrect code is not an arbitrary client lockout');
let rejectedCodes=0;
const invalidCode=new MembershipApi(config,async()=>{rejectedCodes++;return limitedReply(403,{error_code:'otp_expired'});});
await reject(()=>invalidCode.verify('tester@example.com','123456'),error=>error.code==='otp_expired');await reject(()=>invalidCode.verify('tester@example.com','654321'),error=>error.code==='otp_expired');check(rejectedCodes,2,'Non-rate-limit errors leave retry policy to the server');
const afterVerify=new MembershipApi(config,async url=>url.endsWith('/verify')?reply(200,session):limitedReply(429,{code:'over_request_rate_limit'},'120'),()=>fixedNow);
await reject(()=>afterVerify.verify('tester@example.com','123456'),error=>error.status===429&&error.otpAction===undefined);check(afterVerify.otpStatus('verify').seconds,0,'A later membership-RPC limit is not misclassified as OTP verification throttling');
const page=await readFile(new URL('./dist/account/admin.html',import.meta.url),'utf8');
const ui=await readFile(new URL('./dist/account/account.js',import.meta.url),'utf8');
check(/id="email"[^>]*disabled/.test(page),true,'Unconfigured page starts disabled');
check(/localStorage|innerHTML/.test(ui+code),false,'No cross-browser-session token persistence or HTML interpolation; tab restoration is server-verified in session-persistence-tests');
check(page.includes('id="grant-review"'),true,'Admin changes have an explicit review step');
console.log(`PASS: account authentication, expiry, revocation, stale login, public config and UI safety (${checks} checks). No live requests.`);
