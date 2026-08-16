using System;using System.Threading;using System.Threading.Tasks;using System.Windows.Input;using PhotoBooth.Admin.UI.Mvvm;using PhotoBooth.Core.Models;using PhotoBooth.Core.Persistence;using PhotoBooth.Core.Services;
namespace PhotoBooth.Admin.UI.ViewModels
{
 public sealed class DiagnosticsViewModel:PageViewModel
 {
  readonly IHealthStatusService health;readonly IStatsRepository stats;HealthSnapshot snapshot;string error;long sessionCount;long captureCount;long printCount;public DiagnosticsViewModel(IHealthStatusService health,IStatsRepository stats){this.health=health;this.stats=stats;RefreshCommand=new AsyncCommand(_=>Refresh());_=Refresh();}public override string Title=>"Diagnostics";public HealthSnapshot Snapshot{get=>snapshot;private set=>Set(ref snapshot,value);}public string Error{get=>error;private set=>Set(ref error,value);}public long SessionCount{get=>sessionCount;private set=>Set(ref sessionCount,value);}public long CaptureCount{get=>captureCount;private set=>Set(ref captureCount,value);}public long PrintCount{get=>printCount;private set=>Set(ref printCount,value);}public ICommand RefreshCommand{get;}async Task Refresh(){try{Error=null;Snapshot=await health.GetSnapshotAsync(CancellationToken.None);SessionCount=await stats.CountSessionsAsync(CancellationToken.None);CaptureCount=await stats.CountCapturedImagesAsync(CancellationToken.None);PrintCount=await stats.CountSuccessfulPrintsAsync(CancellationToken.None);}catch(Exception e){Error=e.Message;}}
 }
}
