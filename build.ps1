# exe를 다시 만든다. 윈도우에 들어 있는 .NET Framework 컴파일러(csc)만 쓴다.
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$PSScriptRoot\link.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"$fw\System.Web.Extensions.dll" `
    /r:System.ServiceProcess.dll /r:System.Core.dll /r:System.Security.dll /r:Microsoft.VisualBasic.dll `
    "$PSScriptRoot\src\*.cs"
exit $LASTEXITCODE
