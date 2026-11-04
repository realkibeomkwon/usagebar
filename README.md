# UsageBar

AI 코딩 도구의 사용량(남은 한도와 초기화 시각)을 보여 주는 Windows 앱입니다. macOS의 [CodexBar](https://github.com/steipete/CodexBar)를 참고했습니다.

[Mindows](../mindows)와 함께 실행하면 Mindows 메뉴 바에 공급자별 로고와 남은 사용량(%)이 표시됩니다. Mindows 없이 실행하면 공급자마다 트레이 아이콘이 하나씩 생기고, 아이콘 안에 남은 사용량이 숫자로 표시됩니다.

## 실행

```bash
dotnet run -c Release
```

종료는 팝업 아래쪽의 **UsageBar 종료**를 사용합니다.

## 설치 프로그램

```powershell
.\installer\build.ps1
```

`installer\Output\UsageBar-<버전>-setup.exe`가 만들어집니다. 빌드하려면 [Inno Setup 6](https://jrsoftware.org/isinfo.php)(`winget install JRSoftware.InnoSetup`)이 필요합니다. 버전은 `UsageBar.csproj`의 `<Version>`을 따릅니다.

- 관리자 권한 없이 현재 사용자의 `%LOCALAPPDATA%\Programs\UsageBar`에 설치됩니다. .NET 런타임이 포함되어 있으므로 따로 설치하지 않아도 됩니다.
- 시작 메뉴에 바로 가기가 생기고, 설치 앱 목록에서 제거할 수 있습니다. UsageBar가 실행 중이면 설치 및 제거 전에 종료하라고 안내합니다.
- 제거하면 **로그인 시 자동 실행** 작업도 삭제됩니다. `%LOCALAPPDATA%\UsageBar`의 설정은 남겨 둡니다.
- UsageBar는 시작할 때 **로그인 시 자동 실행** 작업이 있으면 그 작업이 지금 실행 중인 UsageBar.exe를 가리키도록 갱신합니다. 그래서 개발 빌드에서 켜 둔 설정도 설치한 UsageBar를 처음 실행하면 설치 경로로 옮겨 갑니다.
- 코드 서명을 하지 않았으므로 처음 실행할 때 SmartScreen 경고가 표시될 수 있습니다.

## 공급자

로그인되어 있는 공급자만 표시됩니다. 사용량은 5분마다 갱신되며, 팝업의 **새로 고침**으로 즉시 갱신할 수도 있습니다.

## 설정

팝업의 **UsageBar 설정**을 누르면 설정 창이 열립니다. **일반**의 **로그인 시 자동 실행**을 켜면 Windows에 로그인할 때 UsageBar가 실행됩니다. 이 설정은 작업 스케줄러에 로그온 시 실행되는 `UsageBar` 작업으로 등록되며, 지금 실행 중인 UsageBar.exe의 경로가 기록됩니다. `HKCU\...\CurrentVersion\Run`과 달리 Explorer의 시작 프로그램 지연을 기다리지 않으므로 로그인 직후 실행되고, 네트워크가 연결되면 사용량을 바로 다시 확인합니다.

왼쪽에서 공급자를 고르면 오른쪽에 다음 항목이 표시됩니다.

- **메뉴 바에 표시**: 메뉴 바와 트레이 아이콘에 표시할지 정합니다. 메뉴 바에 마지막으로 남은 항목은 숨길 수 없습니다. 숨긴 공급자 목록은 `%LOCALAPPDATA%\UsageBar\settings.json`에 저장됩니다.
- **상태, 요금제, 업데이트**: 마지막 갱신 결과입니다.
- **로그인**: 새 콘솔 창에서 CLI의 로그인 명령(`claude auth login`, `codex login`, `grok login`)을 실행합니다. 로그인에 실패하면 창이 닫히지 않고 오류를 보여 줍니다. 창이 닫히면 사용량을 다시 확인하므로, 새로 로그인한 공급자는 다시 실행하지 않아도 메뉴 바에 나타납니다.

| 공급자 | 조건 | 가져오는 방법 |
|---|---|---|
| Claude | `~/.claude/.credentials.json`(Claude Code 로그인) | Claude OAuth 사용량 API |
| Codex | PATH의 `codex` 명령 | `codex app-server`의 `account/rateLimits/read` |
| Grok | PATH 또는 `~/.grok/bin`의 `grok` 명령 | `grok agent stdio`의 `_x.ai/billing` |

로그인 갱신은 공급자마다 다릅니다.

- Claude: 액세스 토큰(약 8시간 유효)이 만료되면 UsageBar가 Claude Code와 같은 방식으로 갱신합니다. Claude Code의 갱신 잠금(`~/.claude/.oauth_refresh.lock`과 이전 버전이 쓰는 `~/.claude.lock`)을 잡은 뒤 `.credentials.json`을 다시 읽고, 다른 프로세스가 이미 갱신하지 않았으면 리프레시 토큰으로 새 토큰을 받아 `claudeAiOauth`의 토큰 항목만 교체합니다. 그래서 부팅 직후 Claude Code나 Claude Desktop을 실행하지 않아도 사용량이 표시됩니다. 리프레시 토큰까지 만료되면 터미널에서 `claude auth login`을 실행합니다.
- Codex, Grok: 인증 파일은 읽기만 합니다. CLI가 직접 로그인을 갱신합니다. 로그인이 만료되면 터미널에서 `codex login` 또는 `grok login`을 실행합니다.

메뉴 바와 트레이 아이콘의 숫자는 가장 많이 사용한 한도 창의 남은 비율입니다(CodexBar의 자동 표시 방식).

## Mindows 연동 형식

UsageBar는 갱신할 때마다 `%LOCALAPPDATA%\UsageBar\status.json`을 통째로 교체하고, 종료할 때 삭제합니다. 토큰은 기록하지 않습니다.

```json
{
  "pid": 1234,
  "hwnd": 5678,
  "providers": [
    {
      "id": "claude",
      "name": "Claude",
      "logo": "<WPF 경로 데이터>",
      "remainingPercent": 44,
      "error": null,
      "plan": "Pro",
      "updatedAt": "2026-10-25T21:46:00+09:00",
      "windows": [ { "label": "주간", "usedPercent": 56, "resetsAt": "2026-10-26T15:00:00+00:00" } ]
    }
  ]
}
```

- `providers`에는 메뉴 바에 표시하도록 선택한 공급자만 들어갑니다.
- `remainingPercent`는 사용량을 아직 받지 못했으면 `null`입니다. `error`는 마지막 갱신에 실패했을 때의 메시지이며, 이때 `windows`에는 마지막으로 성공한 값이 남아 있습니다.
- Mindows는 15분 넘게 갱신되지 않은 파일을 무시합니다.
- 팝업 열기: Mindows는 `AllowSetForegroundWindow(pid)`를 호출한 뒤, `hwnd`에 `RegisterWindowMessage("UsageBar.ShowPopup")` 메시지를 보냅니다. `wParam`은 `providers`의 순서(인덱스)이고, `lParam`은 팝업 왼쪽 위 좌표(화면 픽셀, 하위 16비트 x, 상위 16비트 y)입니다.

## 제한 사항

- CodexBar에 있는 비용 및 토큰 집계(로컬 로그 기반), 30일 차트, 이전 한도 창 기록, 크레딧, 계정 전환, 상태 페이지, 알림은 아직 없습니다.
- Grok 사용량은 현재 크레딧 형식만 읽습니다. 새 주가 시작될 때처럼 사용률이 응답에 없으면 사용량을 표시하지 않습니다.
- 주 모니터만 지원합니다.

오류가 발생하면 `%LOCALAPPDATA%\UsageBar\crash.log`에 기록됩니다.

로고 경로 데이터는 CodexBar(MIT 라이선스)의 메뉴 바 아이콘에서 가져왔습니다.
