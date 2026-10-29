using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageWidget
{
    internal enum DiagnosticEnvironment { Unknown, Windows, Wsl, Api, Manual }
    internal enum DiagnosticState { NoData, Ready, Stale, Checking, Error, Manual }
    internal enum DiagnosticError { None, Installation, Version, Authentication, AccountChanged, Unavailable, Timeout, Response, Network, Storage, Permission, RateLimited, Cancelled, Connection, Input, Unknown }
    internal sealed class DiagnosticFailure
    {
        internal DiagnosticError Kind;
        internal DateTimeOffset At;
    }
    internal sealed class DiagnosticService
    {
        internal string Id, CliVersion, Source;
        internal DiagnosticEnvironment Environment;
        internal DiagnosticState State;
        internal DateTimeOffset? LastSuccess, RetryAt;
        internal DiagnosticFailure Failure;
    }

    // This is an allowlist serializer, not a redactor of provider output. It has no
    // access to keys, identity, paths, settings documents, usage values or log text.
    internal static class SupportDiagnostics
    {
        internal static readonly string[] Services = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
        internal static DiagnosticError Classify(Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
            if (error == null) return DiagnosticError.None;
            // Only the adapter's fixed category survives; never copy Message/Data text.
            var connection = error as ConnectionException;
            if (connection != null) {
                var known = KnownError(connection.Data["ClaudeQuotaKind"] as string);
                if (known == DiagnosticError.Unknown && connection.Issue != null) known = KnownError(connection.Issue.DiagnosticCode);
                if (known != DiagnosticError.Unknown) return known;
                return connection.RetryNotBefore.HasValue ? DiagnosticError.RateLimited : DiagnosticError.Connection;
            }
            if (error is TimeoutException) return DiagnosticError.Timeout;
            var web = error as WebException;
            if (web != null && web.Status == WebExceptionStatus.Timeout) return DiagnosticError.Timeout;
            if (error is OperationCanceledException) return DiagnosticError.Cancelled;
            if (error is HttpRequestException || web != null) return DiagnosticError.Network;
            if (error is UnauthorizedAccessException) return DiagnosticError.Permission;
            if (error is IOException) return DiagnosticError.Storage;
            if (error is ArgumentException) return DiagnosticError.Input;
            return DiagnosticError.Unknown;
        }

        static DiagnosticError KnownError(string code)
        {
            switch (code) {
                case "installation": return DiagnosticError.Installation;
                case "version": return DiagnosticError.Version;
                case "authentication": return DiagnosticError.Authentication;
                case "account": return DiagnosticError.AccountChanged;
                case "unavailable": return DiagnosticError.Unavailable;
                case "timeout": return DiagnosticError.Timeout;
                case "response": return DiagnosticError.Response;
                case "network": return DiagnosticError.Network;
                case "permission": return DiagnosticError.Permission;
                case "rate-limit": return DiagnosticError.RateLimited;
                default: return DiagnosticError.Unknown;
            }
        }

        internal static string SafeVersion(string value)
        {
            Version parsed;
            return value != null && value.Length <= 24 && Regex.IsMatch(value,@"\A[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}\z") && Version.TryParse(value,out parsed)
                ? parsed.ToString() : "not_recorded";
        }
        static string EnumText<T>(T value) where T : struct
        { return Enum.IsDefined(typeof(T),value) ? value.ToString().ToLowerInvariant() : "unknown"; }
        static string Stamp(DateTimeOffset? value)
        { return value.HasValue ? value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'",CultureInfo.InvariantCulture) : "none"; }
        static string Source(string value)
        {
            if (value == "api" || value == "cli" || value == "manual" || value == "code") return value;
            return value != null && value.StartsWith("code:",StringComparison.Ordinal) ? "code" : "unknown";
        }
        internal static string Create(Version app, Version os, bool process64, bool automaticUpdates, IEnumerable<DiagnosticService> services, DateTimeOffset now)
        {
            var output = new StringBuilder("Nephylo diagnostics v1\n");
            output.AppendLine("Generated UTC: " + Stamp(now));
            output.AppendLine("App version: " + SafeVersion(app == null ? null : app.ToString()));
            output.AppendLine("Windows version: " + SafeVersion(os == null ? null : os.ToString()));
            output.AppendLine("Process bitness: " + (process64 ? "64" : "32"));
            output.AppendLine("Automatic updates: " + (automaticUpdates ? "enabled" : "disabled"));
            output.AppendLine("Account, keys, paths, usage values and raw logs are excluded.");
            var snapshots = (services ?? Enumerable.Empty<DiagnosticService>()).Take(16).Where(s => s != null).ToArray();
            foreach (string id in Services) {
                var item = snapshots.FirstOrDefault(s => s.Id == id); if (item == null) continue;
                output.AppendLine(); output.AppendLine("[" + id + "]");
                output.AppendLine("Environment: " + EnumText(item.Environment));
                output.AppendLine("State: " + EnumText(item.State));
                output.AppendLine("Source: " + Source(item.Source));
                if (item.Environment == DiagnosticEnvironment.Windows || item.Environment == DiagnosticEnvironment.Wsl)
                    output.AppendLine("CLI version (last observed): " + SafeVersion(item.CliVersion));
                output.AppendLine("Last success UTC: " + Stamp(item.LastSuccess));
                output.AppendLine("Last error category: " + (item.Failure == null ? "none" : EnumText(item.Failure.Kind)));
                output.AppendLine("Last error UTC: " + Stamp(item.Failure == null ? (DateTimeOffset?)null : item.Failure.At));
                output.AppendLine("Next retry UTC: " + Stamp(item.RetryAt));
            }
            return output.ToString();
        }
    }
}
