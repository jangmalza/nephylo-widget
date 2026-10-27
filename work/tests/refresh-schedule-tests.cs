using System;
using UsageWidget;

internal static class RefreshScheduleTests
{
    private static int assertions;
    private static void Check(bool condition,string message) { assertions++; if (!condition) throw new Exception(message); }
    public static int Main()
    {
        try {
            var now = new DateTimeOffset(2026,10,12,0,0,0,TimeSpan.Zero);
            var failed = new ProviderRefreshSchedule();
            var healthy = new ProviderRefreshSchedule();
            Check(failed.CanAttempt(now,false),"Initial connection is immediately due");
            failed.Failed(now,60,null);
            Check(!failed.CanAttempt(now.AddSeconds(59),false),"No repeated polling before due");
            Check(failed.CanAttempt(now.AddSeconds(60),false),"First retry after normal interval");
            failed.Failed(now.AddMinutes(1),60,null);
            Check(failed.NextAttempt == now.AddMinutes(3),"Second failure doubles delay");
            healthy.Succeeded(now.AddMinutes(1),60);
            Check(healthy.CanAttempt(now.AddMinutes(2),false),"Healthy provider is independent");
            Check(!failed.CanAttempt(now.AddMinutes(2),false),"Failed provider still waits");
            Check(failed.CanAttempt(now.AddMinutes(2),true),"User may retry ordinary connection error");
            for(int i=0;i<20;i++) failed.Failed(now,60,null);
            Check(failed.NextAttempt == now.AddMinutes(15),"Automatic backoff bounded to 15 minutes");
            failed.Failed(now,60,now.AddMinutes(45));
            Check(!failed.CanAttempt(now.AddMinutes(16),true),"Manual action respects server cooldown");
            failed.NetworkRestored(now.AddMinutes(16));
            Check(failed.NextAttempt == now.AddMinutes(45),"Network recovery respects server cooldown");
            Check(failed.CanAttempt(now.AddMinutes(45),false),"Server cooldown expires at boundary");
            failed.Succeeded(now.AddHours(2),60);
            Check(failed.Failures == 0 && failed.NextAttempt == now.AddHours(2).AddMinutes(1),"Success restores ordinary cadence");
            failed.Failed(now,60,null); failed.Failed(now,60,null);
            failed.NetworkRestored(now.AddSeconds(5));
            Check(failed.CanAttempt(now.AddSeconds(5),false),"Recovery immediately retries network failure");
            healthy.Succeeded(now,600); healthy.ChangeInterval(now,60);
            Check(healthy.NextAttempt == now.AddMinutes(1),"Shorter interval applies without waiting for old deadline");
            failed.Failed(now,60,now.AddHours(1)); failed.ChangeInterval(now,60);
            Check(!failed.CanAttempt(now.AddMinutes(1),true),"Settings change cannot bypass server cooldown");
            var capped = new ProviderRefreshSchedule();
            capped.Failed(now,60,now.AddDays(1));
            Check(capped.ServerNotBefore == now.AddHours(1) && capped.NextAttempt == now.AddHours(1),"A day-long Retry-After is capped at one hour");
            Check(!capped.CanAttempt(now.AddMinutes(59),true) && capped.CanAttempt(now.AddHours(1),true),"Manual refresh returns when the capped cooldown ends");
            capped.Failed(now.AddHours(1),60,DateTimeOffset.MaxValue);
            Check(capped.ServerNotBefore == now.AddHours(2),"A maximum Retry-After date cannot lock the provider until restart");
            capped.Failed(now.AddHours(2),60,now.AddHours(2).AddMinutes(2));
            Check(capped.ServerNotBefore == now.AddHours(2).AddMinutes(2),"A short Retry-After within the cap is honored exactly");
            Console.WriteLine("Refresh schedule tests passed: " + assertions + " assertions."); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
