using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class FeatureFlagService : IFeatureFlagService
    {
        readonly ApplicationOptions options; readonly ISettingsService settings; public FeatureFlagService(ApplicationOptions options,ISettingsService settings){this.options=options;this.settings=settings;}
        public async Task<bool> IsEnabledAsync(string feature,CancellationToken token){var configured=options.Features.TryGetValue(feature,out var enabled)&&enabled;var s=await settings.GetAsync(token);switch(feature.ToUpperInvariant()){case "QR":return s.EnableQr;case "PLUGINS":return s.EnablePlugins;case "DIAGNOSTICS":return s.EnableDiagnostics;case "TELEMETRY":return s.EnableTelemetry;case "VIDEO":return configured&&options.Features.TryGetValue("VideoNativeEncoder",out var encoder)&&encoder;default:return configured;}}
    }

    public sealed class StorageManager : IStorageManager
    {
        static readonly string[] Areas={"Captures","Frames","Presets","Preview","Print","Logs","Temp","Backup","Public"};
        readonly ApplicationOptions options; readonly ISettingsService settings;
        public StorageManager(ApplicationOptions options,ISettingsService settings){this.options=options;this.settings=settings;foreach(var area in Areas)Directory.CreateDirectory(GetPath(area));}
        public string GetPath(string area){if(!Areas.Contains(area,StringComparer.OrdinalIgnoreCase))throw new ArgumentOutOfRangeException(nameof(area));return Path.Combine(options.DataDirectory,area);}
        public async Task CleanupAsync(CancellationToken token){var s=await settings.GetAsync(token)??new Settings();Cleanup(GetPath("Temp"),DateTime.UtcNow.AddHours(-Math.Max(1,s.TemporaryFileRetentionHours)),token);Cleanup(GetPath("Preview"),DateTime.UtcNow.AddHours(-Math.Max(1,s.TemporaryFileRetentionHours)),token);Cleanup(GetPath("Captures"),DateTime.UtcNow.AddDays(-Math.Max(1,s.SessionRetentionDays)),token);}
        static void Cleanup(string root,DateTime cutoff,CancellationToken token){foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){token.ThrowIfCancellationRequested();try{if(File.GetLastWriteTimeUtc(file)<cutoff)File.Delete(file);}catch{}}foreach(var dir in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories).OrderByDescending(x=>x.Length))try{if(!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);}catch{}}
    }

    public sealed class LocalUploadService : IUploadService
    {
        readonly IStorageManager storage; public LocalUploadService(IStorageManager storage){this.storage=storage;} public string ProviderName=>"Local";
        public Task<UploadResult> UploadAsync(string filePath,CancellationToken token)=>Task.Run(()=>{try{token.ThrowIfCancellationRequested();var target=Path.Combine(storage.GetPath("Public"),Guid.NewGuid().ToString("N")+Path.GetExtension(filePath));File.Copy(filePath,target,true);return new UploadResult{Succeeded=true,DownloadUri=new Uri(target),ProviderReference=target};}catch(Exception e){return new UploadResult{Succeeded=false,Error=e.Message};}},token);
    }

    public sealed class QrCodeService : IQrCodeService
    {
        public Task<byte[]> GeneratePngAsync(Uri content,int pixels,CancellationToken token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();var writer=new ZXing.BarcodeWriter{Format=ZXing.BarcodeFormat.QR_CODE,Options=new ZXing.QrCode.QrCodeEncodingOptions{Width=pixels,Height=pixels,Margin=2,CharacterSet="UTF-8",ErrorCorrection=ZXing.QrCode.Internal.ErrorCorrectionLevel.M}};using(var bitmap=writer.Write(content.AbsoluteUri))using(var ms=new MemoryStream()){bitmap.Save(ms,System.Drawing.Imaging.ImageFormat.Png);return ms.ToArray();}},token);
    }

    public sealed class PrintQueueService : IPrintQueueService, IDisposable
    {
        readonly IPrinterService printer; readonly ILogger<PrintQueueService> log;readonly string statePath; readonly ConcurrentDictionary<Guid,PrintQueueItem> jobs=new ConcurrentDictionary<Guid,PrintQueueItem>(); readonly ConcurrentQueue<Guid> queue=new ConcurrentQueue<Guid>(); readonly SemaphoreSlim signal=new SemaphoreSlim(0); readonly CancellationTokenSource stop=new CancellationTokenSource(); readonly Task worker;
        public PrintQueueService(IPrinterService printer,ILogger<PrintQueueService> log,ApplicationOptions options){this.printer=printer;this.log=log;var dir=Path.Combine(options.DataDirectory,"Print");Directory.CreateDirectory(dir);var owner=(options.ApplicationName??"PhotoBooth").Replace('.','-');statePath=Path.Combine(dir,"queue-"+owner+".json");Load();worker=Task.Run(Work);}
        public event EventHandler<PrintQueueItem> JobChanged;
        public Task<Guid> EnqueueAsync(PrintJob job,CancellationToken token){token.ThrowIfCancellationRequested();if(job.Id==Guid.Empty)job.Id=Guid.NewGuid();var item=new PrintQueueItem{Job=job,Status=PrintJobStatus.Queued,CreatedAtUtc=DateTime.UtcNow};jobs[job.Id]=item;queue.Enqueue(job.Id);signal.Release();Changed(item);return Task.FromResult(job.Id);}
        public Task CancelAsync(Guid id,CancellationToken token){token.ThrowIfCancellationRequested();if(jobs.TryGetValue(id,out var item)&&item.Status==PrintJobStatus.Queued){item.Status=PrintJobStatus.Cancelled;Changed(item);}return Task.CompletedTask;}
        public Task RetryAsync(Guid id,CancellationToken token){token.ThrowIfCancellationRequested();if(jobs.TryGetValue(id,out var item)&&(item.Status==PrintJobStatus.Failed||item.Status==PrintJobStatus.Cancelled)){item.Status=PrintJobStatus.Queued;item.Error=null;queue.Enqueue(id);signal.Release();Changed(item);}return Task.CompletedTask;}
        public IReadOnlyList<PrintQueueItem> Snapshot()=>jobs.Values.OrderBy(x=>x.CreatedAtUtc).ToList();
        async Task Work(){while(!stop.IsCancellationRequested){try{await signal.WaitAsync(stop.Token);if(!queue.TryDequeue(out var id)||!jobs.TryGetValue(id,out var item)||item.Status!=PrintJobStatus.Queued)continue;item.Status=PrintJobStatus.Printing;item.Attempts++;Changed(item);try{await printer.PrintAsync(item.Job,stop.Token);item.Status=PrintJobStatus.Completed;item.CompletedAtUtc=DateTime.UtcNow;Changed(item);}catch(Exception e){item.Error=e.Message;log.LogError(e,"Print job {JobId} failed",id);if(item.Attempts<item.MaximumAttempts){item.Status=PrintJobStatus.Queued;queue.Enqueue(id);signal.Release();}else item.Status=PrintJobStatus.Failed;Changed(item);}}catch(OperationCanceledException){break;}catch(Exception e){log.LogError(e,"Print queue worker failure");}}}
        void Changed(PrintQueueItem item){Save();JobChanged?.Invoke(this,item);}void Save(){try{var temp=statePath+".tmp";using(var stream=File.Create(temp))new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<PrintQueueItem>)).WriteObject(stream,Snapshot().ToList());if(File.Exists(statePath))File.Delete(statePath);File.Move(temp,statePath);}catch(Exception e){log.LogWarning(e,"Print queue state could not be saved");}}void Load(){try{if(!File.Exists(statePath))return;List<PrintQueueItem> values;using(var stream=File.OpenRead(statePath))values=(List<PrintQueueItem>)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<PrintQueueItem>)).ReadObject(stream);foreach(var item in values??new List<PrintQueueItem>()){jobs[item.Job.Id]=item;if(item.Status==PrintJobStatus.Queued||item.Status==PrintJobStatus.Printing){item.Status=PrintJobStatus.Queued;queue.Enqueue(item.Job.Id);signal.Release();}}}catch(Exception e){log.LogWarning(e,"Print queue state could not be restored");}}public void Dispose(){stop.Cancel();signal.Release();try{worker.Wait(2000);}catch{}Save();signal.Dispose();stop.Dispose();}
    }

    public sealed class RecoveryService : IRecoveryService, IDisposable
    {
        readonly ICameraService camera; readonly ILiveViewService live; readonly ILogger<RecoveryService> log;readonly bool restartLive; CancellationTokenSource source; Task loop; bool started;
        public RecoveryService(ICameraService camera,ILiveViewService live,ILogger<RecoveryService> log,ApplicationOptions options=null){this.camera=camera;this.live=live;this.log=log;restartLive=options==null||options.RestartLiveViewDuringRecovery;}
        public Task StartAsync(CancellationToken token){if(started)return Task.CompletedTask;started=true;source=CancellationTokenSource.CreateLinkedTokenSource(token);loop=Task.Run(()=>Run(source.Token));return Task.CompletedTask;}
        async Task Run(CancellationToken token){try{var cameras=await camera.GetCamerasAsync(token);var recovered=cameras.FirstOrDefault(x=>x.IsConnected);if(restartLive&&recovered!=null&&recovered.SupportsLiveView)await live.StartAsync(recovered.Id,token);}catch(OperationCanceledException){}catch(Exception e){log.LogWarning(e,"Camera recovery check failed");}}
        public async Task StopAsync(CancellationToken token){source?.Cancel();if(loop!=null)await Task.WhenAny(loop,Task.Delay(2000,token));loop=null;} public void Dispose(){source?.Cancel();source?.Dispose();}
    }

    public sealed class HealthStatusService : IHealthStatusService
    {
        readonly ICameraService cameras;readonly IPrinterService printers;readonly IPrintQueueService queue;readonly ApplicationOptions options;
        public HealthStatusService(ICameraService c,IPrinterService p,IPrintQueueService q,ApplicationOptions o){cameras=c;printers=p;queue=q;options=o;}
        public async Task<HealthSnapshot> GetSnapshotAsync(CancellationToken token){var cs=await cameras.GetCamerasAsync(token);var ps=await printers.GetPrintersAsync(token);var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory)));var pending=queue.Snapshot().Count(x=>x.Status==PrintJobStatus.Queued||x.Status==PrintJobStatus.Printing);using(var process=Process.GetCurrentProcess()){process.Refresh();return new HealthSnapshot{TimestampUtc=DateTime.UtcNow,Camera=cs.Any(x=>x.IsConnected)?ComponentHealth.Healthy:ComponentHealth.Unavailable,Printer=ps.Count>0?ComponentHealth.Healthy:ComponentHealth.Unavailable,Storage=drive.AvailableFreeSpace>512L*1024*1024?ComponentHealth.Healthy:ComponentHealth.Degraded,Queue=queue.Snapshot().Any(x=>x.Status==PrintJobStatus.Failed)?ComponentHealth.Degraded:ComponentHealth.Healthy,AvailableDiskBytes=drive.AvailableFreeSpace,ManagedMemoryBytes=GC.GetTotalMemory(false),WorkingSetBytes=process.WorkingSet64,PrivateMemoryBytes=process.PrivateMemorySize64,PeakWorkingSetBytes=process.PeakWorkingSet64,Is64BitProcess=Environment.Is64BitProcess,PendingPrintJobs=pending,CameraSdkVersion=typeof(CameraControl.Devices.CameraDeviceManager).Assembly.GetName().Version.ToString(),Messages=new string[0]};}}
    }

    public sealed class BackupService : IBackupService
    {
        readonly ApplicationOptions options;public BackupService(ApplicationOptions o){options=o;}
        public Task<string> ExportAsync(string zip,CancellationToken token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zip)));if(File.Exists(zip))File.Delete(zip);ZipFile.CreateFromDirectory(options.DataDirectory,zip,CompressionLevel.Optimal,false);return zip;},token);
        public Task ImportAsync(string zip,CancellationToken token)=>Task.Run(()=>{token.ThrowIfCancellationRequested();var staging=Path.Combine(Path.GetTempPath(),"photobooth-restore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);try{ZipFile.ExtractToDirectory(zip,staging);foreach(var file in Directory.EnumerateFiles(staging,"*",SearchOption.AllDirectories)){token.ThrowIfCancellationRequested();var relative=file.Substring(staging.Length).TrimStart(Path.DirectorySeparatorChar);var target=Path.GetFullPath(Path.Combine(options.DataDirectory,relative));if(!target.StartsWith(Path.GetFullPath(options.DataDirectory),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe backup entry.");Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);}}finally{try{Directory.Delete(staging,true);}catch{}}},token);
    }

    public sealed class PasswordService : IPasswordService
    {
        public string Hash(string password){var salt=new byte[16];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt);using(var derive=new Rfc2898DeriveBytes(password,salt,120000,HashAlgorithmName.SHA256)){return "PBKDF2$120000$"+Convert.ToBase64String(salt)+"$"+Convert.ToBase64String(derive.GetBytes(32));}}
        public bool Verify(string password,string encoded){try{var p=encoded.Split('$');var salt=Convert.FromBase64String(p[2]);using(var d=new Rfc2898DeriveBytes(password,salt,int.Parse(p[1]),HashAlgorithmName.SHA256))return Fixed(d.GetBytes(32),Convert.FromBase64String(p[3]));}catch{return false;}}
        static bool Fixed(byte[] a,byte[] b){if(a.Length!=b.Length)return false;var diff=0;for(var i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
    }

    public sealed class LocalizationService : ILocalizationService
    {
        string culture="en";readonly System.Resources.ResourceManager resources=new System.Resources.ResourceManager("PhotoBooth.Core.Resources.Strings",typeof(Settings).Assembly);
        public string Culture=>culture;public string Get(string key)=>resources.GetString(key,new System.Globalization.CultureInfo(culture))??key;public void SetCulture(string value){culture=string.Equals(value,"vi",StringComparison.OrdinalIgnoreCase)?"vi":"en";}
    }
}
