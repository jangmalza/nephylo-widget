# Nephylo Widget

**AI 서비스의 남은 사용량을, 한곳에서 가볍게.**

여러 AI 서비스의 잔여량과 초기화 시각을 Windows 바탕화면에서 확인하는 무료 오픈소스 위젯입니다. Nephylo 가입이나 결제 없이 모든 기능을 사용할 수 있습니다.

**버전 안내:** 이 README는 **1.25.34 소스** 기준입니다. **1.25.33**부터 위젯 전용 설치 파일에 Claude 직접 잔여량 조회를 포함합니다. 기존 공개 1.25.22는 자동 업데이트하거나 공식 설치 파일로 업데이트할 수 있으며, 이전 버전의 응답 수신 안내도 아래에 보존합니다.

**[Windows 설치 파일 다운로드](https://nephylo.com/downloads/Nephylo-Setup.exe)** · [홈페이지](https://nephylo.com/) · [개발 후원](https://github.com/sponsors/jangmalza) · [English](#english) · [MIT License](LICENSE)

<p align="center">
  <img src="docs/images/widget.png" width="360" alt="Nephylo 다크 모드 위젯: Codex의 5시간·주간 잔여량, ElevenLabs 크레딧과 초기화 시각" />
</p>

<p align="center"><sub>1.25.21의 실제 WPF UI를 예시 데이터로 렌더링한 화면입니다. 실계정 정보나 실제 사용량을 포함하지 않습니다.</sub></p>

## 설치하고 시작하기

1. [Nephylo-Setup.exe](https://nephylo.com/downloads/Nephylo-Setup.exe)를 다운로드해 실행합니다. 압축 해제나 개발 도구 설치는 필요하지 않습니다.
2. 설치를 마치고 Nephylo를 실행합니다. 창이 보이지 않으면 Windows 시작 메뉴에서 **Nephylo**를 엽니다.
3. 위젯의 **＋ → 서비스 연결 · 관리**에서 아래의 첫 연결 예시를 따라 사용할 AI 서비스를 연결합니다.
4. 잔여량을 확인하세요. **… → 대시보드 열기**에서 전체 현황을, **… → 간략 보기**에서 미니바를 사용할 수 있습니다.

지원 환경: **Windows 10 1903 이상(x64) 또는 Windows 11(x64/ARM64)**, .NET Framework 4.8. 각 AI 서비스의 로그인·API 키·구독 및 이용 요금은 별도이며, 연결한 서비스만 위젯에 표시합니다.

<details>
<summary><strong>Windows 보안 경고가 표시되나요?</strong></summary>

현재 EXE에는 **Windows 코드 서명이 적용되지 않아** SmartScreen에서 알 수 없는 게시자 등의 경고가 표시될 수 있습니다. [공식 다운로드 페이지](https://nephylo.com/#download)에서 배포 주소와 SHA-256을 확인하세요. 설치 환경은 [설치 FAQ](https://nephylo.com/#faq)에서 확인할 수 있습니다.

Nephylo가 확인하는 **업데이트 정보의 배포 서명·파일 무결성**은 Windows 코드 서명과 별개이며, Windows의 보안 경고가 사라진다는 뜻이 아닙니다. 설치가 막히면 [이슈](https://github.com/jangmalza/nephylo-widget/issues)에 Windows 버전·설치 파일 버전·오류 문구를 알려주세요. API 키나 인증 정보는 포함하지 마세요.

</details>

<details>
<summary><strong>PowerShell로 설치하기</strong></summary>

1. [홈페이지의 PowerShell 설치 안내](https://nephylo.com/#powershell-install)를 펼칩니다.
2. 확인된 설치 명령을 복사하고, 실행할 내용을 확인합니다.
3. 일반 PowerShell 창에 붙여넣고 실행합니다.

홈페이지는 설치 스크립트의 SHA-256을 확인한 뒤 명령을 제공합니다. 스크립트는 서명된 업데이트 정보와 다운로드한 EXE를 검증합니다. 원문은 [install-nephylo.ps1](https://nephylo.com/downloads/install-nephylo.ps1)에서 읽을 수 있습니다.

</details>

### 첫 서비스 연결

**Codex·Claude Code·Higgsfield용 앱이나 CLI는 Nephylo 설치기에 포함되지 않습니다.** 사용할 서비스의 도구를 별도로 설치하고 로그인해야 합니다. Codex나 Claude Code를 WSL에서 연결한다면 선택할 배포판 안에 해당 CLI를 설치·로그인하세요.

| 서비스 | 미리 준비할 것 | 위젯에서 할 일 |
| --- | --- | --- |
| Codex | Windows용 Codex 앱 또는 CLI를 설치하고 로그인합니다. | **＋ → Codex → 연결 환경**에서 Windows 또는 WSL 배포판을 선택하고 **연결 확인**을 누릅니다. 로그인이 필요하면 **브라우저로 로그인**을 사용합니다. |
| ElevenLabs | API 키에 **사용자 → 읽기(User → Read)** 권한을 켭니다. | **＋ → ElevenLabs**에 키를 붙여 넣고 **연결 확인 후 저장**을 누릅니다. |
| Claude Code | 직접 조회에는 Windows 또는 선택한 WSL의 공식 Claude Code **2.1.304 이상**과 한도 정보를 제공하는 Pro·Max 계정 로그인이 필요합니다. | **＋ → Claude → 연결 환경 → Claude Code 연결 → 잔여량 조회**로 확인합니다. 이후 위젯 새로고침·자동 조회로 갱신하며 Claude와 새 대화를 할 필요가 없습니다. |
| OpenAI API | 조직 소유자의 **Admin API 키**를 준비합니다. 일반 모델 호출용 API 키로는 비용을 조회할 수 없습니다. | **＋ → OpenAI API**에 키를 입력하고 **연결 확인 후 저장**을 누릅니다. 필요하면 고급 설정에서 월 예산을 입력합니다. 이 값은 위젯의 비교 기준이며 실제 결제 한도를 바꾸지 않습니다. |

현재 위젯은 **잔여량 조회 완료**와 **이전 값 · 새로고침 필요**를 구분합니다. 직접 조회가 지원되지 않으면 CLI 업데이트·로그인 안내를 확인하세요. Claude Free·API 키 계정이나 웹만 사용하는 경우에는 **수동 기록**을 이용할 수 있습니다.

**기존 응답 수신 방식(이전 공개 1.25.22 포함):** Claude Code 연결 후 CLI를 다시 열고 평소 대화의 새 응답을 받습니다. 이전 1.25.22의 버튼 이름은 **수신 확인**이며, 1.25.33은 **잔여량 조회**입니다. 기존 방식의 **첫 수신 대기**는 아직 데이터가 도착하지 않았다는 뜻이고 **수신됨 · 한도 미제공**은 수신했지만 한도가 없다는 뜻입니다. 기존 상태 표시줄 스크립트는 자동으로 덮어쓰지 않습니다.

나머지 서비스의 준비물과 오류별 해결 방법은 [서비스별 연결·해결 안내](https://nephylo.com/#connect)에서 확인하세요. API 키나 로그인 토큰을 이슈에 첨부하지 마세요.

## 주요 기능

| 기능 | 할 수 있는 일 |
| --- | --- |
| 잔여량과 초기화 시각 | 서비스별 한도·크레딧·예산을 확인하고 초기화까지 남은 시간을 파악합니다. |
| 위젯과 미니바 | 바탕화면 위젯을 작은 가로 막대로 전환합니다. 미니바 너비는 표시하는 서비스 수에 맞춰 조절됩니다. |
| 통합 대시보드 | 여러 서비스의 현황, 초기화 일정, 사용 이력과 잔량 분석을 한 창에서 봅니다. |
| 잔량·초기화 알림 | 잔여량 부족과 초기화 일정을 알림으로 확인합니다. |
| 이력과 CSV | PC에 최대 30일의 관측 이력을 보관하고 CSV로 내보냅니다. |
| 사용 추세 분석 | 관측된 이력으로 하루 평균 소모량·소진 예상·주간 요약을 확인합니다. |
| 취향에 맞는 설정 | 다크·라이트 테마, 한국어·영어, 애니메이션, 항상 위에 표시와 Windows 시작 시 실행을 설정합니다. |
| 자동 업데이트 | 설치된 위젯이 새 버전을 감지하고 검증한 뒤 다운로드·설치·재시작합니다. |

### 자주 쓰는 기능 열기

| 하고 싶은 일 | 메뉴 경로 |
| --- | --- |
| 알림 기준·이력 기록 설정, 테스트 알림 보내기 | **… → 알림 · 이력 설정** |
| 사용 이력 CSV 저장 | **… → 사용 이력**에서 서비스·항목·기간을 선택한 뒤 **CSV 내보내기**. 대시보드의 **사용 이력**에서도 내보낼 수 있습니다. |
| 언어·테마 변경 | 상단 **⚙ → 언어 / Language·화면 테마 → 저장** |
| Windows 시작 시 실행·자동 갱신 간격 설정 | **⚙ → 자동 실행과 갱신 → 저장** |
| 자동 업데이트 켜기·끄기, 수동으로 새 버전 확인 | **… → 버전 · 설치 · 업데이트**에서 **자동으로 최신 버전 유지** 또는 **새 버전 확인** |

**알림은 위젯이 실행 중일 때 작동합니다.** 트레이에 숨겨도 받을 수 있으며, Windows 알림 설정이나 방해 금지에 따라 표시되지 않을 수 있습니다. **알림 · 이력 설정 → 알림 테스트**로 확인하세요. 초기화 알림은 알려진 예정 시각을 기준으로 하며, 실제 잔량이 충전됐는지는 새로고침 후 확인합니다.

<details>
<summary><strong>설치 직후 이력이나 소진 예상이 비어 있나요?</strong></summary>

- **이력은 기록을 켠 뒤 이 PC에서 확인한 값부터 쌓입니다.** 설치 전 과거 30일을 가져오지 않습니다. 위젯을 종료한 동안의 사용량을 계속 수집하지 않으며 관측 공백을 사용량 0으로 채우지 않습니다.
- **하루 평균은 오늘을 제외한 최근 7일 중 1시간 이상 관측한 날**의 실제 관측 소모량으로 계산합니다. 설치 첫날에는 표시할 완료일이 없을 수 있습니다.
- **소진 예상은 즉시 계산되지 않습니다.** 현재 기본 조건은 첫 관측 후 최소 **72시간** 경과, 최근 7일 중 하루 **4시간 이상 관측한 완료일 3일**, 예측용 유효 관측 합계 **12시간 이상**입니다. 조건을 충족해도 최근 값과 초기화 시각이 유효하고 소모 변화가 있어야 예상 시점을 계산할 수 있습니다.
- 수동 기록과 5시간처럼 짧은 한도는 소진 예상 대상이 아닙니다. **데이터 수집 중·기록 부족**은 필요한 이력을 모으는 상태이며 연결 실패를 뜻하지 않습니다.

</details>

분석 결과는 수집된 이력에 따른 추정치입니다. 서비스의 마지막 확인 시각과 조회 방식을 함께 확인하세요.

## 화면 살펴보기

### 작업을 가리지 않는 미니바

서비스 아이콘·잔여량·초기화 시각을 간결하게 표시합니다. 서비스를 누르면 상세 정보를 확인할 수 있습니다.

![Codex·ElevenLabs·Claude의 잔여량을 한 줄에 표시하는 Nephylo 미니바](docs/images/minibar.png)

### 여러 서비스를 모아 보는 대시보드

위젯의 **… → 대시보드 열기**에서 시작합니다. 서비스별 잔량과 초기화 일정, 확인이 필요한 항목을 한곳에서 확인합니다.

![Nephylo 통합 대시보드: 서비스별 잔여량과 초기화 현황](docs/images/dashboard.png)

<details>
<summary><strong>사용 이력 그래프도 보기</strong></summary>

서비스·항목·기간을 선택해 잔량 추이를 살펴봅니다. 점은 실제로 관측한 값을 나타내며 수동 기록이나 수집 공백을 연속 사용량으로 취급하지 않습니다.

![Nephylo 사용 이력: Codex 주간 잔량 추이와 관측 소모량 비교](docs/images/history.png)

</details>

위 화면은 모두 예시 데이터입니다. 서비스 종류와 연결 상태에 따라 표시 항목이 달라집니다.

## 서비스별 조회 방식

모든 서비스가 같은 방법으로 잔여량을 제공하지는 않습니다.

| 서비스 | 표시하는 정보·연결 방식 |
| --- | --- |
| Codex | 로그인된 Codex 앱·CLI에서 계정 한도와 초기화 시각을 조회합니다. Windows와 WSL 환경을 지원합니다. **일반 ChatGPT 채팅의 남은 메시지 횟수는 표시하지 않습니다.** |
| ElevenLabs | 사용자 읽기 권한이 있는 API 키로 구독 크레딧 잔여량을 조회합니다. |
| Claude Code | 1.25.33부터 공식 CLI 2.1.304 이상에서 대화 없이 5시간·주간 잔여량을 조회합니다. 기존 응답 수신도 유지하며 공개 1.25.22는 이 수신 방식을 사용합니다. **Free·API 키 계정의 구독 잔여량 자동 조회는 지원하지 않으며 수동 기록을 이용할 수 있습니다.** |
| OpenAI API | Admin API 키로 조회한 이번 달 조직 비용과 사용자가 설정한 예산을 비교합니다. 실제 선불 잔액이나 ChatGPT 구독 잔량을 뜻하지 않습니다. |
| Higgsfield 웹 | 공식 CLI 로그인과 작업 공간 연결 후 웹 구독 크레딧을 조회합니다. |
| Higgsfield API | API 콘솔에서 확인한 USD 잔액을 수동 기록합니다. 웹 구독 크레딧과 별도입니다. |
| Gemini API·Cursor | 사용자가 확인한 잔량을 수동 기록하고 기록 시각을 함께 표시합니다. |

Nephylo는 각 AI 서비스의 공식 제품이 아닙니다. 제공자가 공개하는 정보, 계정 요금제와 인증 상태에 따라 조회 범위가 달라질 수 있습니다.

## 데이터는 어디에 보관하나요?

- 입력한 API 키는 **현재 Windows 사용자 계정에 묶어 암호화(Windows DPAPI)** 해 PC에 저장합니다. 키는 연결한 AI 서비스의 조회 인증에 사용하며 Nephylo 웹사이트나 회원 서버로 보내지 않습니다.
- 서비스 설정·수동 기록·사용 이력도 이 PC의 **`%USERPROFILE%\.nephylo`** 폴더에 보관합니다. 사용 이력에 API 키나 로그인 토큰을 넣지 않습니다.
- **앱을 제거해도 사용자 데이터 폴더는 남습니다.** 서비스 관리에서 저장된 키를 삭제할 수 있으며, 사용 이력 화면에서 기록을 지울 수 있습니다. 저장 폴더와 내보낸 파일은 별도로 관리하세요.

자세한 처리 방식은 [개인정보 안내](https://nephylo.com/privacy/)를 확인하세요. 저장 폴더나 인증 파일 전체를 공개 이슈에 업로드하지 마세요.

## 개발·기여

현재 소스 버전: **1.25.34** · C# / WPF / .NET Framework 4.8

<details>
<summary><strong>소스에서 빌드하고 테스트하기</strong></summary>

Windows PowerShell 5.1과 .NET Framework 4.8을 사용합니다. 테스트 경로 길이 제한을 피하려면 `C:\src\nephylo`처럼 짧은 경로에 clone하세요.

```powershell
git clone https://github.com/jangmalza/nephylo-widget.git C:\src\nephylo
cd C:\src\nephylo
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\work\build.ps1
if ($LASTEXITCODE -ne 0) { throw '빌드 실패' }
.\work\build\UsageWidget.exe --demo
```

`--demo`는 실제 서비스에 연결하지 않고 예시 데이터로 실행합니다. 일반 실행은 해당 Windows 사용자의 로컬 프로필을 사용합니다. 소스에서 만든 개발 빌드는 자동 설치 대상이 아닙니다.

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\work\tests\run-ci-tests.ps1
```

공개 CI는 Windows 개발 빌드·48개 오프라인 스위트, 서버 격리 테스트, 비밀정보 검사, 소스 구성·JavaScript 문법을 검사합니다. 운영 서버 배포나 실제 사용자 인증은 하지 않습니다.

</details>

[기여 안내](CONTRIBUTING.md) · [포크·독립 배포 안내](docs/OPEN-SOURCE.md) · [릴리스 절차](docs/RELEASING.md) · [이슈 등록](https://github.com/jangmalza/nephylo-widget/issues)

공개 소스는 검증된 스냅샷에서 자동 동기화됩니다. 운영 배포 자료·개인 설정·비공개 Git 이력·기존 EXE·ZIP은 포함하지 않습니다. 웹 관리자 설정은 비활성 예제이며 공식 업데이트 검증용 공개키만 포함합니다. 웹사이트를 포크해 서비스하려면 자신의 릴리스 산출물과 운영 설정이 필요합니다.

## 개발 후원

[GitHub Sponsors에서 후원하기](https://github.com/sponsors/jangmalza)로 서비스 연동 유지보수, 오류 수정과 호스팅·테스트 비용을 지원할 수 있습니다. 후원은 선택이며, 후원 여부와 관계없이 모든 위젯 기능을 무료로 사용할 수 있습니다. 별도의 전용 기능이나 우선 지원을 제공하는 상품이 아닙니다.

## 라이선스

Nephylo 코드·문서·자체 아이콘은 [MIT License](LICENSE)입니다. 수정·재배포·상업적 이용 시 저작권과 라이선스 고지를 유지하세요. 외부 구성요소는 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)와 [라이선스 목록](docs/LICENSES.md)의 조건을 따릅니다. 코드 공개가 서비스 상표나 외부 API 사용 권한을 대신하지는 않습니다.

## English

**Version scope:** this README describes source **1.25.34**. The widget-only **1.25.33** release includes direct Claude quota queries. Earlier public 1.25.22 installations can use automatic updates or the official setup file; legacy receipt instructions are retained below.

**Nephylo is a free, open-source Windows widget for AI service quotas, remaining credits and reset times.** No Nephylo account or payment is required. Provider authentication, subscriptions and API fees are separate.

**[Sponsor development on GitHub](https://github.com/sponsors/jangmalza):** optional contributions support integration maintenance, bug fixes, hosting and testing. All widget features remain free regardless of sponsorship. Sponsorship does not include exclusive features or priority support.

**Install:** [Download Nephylo-Setup.exe](https://nephylo.com/downloads/Nephylo-Setup.exe), run it, then use **＋ → Connect services**. No build tools are required. The [website](https://nephylo.com/) also provides a verified PowerShell installation command. Requires Windows 10 1903+ x64 or Windows 11 x64/ARM64 and .NET Framework 4.8.

If the widget is not visible after installation, open **Nephylo** from the Windows Start menu. Codex, Claude Code and Higgsfield tools are installed separately; they are not bundled with Nephylo. When connecting Codex or Claude Code through WSL, install and sign in to the CLI inside your selected distribution.

<details>
<summary><strong>First connection examples and Windows security notice</strong></summary>

- **Codex:** install and sign in to the Windows app/CLI, or the CLI in WSL. Select **＋ → Codex → connection environment**, choose Windows or your distribution, and check the connection. Use the browser sign-in option if needed. Codex quota is separate from ordinary ChatGPT chat message limits.
- **ElevenLabs:** enable **User → Read** on your API key, paste it under **＋ → ElevenLabs**, then check and save the connection.
- **Claude Code, 1.25.33 and later:** install official Claude Code **2.1.304 or later** in Windows or the selected WSL distribution and sign in with a Pro/Max account that provides quota data. Use **＋ → Claude → connection environment → Connect Claude Code → Check remaining usage**. Refresh or automatic polling updates the reading without sending a conversation prompt. Follow the CLI update or sign-in guidance if direct queries are unavailable. Free/API-key or web-only users can use manual records.
- **Legacy Claude receipts, including earlier public 1.25.22:** connect Claude Code, reopen the CLI and receive a normal response. The old 1.25.22 button is **Check received data**; 1.25.33 uses **Check remaining usage**. Waiting for a first receipt differs from receiving data without quota fields. Existing status-line scripts are preserved.
- **OpenAI API:** use an organization owner's **Admin API key**; ordinary model API keys cannot read organization costs. Check and save it under **＋ → OpenAI API**. The optional monthly budget is a comparison value and does not change your actual billing limit.

The current EXE has **no Windows code signature**, so SmartScreen may show an unknown-publisher warning. Check the source and SHA-256 on the [official download page](https://nephylo.com/#download); see the [installation FAQ](https://nephylo.com/#faq) or report your Windows version, installer version and error message in an [issue](https://github.com/jangmalza/nephylo-widget/issues). Do not include credentials. Nephylo's signed update metadata and file-integrity checks are separate from Windows code signing and do not guarantee that Windows warnings disappear.

</details>

**Features:** desktop widget, compact minibar, unified dashboard, quota/reset alerts, up to 30 days of local history, CSV export, usage forecasts, weekly summaries, dark/light themes, Korean/English and automatic updates for installed copies.

**Provider scope:** Codex and ElevenLabs expose quota/credit readings. Version 1.25.33 queries supported Claude limits directly through official CLI 2.1.304 or later without a conversation; legacy receipts, including public 1.25.22, update after a normal response. OpenAI API shows organization costs against your configured budget, not prepaid balance or ChatGPT subscription usage. Higgsfield web credits use its CLI connection. Higgsfield API balance, Gemini API and Cursor use manual records. Automatic Claude Free/API-key subscription quota readings are not supported; manual records remain available.

**Finding controls:** the **…** menu opens notification/history settings, usage history with **CSV export**, and version/installation/update management with automatic-update and manual-check controls. The top **⚙** opens language, theme, startup and refresh settings; save your changes. The dashboard's history page also exports the selected service, metric and period.

**Notifications:** Nephylo must be running; hiding it in the tray is fine. Windows notification settings or Do Not Disturb can suppress display. Use the notification test in notification/history settings. Reset reminders use known scheduled times; refresh to confirm actual replenishment.

<details>
<summary><strong>Why history or forecasts may be empty on first use</strong></summary>

History starts with readings observed on this PC after recording is enabled; it does not import the previous 30 days or keep collecting while Nephylo is closed. Daily averages use completed days from the previous seven days with at least one hour of observations, excluding today. Forecasts currently require at least **72 hours since first observation**, **three completed days from the previous seven days with four or more observed hours each**, and **12 total eligible observed hours**. They also need a fresh reading, a valid reset time and a consumption trend. Manual records and short limits such as five-hour windows do not receive depletion forecasts. A collecting/insufficient-history state does not itself mean the connection failed.

</details>

**Local data:** API keys are encrypted for the current Windows user using DPAPI and sent only to the connected provider for authentication, not to Nephylo's website or membership server. Settings, manual readings and history stay under **`%USERPROFILE%\.nephylo`**. Uninstalling leaves that data folder in place; remove saved keys in service management and clear history in the history view as needed. Manage exported files separately, and do not attach your profile or authentication files to public issues. See the [privacy notice](https://nephylo.com/privacy/).

The images above show the actual 1.25.21 WPF views rendered with built-in demo data, without live accounts. See [CONTRIBUTING.md](CONTRIBUTING.md) for contributions and [docs/OPEN-SOURCE.md](docs/OPEN-SOURCE.md) before distributing a fork. Licensed under [MIT](LICENSE); third-party notices remain applicable.
