# 릴리스를 만든다: 빌드 -> 서명 -> 서명 확인 -> GitHub 릴리스.
#   .\release.ps1 -Notes notes.md          실제 릴리스
#   .\release.ps1 -Notes notes.md -DryRun  gh release create 만 빼고 전부 한다(git 검사는 경고만)
param(
    [string]$Notes,
    [switch]$DryRun,
    [string]$KeyName = "YeondongRelease"   # 시험할 때만 다른 이름을 준다
)
$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location $PSScriptRoot
$repo = "88bawi88/yeondong-kvm"

function Fail($m) { Write-Host "중단: $m" -ForegroundColor Red; exit 1 }

# 버전 -> 태그
$src = Get-Content "$PSScriptRoot\src\Program.cs" -Encoding UTF8 -Raw
if ($src -notmatch '\[assembly:\s*System\.Reflection\.AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]') { Fail "Program.cs 에서 AssemblyVersion 을 못 찾음" }
$tag = "v$($Matches[1]).$($Matches[2]).$($Matches[3])"
Write-Host "태그: $tag"

if (-not $DryRun) {
    if (-not $Notes -or -not (Test-Path $Notes)) { Fail "-Notes <파일> 이 필요함" }
}

# git 상태: 커밋하지 않은 변경이 없고, 현재 커밋이 푸시돼 있어야 한다.
# 실패해도 멈추지 않게 잠시 Continue (PS 5.1은 native stderr를 오류로 취급한다)
$ErrorActionPreference = "Continue"
$dirty = git status --porcelain 2>$null
$head = git rev-parse HEAD 2>$null
$up = git rev-parse '@{u}' 2>$null
$ErrorActionPreference = "Stop"
$gitProblem = $null
if ($dirty) { $gitProblem = "커밋하지 않은 변경이 있음" }
elseif (-not $up) { $gitProblem = "업스트림이 없음(푸시 안 됨)" }
elseif ($head -ne $up) { $gitProblem = "현재 커밋이 푸시되지 않음" }
if ($gitProblem) {
    if ($DryRun) { Write-Host "경고(DryRun이라 계속): $gitProblem" -ForegroundColor Yellow }
    else { Fail $gitProblem }
}

# 빌드
& "$PSScriptRoot\build.ps1"
if ($LASTEXITCODE -ne 0) { Fail "build.ps1 실패" }
if (-not (Test-Path "$PSScriptRoot\link.exe")) { Fail "link.exe 가 없음" }

# 서명 도구
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& "$fw\csc.exe" /nologo /target:exe /optimize+ /codepage:65001 /out:"$PSScriptRoot\tools\sign.exe" /r:System.Core.dll "$PSScriptRoot\tools\Sign.cs"
if ($LASTEXITCODE -ne 0) { Fail "sign.exe 컴파일 실패" }
$sign = "$PSScriptRoot\tools\sign.exe"

# 서명하고, 키 저장소의 공개키로 다시 확인한다.
Remove-Item "$PSScriptRoot\link.exe.sig" -ErrorAction SilentlyContinue
& $sign file $KeyName "$PSScriptRoot\link.exe"
if ($LASTEXITCODE -ne 0) { Fail "서명 실패" }
$pub = (& $sign pub $KeyName | Select-Object -First 1).Trim()
& $sign verify $pub "$PSScriptRoot\link.exe"
if ($LASTEXITCODE -ne 0) { Fail "서명 확인 실패" }

# 코드에 박힌 공개키와 같은지도 본다(다르면 업데이트가 항상 거부된다).
$upd = Get-Content "$PSScriptRoot\src\Update.cs" -Encoding UTF8 -Raw
if ($upd -match 'PublicKey\s*=\s*"([^"]*)"') {
    if ($Matches[1] -ne $pub) {
        $m = "src\Update.cs 의 PublicKey 가 키($KeyName) 공개키와 다름"
        if ($DryRun) { Write-Host "경고: $m" -ForegroundColor Yellow } else { Fail $m }
    }
}

if ($DryRun) {
    Write-Host "DryRun 완료. 올릴 파일: link.exe, link.exe.sig, 태그 $tag" -ForegroundColor Green
    exit 0
}

gh release create $tag "$PSScriptRoot\link.exe" "$PSScriptRoot\link.exe.sig" --repo $repo --title "연동 $tag" --notes-file $Notes
if ($LASTEXITCODE -ne 0) { Fail "gh release create 실패" }
Write-Host "릴리스 완료: $tag" -ForegroundColor Green
