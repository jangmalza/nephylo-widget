using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class ClaudeQuotaTests
{
    static int checks;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static Dictionary<string,object> Usage(DateTimeOffset now)
    {
        return new Dictionary<string,object> { {"subscription_type","max"},{"rate_limits_available",true},{"rate_limits",new Dictionary<string,object> {
            {"five_hour",new Dictionary<string,object>{{"utilization",25},{"resets_at",now.AddHours(2).ToString("o")}}},
            {"seven_day",new Dictionary<string,object>{{"utilization",40},{"resets_at",now.AddDays(3).ToString("o")}}},
            {"seven_day_opus",new Dictionary<string,object>{{"utilization",99},{"resets_at",now.AddDays(3).ToString("o")}}}
        }},{"behaviors",new Dictionary<string,object>{{"private_transcript","NEVER_PERSIST_PRIVATE_TRANSCRIPT"}}} };
    }
    internal static int Fake(string[] args)
    {
        string root=Environment.GetEnvironmentVariable("NEPHYLO_CLAUDE_QUOTA_TEST"), mode=File.ReadAllText(Path.Combine(root,"mode.txt"));
        File.AppendAllText(Path.Combine(root,"pids.txt"),Process.GetCurrentProcess().Id+"\n");
        File.AppendAllText(Path.Combine(root,"calls.txt"),(args.Contains("--version")?"version":args.Contains("--help")?"help":args.Contains("auth")?"auth":"query")+"\n");
        if(args.Contains("--version")){Console.WriteLine(mode=="old"?"2.1.296 (Claude Code)":File.Exists(Path.Combine(root,"version.txt"))?File.ReadAllText(Path.Combine(root,"version.txt")):"2.1.304 (Claude Code)");return 0;}
        if(args.Contains("--help")){Console.WriteLine(mode=="missing-flag"?"--safe-mode":String.Join(" ",ClaudeQuotaService.Arguments(Guid.NewGuid().ToString("D"))));return 0;}
        string email=File.ReadAllText(Path.Combine(root,"account.txt"));
        if(args.Contains("auth")){
            if(mode=="switch-after"&&File.Exists(Path.Combine(root,"queried")))email="other@example.invalid";
            Console.WriteLine(Json.Serialize(new{loggedIn=mode!="logout",authMethod=mode=="api"?"api_key":"claude.ai",apiProvider="firstParty",email=mode=="no-email"?null:email,orgId="fixture-organization"}));return 0;
        }
        File.AppendAllText(Path.Combine(root,"arguments.ndjson"),Json.Serialize(args)+"\n");
        string session=args[Array.IndexOf(args,"--session-id")+1];
        while(true){
            string line=Console.ReadLine();if(line==null)return 0;
            File.AppendAllText(Path.Combine(root,"requests.ndjson"),line+"\n");
            var frame=Json.Deserialize<Dictionary<string,object>>(line);var request=(Dictionary<string,object>)frame["request"];string subtype=(string)request["subtype"],id=(string)frame["request_id"];
            object response;
            if(mode=="timeout")Thread.Sleep(10000);
            if(subtype=="initialize")response=new{current_permission_mode=mode=="permission"?"default":"dontAsk",account=new{apiProvider=mode=="provider"?"bedrock":"firstParty",email=mode=="child-account"?"other@example.invalid":email}};
            else{
                File.WriteAllText(Path.Combine(root,"queried"),"yes");
                File.AppendAllText(Path.Combine(root,"query-count.txt"),"query\n");
                response=Usage(DateTimeOffset.UtcNow);
                if(mode=="unavailable")((Dictionary<string,object>)response)["rate_limits_available"]=false;
                if(mode=="oversized"){Console.WriteLine(new string('x',2*1024*1024));return 0;}
                if(mode=="unexpected-frame"){Console.WriteLine(Json.Serialize(new{type="control_request",request=new{subtype="can_use_tool"}}));return 0;}
                if(mode=="large-stderr"){Console.Error.WriteLine(new string('x',2*1024*1024));}
            }
            Console.WriteLine(Json.Serialize(new{type="control_response",session_id=mode=="wrong-session"?Guid.NewGuid().ToString("D"):session,response=new{subtype="success",request_id=mode=="wrong-id"?"not-owned":id,response=response}}));
        }
    }
    static void ExpectFailure(Func<ClaudeUsageResult> action,string kind)
    {
        try{action();throw new Exception("Expected a quota failure: "+kind);}catch(ConnectionException error){Check((string)error.Data["ClaudeQuotaKind"]==kind,"Expected bounded failure "+kind+", got "+error.Data["ClaudeQuotaKind"]);Check(!error.Message.Contains("example.invalid")&&!error.Message.Contains("PRIVATE"),"Failure does not expose provider output or identity");}
    }
    static int Calls(string root,string kind=null) { return File.ReadAllLines(Path.Combine(root,"calls.txt")).Count(line=>kind==null||line==kind); }
    internal static int Run(string parent)
    {
        string root=Path.Combine(parent,"quota");Directory.CreateDirectory(root);
        string priorConfig=ClaudeUsageService.ConfigDirectoryOverride;
        int priorTimeout=ClaudeQuotaService.TimeoutMilliseconds;
        var priorClock=ClaudeQuotaService.UtcNowOverride;
        try{
            var now=DateTimeOffset.UtcNow;string fingerprint=new string('a',64);
            var parsed=ClaudeQuotaService.Parse(Usage(now),now,fingerprint);
            Check(parsed.IsDirectQuery&&parsed.HasQuotaData&&parsed.Snapshot.Rows[0].RemainingPercent==75&&parsed.Snapshot.Rows[1].RemainingPercent==60,"Official common percentages parsed without model-specific contamination");
            Check(!parsed.QuotaJson.Contains("PRIVATE")&&!parsed.QuotaJson.Contains("opus")&&!parsed.QuotaJson.Contains("example.invalid"),"Only canonical common rate limits survive the adapter");
            foreach(object invalid in new object[]{-1,101,Double.NaN,Double.PositiveInfinity,"25",true}){
                var response=Usage(now);var row=(Dictionary<string,object>)((Dictionary<string,object>)response["rate_limits"])["five_hour"];row["utilization"]=invalid;
                ExpectFailure(()=>ClaudeQuotaService.Parse(response,now,fingerprint),"response");
            }
            foreach(string invalid in new[]{"2026-10-21",now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss"),now.AddHours(-1).ToString("o"),now.AddDays(3).ToString("o")}){
                var response=Usage(now);((Dictionary<string,object>)((Dictionary<string,object>)response["rate_limits"])["five_hour"])["resets_at"]=invalid;
                ExpectFailure(()=>ClaudeQuotaService.Parse(response,now,fingerprint),"response");
            }
            var partial=Usage(now);((Dictionary<string,object>)partial["rate_limits"])["five_hour"]=null;
            var partialResult=ClaudeQuotaService.Parse(partial,now,fingerprint);
            Check(!partialResult.Snapshot.Rows[0].RemainingPercent.HasValue&&partialResult.Snapshot.Rows[1].RemainingPercent==60,"Missing five-hour limit is unknown, not an invented 100 percent");
            var noReset=Usage(now);((Dictionary<string,object>)((Dictionary<string,object>)noReset["rate_limits"])["five_hour"])["resets_at"]=null;
            Check(!ClaudeQuotaService.Parse(noReset,now,fingerprint).Snapshot.Rows[0].ResetsAt.HasValue,"Nullable official reset remains unknown");
            var unavailable=Usage(now);unavailable["rate_limits_available"]=false;ExpectFailure(()=>ClaudeQuotaService.Parse(unavailable,now,fingerprint),"unavailable");
            unavailable=Usage(now);unavailable["subscription_type"]="api";ExpectFailure(()=>ClaudeQuotaService.Parse(unavailable,now,fingerprint),"unavailable");

            ClaudeUsageService.ConfigDirectoryOverride=null;
            var info=new ProcessStartInfo("fixture.exe");info.EnvironmentVariables["ANTHROPIC_API_KEY"]="private-fixture";info.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"]="private-fixture";info.EnvironmentVariables["CLAUDE_CODE_SDK_URL"]="private-fixture";
            ClaudeQuotaService.ConfigureEnvironment(info,"",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude"));
            Check(!info.EnvironmentVariables.ContainsKey("CLAUDE_CONFIG_DIR"),"Explicit default directory preserves normal CLI account metadata");
            Check(!info.EnvironmentVariables.ContainsKey("ANTHROPIC_API_KEY")&&!info.EnvironmentVariables.ContainsKey("ANTHROPIC_AUTH_TOKEN")&&!info.EnvironmentVariables.ContainsKey("CLAUDE_CODE_SDK_URL"),"API credentials and other hosts' transports are not inherited");
            ClaudeQuotaService.ConfigureEnvironment(info,"",Path.Combine(root,"custom-profile"));
            Check(info.EnvironmentVariables["CLAUDE_CONFIG_DIR"]==Path.Combine(root,"custom-profile"),"Custom profile is pinned to the selected environment");
            // The fake CLI owns all process I/O; use real target resolution so two
            // explicit profiles cannot be collapsed by the bridge test override.
            ClaudeUsageService.ConfigDirectoryOverride=null;

            File.WriteAllText(Path.Combine(root,"account.txt"),"fixture@example.invalid");
            string activeExecutable=Assembly.GetExecutingAssembly().Location;
            ClaudeQuotaService.StartInfoOverride=(distribution,directory,args)=>{
                File.AppendAllText(Path.Combine(root,"targets.txt"),distribution+"|"+directory+"\n");
                var start=new ProcessStartInfo(activeExecutable,"--fake-quota "+String.Join(" ",args.Select(ClaudeQuotaService.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.EnvironmentVariables["NEPHYLO_CLAUDE_QUOTA_TEST"]=root;return start;
            };
            File.WriteAllText(Path.Combine(root,"mode.txt"),"normal");
            var service=new ClaudeQuotaService();var first=service.ReadAsync("","",CancellationToken.None);var second=service.ReadAsync("","",CancellationToken.None);Task.WaitAll(first,second);
            Check(first.Result.Snapshot.Rows[0].RemainingPercent==75&&second.Result.AccountFingerprint==first.Result.AccountFingerprint,"Concurrent readers share a verified account result");
            Check(File.ReadAllLines(Path.Combine(root,"query-count.txt")).Length==1,"Rapid concurrent refresh performs only one quota request");
            Check(Calls(root,"version")==1&&Calls(root,"help")==1&&Calls(root,"auth")==3,"Concurrent cached reads reuse capabilities but still verify the current account");
            int metadataBefore=Calls(root);
            Check(service.KnownVersion("","")=="2.1.304"&&service.KnownVersion("",Path.Combine(root,"other-profile"))==null,"Diagnostic version belongs only to its observed profile");
            Check(Calls(root)==metadataBefore,"Reading diagnostic version does not launch a process");
            string original=first.Result.AccountFingerprint;
            File.WriteAllText(Path.Combine(root,"account.txt"),"other@example.invalid");var next=service.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(next.AccountFingerprint!=original,"Account switch invalidates a fresh cache");
            File.WriteAllText(Path.Combine(root,"account.txt"),"fixture@example.invalid");
            Check(service.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult().AccountFingerprint==original&&File.ReadAllLines(Path.Combine(root,"query-count.txt")).Length==3,"A to B to A requires a new verified request");
            Check(Calls(root,"version")==1&&Calls(root,"help")==1,"Account changes never require duplicate capability probes for the unchanged CLI");

            var clock=DateTimeOffset.UtcNow;ClaudeQuotaService.UtcNowOverride=()=>clock;
            var optimized=new ClaudeQuotaService();int calls=Calls(root);
            optimized.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==5,"Cold Windows read performs complete capability and identity checks");
            clock=clock.AddSeconds(20);calls=Calls(root);int authCalls=Calls(root,"auth");
            var warm=optimized.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==3&&Calls(root,"auth")-authCalls==2,"Warm quota refresh uses three children and keeps identity checks before and after the query");
            calls=Calls(root);var rapid=optimized.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==1&&rapid.ObservedAt==warm.ObservedAt,"Rapid read checks identity once and preserves the observation timestamp");
            clock=clock.AddMinutes(6);calls=Calls(root);optimized.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==5,"Capability expiry performs a full revalidation");
            clock=clock.AddMinutes(-6);int versions=Calls(root,"version"),helps=Calls(root,"help");
            optimized.ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"version")==versions+1&&Calls(root,"help")==helps+1,"A backward clock cannot extend capability validity");
            string profile=Path.Combine(root,"second-profile");versions=Calls(root,"version");
            optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"version")==versions+1,"Profile changes do not inherit another target's capabilities");
            activeExecutable=Path.Combine(root,"replacement.exe");File.Copy(Assembly.GetExecutingAssembly().Location,activeExecutable);
            versions=Calls(root,"version");optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"version")==versions+1,"A changed executable path revalidates capabilities");
            File.SetLastWriteTimeUtc(activeExecutable,DateTime.UtcNow.AddSeconds(10));versions=Calls(root,"version");
            optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"version")==versions+1,"Replacing the executable at the same path invalidates capabilities");
            File.SetLastWriteTimeUtc(activeExecutable,DateTime.UtcNow.AddSeconds(20));File.WriteAllText(Path.Combine(root,"mode.txt"),"old");
            ExpectFailure(()=>optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult(),"version");
            Check(optimized.KnownVersion("",profile)=="2.1.296","Unsupported numeric version remains available for diagnosis");
            File.WriteAllText(Path.Combine(root,"mode.txt"),"normal");calls=Calls(root);
            optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==5,"A failed capability check cannot leave a reusable quota or capability cache");
            clock=clock.AddSeconds(20);File.WriteAllText(Path.Combine(root,"mode.txt"),"wrong-id");
            ExpectFailure(()=>optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult(),"response");
            File.WriteAllText(Path.Combine(root,"mode.txt"),"normal");calls=Calls(root);
            optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==5,"Protocol failure invalidates capabilities so the next attempt rechecks support");
            File.WriteAllText(Path.Combine(root,"mode.txt"),"logout");
            ExpectFailure(()=>optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult(),"authentication");
            File.WriteAllText(Path.Combine(root,"mode.txt"),"normal");calls=Calls(root);
            optimized.ReadAsync("",profile,CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==5,"Logging back in cannot reuse a pre-logout result");
            activeExecutable=Assembly.GetExecutingAssembly().Location;
            var wslCached=new ClaudeQuotaService();wslCached.ReadAsync("Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            calls=Calls(root);helps=Calls(root,"help");
            wslCached.ReadAsync("Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==2&&Calls(root,"help")==helps,"WSL checks CLI version and identity on every read but caches unchanged help");
            clock=clock.AddSeconds(20);calls=Calls(root);
            wslCached.ReadAsync("Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root)-calls==4,"Warm WSL refresh retains version and both identity checks");
            File.WriteAllText(Path.Combine(root,"version.txt"),"2.1.305 (Claude Code)");helps=Calls(root,"help");
            wslCached.ReadAsync("Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"help")==helps+1&&wslCached.KnownVersion("Fixture Distro","")=="2.1.305","Changed WSL CLI version immediately rechecks supported options");
            helps=Calls(root,"help");wslCached.ReadAsync("Other Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            Check(Calls(root,"help")==helps+1,"WSL distributions do not share a capability cache");
            File.Delete(Path.Combine(root,"version.txt"));ClaudeQuotaService.UtcNowOverride=priorClock;
            var wsl=new ClaudeQuotaService().ReadAsync("Fixture Distro","",CancellationToken.None).GetAwaiter().GetResult();
            Check(wsl.HasQuotaData&&File.ReadAllText(Path.Combine(root,"targets.txt")).Contains("Fixture Distro|"),"WSL selected target stays separate from Windows");
            foreach(var pair in new[]{"logout:authentication","api:authentication","no-email:authentication","old:version","missing-flag:version","child-account:account","provider:account","permission:account","wrong-session:response","wrong-id:response","unexpected-frame:response","oversized:response","unavailable:unavailable","switch-after:account"}){
                var parts=pair.Split(':');File.WriteAllText(Path.Combine(root,"mode.txt"),parts[0]);
                if(parts[0]=="switch-after")File.Delete(Path.Combine(root,"queried"));
                ExpectFailure(()=>new ClaudeQuotaService().ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult(),parts[1]);
            }
            File.WriteAllText(Path.Combine(root,"mode.txt"),"large-stderr");
            Check(new ClaudeQuotaService().ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult().HasQuotaData,"Large stderr cannot block a valid quota response");
            File.WriteAllText(Path.Combine(root,"mode.txt"),"timeout");ClaudeQuotaService.TimeoutMilliseconds=700;
            ExpectFailure(()=>new ClaudeQuotaService().ReadAsync("","",CancellationToken.None).GetAwaiter().GetResult(),"timeout");
            using(var cancel=new CancellationTokenSource()){
                cancel.CancelAfter(350);bool cancelled=false;
                try{new ClaudeQuotaService().ReadAsync("","",cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled,"Application cancellation remains cancellation, not an authentication error");
            }
            foreach(string line in File.ReadAllLines(Path.Combine(root,"requests.ndjson"))){
                var frame=Json.Deserialize<Dictionary<string,object>>(line);var request=(Dictionary<string,object>)frame["request"];
                Check((string)frame["type"]=="control_request"&&new[]{"initialize","get_usage"}.Contains((string)request["subtype"]),"No user prompt, tool request or model turn is sent");
                if((string)request["subtype"]=="get_usage")Check(Object.Equals(request["skip_behaviors"],true),"Local transcript analysis is skipped");
            }
            foreach(string line in File.ReadAllLines(Path.Combine(root,"arguments.ndjson"))){var args=Json.Deserialize<string[]>(line);Check(args.Contains("--no-session-persistence")&&args.Contains("--restricted")&&args[Array.IndexOf(args,"--tools")+1]==""&&args[Array.IndexOf(args,"--permission-mode")+1]=="dontAsk"&&args[Array.IndexOf(args,"--setting-sources")+1]=="","Owned child has no tools, user settings or persisted session");}
            foreach(string line in File.ReadAllLines(Path.Combine(root,"pids.txt"))){bool alive;try{using(var p=Process.GetProcessById(Int32.Parse(line)))alive=!p.HasExited;}catch(ArgumentException){alive=false;}Check(!alive,"Every owned quota child has exited, including failures and timeout");}
            return checks;
        }finally{ClaudeQuotaService.StartInfoOverride=null;ClaudeQuotaService.TimeoutMilliseconds=priorTimeout;ClaudeQuotaService.UtcNowOverride=priorClock;ClaudeUsageService.ConfigDirectoryOverride=priorConfig;}
    }
}
