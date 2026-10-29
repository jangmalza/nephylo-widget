using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Only app-authored text and fixed official destinations may reach the UI.
    public sealed class ConnectionIssue
    {
        public string Title;
        public string Message;
        public string ActionLabel;
        public string ActionUrl;
        internal string DiagnosticCode;
    }

    public sealed class ConnectionException : InvalidOperationException
    {
        public ConnectionIssue Issue { get; private set; }
        public DateTimeOffset? RetryNotBefore { get; private set; }
        public ConnectionException(ConnectionIssue issue) : this(issue,null) { }
        public ConnectionException(ConnectionIssue issue, DateTimeOffset? retryNotBefore) : base(issue.Message) { Issue = issue; RetryNotBefore = retryNotBefore; }
    }

    public static class ConnectionDiagnostics
    {
        public static DateTimeOffset? RetryAfter(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter;
            if (retry == null) return null;
            if (retry.Date.HasValue) return retry.Date;
            if (!retry.Delta.HasValue) return null;
            try { return DateTimeOffset.UtcNow.Add(retry.Delta.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private const string ElevenKeys = "https://elevenlabs.io/app/developers/api-keys";
        private const string OpenAiKeys = "https://platform.openai.com/settings/organization/admin-keys";
        private const string CodexGuide = "https://developers.openai.com/codex/cli/";

        private static string Id(string serviceId)
        {
            var id = (serviceId ?? "").ToLowerInvariant();
            return id == "elevenlabs" ? "eleven" : id;
        }

        private static string Name(string serviceId)
        {
            switch (Id(serviceId))
            {
                case "codex": return "Codex";
                case "eleven": return "ElevenLabs";
                case "openai": return "OpenAI";
                case "claude": return "Claude Code";
                case "gemini": return "Gemini";
                case "cursor": return "Cursor";
                default: return "서비스";
            }
        }

        private static ConnectionIssue Make(string title, string message, string label = null, string url = null, string code = null)
        {
            return new ConnectionIssue { Title = title, Message = message, ActionLabel = label, ActionUrl = url, DiagnosticCode = code };
        }

        public static ConnectionIssue For(string serviceId, Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
            var known = error as ConnectionException;
            if (known != null) return known.Issue;
            if (error is OperationCanceledException || error is TimeoutException) return Timeout(serviceId);
            var web = error as WebException;
            if (web != null && web.Status == WebExceptionStatus.Timeout) return Timeout(serviceId);
            if (error is HttpRequestException || web != null) return Network(serviceId);
            if (Id(serviceId) == "claude") return Claude(error);
            return Make(Name(serviceId) + " 연결을 확인해 주세요", "1. 서비스 연결에서 로그인 또는 API 키를 확인해 주세요.\n2. 인터넷 연결을 확인한 뒤 다시 연결을 확인해 주세요.");
        }

        private static ConnectionIssue Claude(Exception error)
        {
            // Match only exact messages produced by the local Claude bridge. Untrusted
            // text is never copied to a diagnostic, even when it resembles a known error.
            string message = error != null && error.GetType() == typeof(InvalidOperationException) ? error.Message : null;
            const string guide = "https://code.claude.com/docs/en/statusline";
            if (message == "Claude 설정 폴더는 이 PC의 전체 경로로 입력해 주세요.")
                return Make("Claude 설정 폴더를 확인해 주세요",message,"상태 표시줄 안내 열기",guide);
            if (message == "기존 Claude 상태 표시줄을 유지했습니다. 기존 스크립트에서 위젯의 --claude-statusline 명령에 같은 JSON 입력을 전달하도록 연결해 주세요."
                || message == "기존 Claude 상태 표시줄이 위젯 연결과 달라 변경하지 않았습니다.")
                return Make("기존 상태 표시줄을 유지했어요", "기존 Claude 상태 표시줄 설정은 변경하지 않았습니다. 서비스 연결에서 직접 기록을 사용하거나, 공식 안내에 따라 기존 상태 표시줄과 위젯을 함께 사용하도록 설정해 주세요.", "상태 표시줄 안내 열기", guide);
            if (message == "Claude 설정 JSON 형식이 올바르지 않아 변경하지 않았습니다.")
                return Make("Claude 설정 파일을 확인해 주세요", "설정 파일의 JSON 형식이 올바르지 않아 변경하지 않았습니다. 기존 설정 파일을 백업하고 JSON 문법을 수정한 뒤 다시 연결해 주세요.", "상태 표시줄 안내 열기", guide);
            if (message == "Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 연결해 주세요."
                || message == "Claude 설정을 다른 작업에서 변경 중입니다. 잠시 후 다시 시도해 주세요."
                || message == "Claude 설정이 변경되어 연결을 중단했습니다. 다시 시도해 주세요."
                || message == "Claude 설정이 변경되어 연결 해제를 중단했습니다. 다시 시도해 주세요.")
                return Make("다른 설정 작업이 끝난 뒤 다시 시도해 주세요", "Claude 설정을 동시에 변경하는 작업이 있어 중단했습니다. 다른 설정 작업을 마친 뒤 연결 상태를 확인하고 다시 시도해 주세요.");
            bool waiting = message == "Claude Code 연결 후 대화를 시작하면 사용량이 표시됩니다.";
            return Make(waiting ? "첫 사용량 기록을 기다리고 있어요" : "Claude Code 연결을 확인해 주세요",
                "1. 서비스 연결에서 상태줄 연결을 확인해 주세요.\n2. Claude Code를 새로 열고 평소처럼 사용하다 다음 응답을 받은 뒤 다시 확인해 주세요. 상태줄 연결만으로 사용량이 즉시 표시되지는 않습니다.", "Claude Code 안내 열기", guide);
        }

        public static ConnectionIssue MissingKey(string serviceId)
        {
            bool openAi = Id(serviceId) == "openai";
            return Make("API 키를 입력해 주세요", openAi
                ? "조직 관리자가 발급한 Admin API 키를 붙여넣고 연결을 확인해 주세요. 일반 프로젝트 API 키로는 조직 비용을 조회할 수 없습니다."
                : "ElevenLabs API 키를 붙여넣고 연결을 확인해 주세요. 키에 사용자 → 읽기 (User → Read, user_read) 권한이 필요합니다.",
                openAi ? "Admin 키 설정 열기" : "API 키 설정 열기", openAi ? OpenAiKeys : ElevenKeys);
        }

        public static ConnectionIssue InvalidKeyFormat(string serviceId)
        {
            var issue = MissingKey(serviceId);
            issue.Title = "API 키 형식을 확인해 주세요";
            issue.Message = Name(serviceId) + " API 키 형식이 올바르지 않습니다. 키 전체를 다시 복사해 붙여넣고, 줄바꿈이나 불필요한 공백이 없는지 확인해 주세요.";
            return issue;
        }

        public static ConnectionIssue Http(string serviceId, int status, string body = null)
        {
            var issue = HttpIssue(serviceId,status,body);
            string providerCode = Id(serviceId) == "eleven" ? ElevenCode(body) : null;
            if (providerCode == "missing_permissions" || providerCode == "insufficient_permissions" || providerCode == "workspace_access_denied") issue.DiagnosticCode = "permission";
            else if (providerCode == "invalid_api_key" || providerCode == "missing_api_key") issue.DiagnosticCode = "authentication";
            else if (issue.DiagnosticCode == null) issue.DiagnosticCode = status == 401 ? "authentication" : status == 403 ? "permission" : status == 429 ? "rate-limit" : status >= 500 && status <= 599 ? "network" : "response";
            return issue;
        }

        private static ConnectionIssue HttpIssue(string serviceId, int status, string body)
        {
            var id = Id(serviceId);
            if (id == "eleven")
            {
                string code = ElevenCode(body);
                if (code == "missing_permissions" || code == "insufficient_permissions")
                    return Make("사용자 읽기 권한이 필요해요", "1. ElevenLabs API 키 설정에서 사용 중인 키를 선택해 주세요.\n2. 사용자 → 읽기 (User → Read, user_read)를 켜고 저장한 뒤 연결을 다시 확인해 주세요.", "API 키 권한 설정 열기", ElevenKeys);
                if (code == "invalid_api_key" || code == "missing_api_key")
                    return Make("API 키를 인증할 수 없어요", "ElevenLabs API 키가 만료·삭제되었거나 올바르지 않습니다. API 키 설정에서 유효한 키를 다시 복사해 붙여넣고 연결을 확인해 주세요.", "API 키 설정 열기", ElevenKeys);
                if (code == "workspace_access_denied")
                    return Make("워크스페이스 접근 권한이 없어요", "조회하려는 ElevenLabs 워크스페이스에서 발급한 키인지 확인해 주세요. 해당 워크스페이스의 키를 붙여넣고 연결을 다시 확인해 주세요.", "API 키 설정 열기", ElevenKeys);
                if (status == 401)
                    return Make("API 키 또는 권한을 확인해 주세요", "ElevenLabs 인증 또는 조회 권한을 확인할 수 없습니다 (HTTP 401).\n1. 유효한 API 키를 다시 붙여넣어 주세요.\n2. 사용자 → 읽기 (User → Read, user_read) 권한을 확인해 주세요.", "API 키 설정 열기", ElevenKeys);
                if (status == 403)
                    return Make("조회 접근이 거부되었어요", "ElevenLabs 접근이 거부되었습니다 (HTTP 403). 키의 사용자 읽기 권한, 워크스페이스 접근 권한, IP 제한을 확인한 뒤 다시 시도해 주세요.", "API 키 권한 설정 열기", ElevenKeys);
            }
            if (id == "openai")
            {
                if (status == 401)
                    return Make("Admin API 키를 확인해 주세요", "OpenAI 인증에 실패했습니다. 일반 프로젝트 키가 아닌 조직의 Admin API 키를 다시 복사해 붙여넣고 연결을 확인해 주세요.", "Admin 키 설정 열기", OpenAiKeys);
                if (status == 403)
                    return Make("조직 비용 조회 권한이 필요해요", "OpenAI 비용 조회 권한이 없습니다. 조직 관리자에게 Admin API 키와 해당 조직의 비용 조회 권한을 확인해 달라고 요청해 주세요.", "Admin 키 설정 열기", OpenAiKeys);
            }
            if (id == "codex" && (status == 401 || status == 403)) return CodexLogin();
            if (status == 429)
                return Make("조회가 일시 제한되었어요", Name(serviceId) + " 조회가 일시 제한되었습니다. 1분 정도 기다린 뒤 다시 시도해 주세요. 제한이 계속되면 더 기다려 주세요.");
            if (status >= 500 && status <= 599)
                return Make("서비스가 잠시 응답하지 않아요", Name(serviceId) + " 서버에서 오류가 발생했습니다 (HTTP " + status.ToString(CultureInfo.InvariantCulture) + "). 잠시 후 다시 연결을 확인해 주세요.");
            return Make("사용량을 가져오지 못했어요", Name(serviceId) + " 조회에 실패했습니다 (HTTP " + status.ToString(CultureInfo.InvariantCulture) + "). 잠시 후 다시 확인하고, 문제가 계속되면 위젯을 최신 버전으로 업데이트해 주세요.");
        }

        // This parser recognizes a small allowlist of error codes, not provider text.
        private static string ElevenCode(string json)
        {
            try
            {
                if (String.IsNullOrEmpty(json) || json.Length > 65536) return null;
                var root = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 16 }.DeserializeObject(json) as Dictionary<string, object>;
                var detail = UsageJson.Object(root, "detail");
                var code = UsageJson.String(detail, "code");
                var legacy = UsageJson.String(detail, "status");
                if (String.IsNullOrEmpty(code)) return legacy;
                if ((code == "unauthorized" || code == "forbidden") && (legacy == "missing_permissions" || legacy == "insufficient_permissions")) return legacy;
                return code;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        public static ConnectionIssue Timeout(string serviceId)
        {
            return Make("응답을 기다리다 시간이 초과됐어요", Name(serviceId) + " 응답 시간이 초과되었습니다.\n1. 인터넷 연결과 VPN·프록시 상태를 확인해 주세요.\n2. 잠시 후 다시 연결을 확인해 주세요.",code:"timeout");
        }

        public static ConnectionIssue Network(string serviceId)
        {
            return Make("인터넷 연결을 확인해 주세요", Name(serviceId) + "에 연결할 수 없습니다.\n1. 인터넷 연결과 VPN·프록시 상태를 확인해 주세요.\n2. 회사 네트워크라면 서비스 접속이 허용되는지 확인한 뒤 다시 시도해 주세요.",code:"network");
        }

        public static ConnectionIssue Malformed(string serviceId, string message = null)
        {
            return Make("사용량 응답을 확인하지 못했어요", message ?? (Name(serviceId) + " 사용량 응답을 읽을 수 없습니다. 잠시 후 다시 확인하고, 문제가 계속되면 위젯과 연결 도구를 최신 버전으로 업데이트해 주세요."),code:"response");
        }

        public static ConnectionIssue CodexMissing(bool invalidPath = false)
        {
            return Make(invalidPath ? "Codex 실행 경로를 확인해 주세요" : "Codex를 설치해 주세요",
                invalidPath ? "선택한 Codex 실행 파일을 찾거나 시작할 수 없습니다. 고급 설정에서 실제 codex.exe 파일을 선택하거나 경로를 비워 자동 검색해 주세요."
                : "Codex Windows 앱 또는 CLI를 찾지 못했습니다. 같은 Windows 계정에 설치했는지 확인한 뒤 다시 시도해 주세요. 이미 설치했다면 고급 설정에서 codex.exe를 직접 선택할 수 있어요.", "Codex 설치 안내 열기", CodexGuide,"installation");
        }

        public static ConnectionIssue CodexLogin()
        {
            return Make("ChatGPT 로그인이 필요해요", "서비스 연결에서 '브라우저로 로그인'을 누르고 로그인을 마쳐 주세요. 완료되면 사용량을 자동으로 확인합니다. API 키 로그인은 Codex 구독 한도를 제공하지 않습니다.",code:"authentication");
        }

        public static ConnectionIssue CodexLoginFailed()
        {
            return Make("Codex 로그인이 완료되지 않았어요", "브라우저 인증이 취소되거나 실패했습니다. '브라우저로 로그인'을 다시 눌러 완료해 주세요. 기존 위젯 설정과 이력은 유지됩니다.",code:"authentication");
        }

        public static ConnectionIssue CodexUnsupported()
        {
            return Make("Codex를 업데이트해 주세요", "이 Codex 버전의 사용량 조회 연결을 확인할 수 없습니다. Codex 앱 또는 CLI를 최신 버전으로 업데이트한 뒤 다시 연결해 주세요.", "Codex 업데이트 안내 열기", CodexGuide,"version");
        }

        public static ConnectionIssue CodexDisconnected()
        {
            return Make("Codex 연결이 종료되었어요", "새로고침으로 Codex에 다시 연결해 주세요. 계속 연결되지 않으면 서비스 연결에서 로그인 상태와 실행 파일 경로를 확인해 주세요.");
        }

        internal static ConnectionIssue CodexRpc(Dictionary<string, object> error)
        {
            var code = UsageJson.Number(error, "code");
            var message = (UsageJson.String(error, "message") ?? "").ToLowerInvariant();
            if (code == -32601 || message.Contains("unknown method") || message.Contains("method not found")) return CodexUnsupported();
            if (code == 401 || code == 403 || message.Contains("401") || message.Contains("unauthorized") || message.Contains("not logged") || message.Contains("authentication") || message.Contains("token")) return CodexLogin();
            if (code == 429 || message.Contains("429") || message.Contains("too many")) return Http("codex", 429);
            return For("codex", null);
        }
    }
}
