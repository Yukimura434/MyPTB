$ErrorActionPreference='Stop'
$bin=Join-Path (Get-Location) 'PhotoBooth.Admin.UI\bin\Release\net48'
$net='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs=@('mscorlib','System','System.Core','System.Xaml','WindowsBase','PresentationCore','PresentationFramework') | ForEach-Object {'/r:'+(Join-Path $net ($_+'.dll'))}
$refs+=@('MiuPhotoBooth.exe','PhotoBooth.Customer.UI.dll','PhotoBooth.Core.dll','PhotoBooth.Business.dll','PhotoBooth.Database.dll','PhotoBooth.Infrastructure.dll','PhotoBooth.Shared.dll','PhotoBooth.FrameEngine.dll','PhotoBooth.Color.D3D11.dll','PhotoBooth.OpenCvRetouch.dll','Microsoft.Extensions.DependencyInjection.dll','Microsoft.Extensions.DependencyInjection.Abstractions.dll','Microsoft.Extensions.Logging.Abstractions.dll','Microsoft.Extensions.Logging.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Threading.Tasks.Extensions.dll') | ForEach-Object {'/r:'+(Join-Path $bin $_)}
Copy-Item -LiteralPath (Join-Path $bin 'MiuPhotoBooth.exe.config') -Destination (Join-Path $bin 'X88Check.exe.config') -Force
$sdk=dotnet --version
$csc=Join-Path 'C:\Program Files\dotnet\sdk' "$sdk\Roslyn\bincore\csc.dll"
dotnet $csc /nologo /optimize+ /platform:x86 "/out:$bin\X88Check.exe" @refs Scripts\Validation\UnifiedUxCheck.cs Scripts\Validation\ApprovedUxCheck.cs Scripts\Validation\FourFixCheck.cs /main:FourFixEntry
if($LASTEXITCODE -ne 0){throw 'Harness build failed'}
& "$bin\X88Check.exe" (Join-Path (Get-Location) '.validation\x88\ui') *> .validation\x88\ui.log
$result=$LASTEXITCODE
Get-Content .validation\x88\ui.log -Tail 25
exit $result
