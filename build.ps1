# Builds bin\SonarFollow.exe with the C# compiler that ships with .NET Framework 4.x,
# so neither the .NET SDK nor Visual Studio is needed.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'bin') | Out-Null

& $csc -nologo -target:winexe -optimize+ `
    -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll `
    -out:"$PSScriptRoot\bin\SonarFollow.exe" "$PSScriptRoot\src\*.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
