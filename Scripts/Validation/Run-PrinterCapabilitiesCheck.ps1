param([switch]$Print)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$bin=Join-Path $root 'PhotoBooth.Admin.UI\bin\Release\net48'
$net='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs=@('mscorlib','System','System.Core','System.Drawing','System.Xaml','System.Printing','ReachFramework','WindowsBase','PresentationCore','PresentationFramework') | ForEach-Object {'/r:'+(Join-Path $net ($_+'.dll'))}
$refs+=@('MiuPhotoBooth.exe','PhotoBooth.Core.dll','PhotoBooth.Business.dll','PhotoBooth.Database.dll','Microsoft.Extensions.Logging.Abstractions.dll') | ForEach-Object {'/r:'+(Join-Path $bin $_)}
Copy-Item -LiteralPath (Join-Path $bin 'MiuPhotoBooth.exe.config') -Destination (Join-Path $bin 'X111Check.exe.config') -Force
$sdk=dotnet --version
$csc=Join-Path 'C:\Program Files\dotnet\sdk' "$sdk\Roslyn\bincore\csc.dll"
dotnet $csc /nologo /optimize+ /platform:x86 "/out:$bin\X111Check.exe" @refs (Join-Path $PSScriptRoot 'PrinterCapabilitiesCheck.cs')
if($LASTEXITCODE -ne 0){throw 'Harness build failed'}
$arguments=@((Join-Path $root '.validation\x111-pc'))
if($Print){$arguments+='--print'}
& "$bin\X111Check.exe" @arguments
exit $LASTEXITCODE
