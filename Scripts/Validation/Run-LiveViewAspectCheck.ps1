$ErrorActionPreference='Stop'
$bin=Join-Path (Get-Location) 'PhotoBooth.Admin.UI\bin\Release\net48'
$net='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs=@('mscorlib','System','System.Core','System.Xaml','WindowsBase','PresentationCore','PresentationFramework') | ForEach-Object {'/r:'+(Join-Path $net ($_+'.dll'))}
$refs+=@('PhotoBooth.Customer.UI.dll','PhotoBooth.Color.D3D11.dll') | ForEach-Object {'/r:'+(Join-Path $bin $_)}
Copy-Item -LiteralPath (Join-Path $bin 'MiuPhotoBooth.exe.config') -Destination (Join-Path $bin 'LiveViewAspectCheck.exe.config') -Force
$sdk=dotnet --version
$csc=Join-Path 'C:\Program Files\dotnet\sdk' "$sdk\Roslyn\bincore\csc.dll"
dotnet $csc /nologo /platform:x86 "/out:$bin\LiveViewAspectCheck.exe" @refs Scripts\Validation\LiveViewAspectCheck.cs
if($LASTEXITCODE -ne 0){throw 'Harness build failed'}
& "$bin\LiveViewAspectCheck.exe" (Join-Path (Get-Location) '.validation\x96-ui') *> .validation\x96-ui.log
$result=$LASTEXITCODE
Get-Content .validation\x96-ui.log -Tail 15
exit $result
