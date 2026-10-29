# 개발 빌드와 독립 릴리스

공개 소스는 Nephylo 1.25.34을 기준으로 합니다. 운영 배포 이력과 서버 설정은 포함하지 않습니다. 공식 설치 파일은 [nephylo.com](https://nephylo.com/)에서 제공합니다.

## 1. 새 PC에서 준비할 것

Windows PowerShell 5.1, .NET Framework 4.8과 Node.js를 준비합니다. `C:\src\nephylo`처럼 짧은 경로를 사용하세요.

```powershell
powershell.exe -NoProfile -File work/build.ps1
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
.\work\build\UsageWidget.exe --demo
```

설치기를 만들 때만 Inno Setup 7.1 이상이 필요합니다. `work/build-setup.ps1 -Compiler` 또는 `INNO_SETUP_COMPILER`로 컴파일러 경로를 지정할 수 있습니다. 일부 추가 설치 회귀 테스트는 `work/tools/innosetup-7.1.0/ISCC.exe`를 사용합니다. 컴파일러는 저장소에 동봉하지 않습니다.

## 2. 포크의 독립 배포

먼저 [독립 배포 안내](OPEN-SOURCE.md#포크의-독립-배포)에 따라 설치 경로·식별자·업데이트 URL·검증 공개키를 자신의 것으로 분리하세요. 공식 Nephylo 경로나 키를 사용해 포크를 배포하지 마세요.

새 산출물 폴더에 `work/build.ps1 -OutputDirectory <폴더>`로 빌드합니다. 같은 폴더에 안내용 `README.txt`를 준비하고 `work/compose-notices.ps1 -OutputPath <폴더>/THIRD-PARTY-NOTICES.txt`로 프로젝트와 외부 라이선스 고지를 생성합니다. 버전은 `work/src/AssemblyInfo.cs`와 맞춥니다.

`work/build-setup.ps1 -Version <버전> -SourceDirectory <폴더> -OutputDirectory <새 설치기 폴더>`로 설치기를 만들 수 있습니다. 기존 공개 버전 번호나 파일을 덮어쓰지 않습니다.

자동 업데이트 서명 형식은 [서명 안내](../work/UPDATE-RELEASES.md)를 참고하세요. 서명 개인키와 로그인 정보는 Git·공개 설치기에 포함하지 않습니다. 공개용 CI는 개발 빌드와 격리 테스트를 수행하며 배포나 공식 릴리스 서명은 하지 않습니다.
