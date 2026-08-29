using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
namespace PhotoBooth.Admin.UI.ViewModels
{
 public sealed class DiagnosticsViewModel:PageViewModel
 {
  readonly IStatsRepository stats;readonly IHealthStatusService health;DataStatisticsSnapshot snapshot=new DataStatisticsSnapshot{RecentCaptures=new RecentCaptureStatistics[0]};HealthSnapshot healthSnapshot=new HealthSnapshot();string error;bool loading;
  public DiagnosticsViewModel(IStatsRepository stats,IHealthStatusService health){this.stats=stats;this.health=health;RefreshCommand=new AsyncCommand(_=>Refresh());_=Refresh();}
  public override string Title=>"Dữ liệu & thống kê";
  public DataStatisticsSnapshot Snapshot{get=>snapshot;private set{if(Set(ref snapshot,value)){Raise(nameof(TotalOriginalCount));Raise(nameof(TotalAssetCount));Raise(nameof(TotalAssetSize));Raise(nameof(UpdatedText));}}}
  public long TotalOriginalCount=>(Snapshot?.PictureCount??0)+(Snapshot?.VideoCount??0);
  public long TotalAssetCount=>(Snapshot?.PictureCount??0)+(Snapshot?.VideoCount??0)+(Snapshot?.CompositeCount??0)+(Snapshot?.GifCount??0)+(Snapshot?.ShareArchiveCount??0);
  public string TotalAssetSize=>FormatBytes(Snapshot?.TotalAssetBytes??0);
  public string UpdatedText=>Snapshot==null?string.Empty:"Cập nhật "+Snapshot.GeneratedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
  public HealthSnapshot Health{get=>healthSnapshot;private set{if(Set(ref healthSnapshot,value)){Raise(nameof(CurrentRam));Raise(nameof(PeakRam));Raise(nameof(PrivateRam));Raise(nameof(ManagedRam));Raise(nameof(ProcessArchitecture));}}}
  public string CurrentRam=>FormatBytes(Health?.WorkingSetBytes??0);
  public string PeakRam=>FormatBytes(Health?.PeakWorkingSetBytes??0);
  public string PrivateRam=>FormatBytes(Health?.PrivateMemoryBytes??0);
  public string ManagedRam=>FormatBytes(Health?.ManagedMemoryBytes??0);
  public string ProcessArchitecture=>(Health?.Is64BitProcess??false)?"x64":"x86 — cần giữ RAM thấp hơn giới hạn địa chỉ của tiến trình 32-bit";
  public string Error{get=>error;private set{Set(ref error,value);Raise(nameof(HasError));}}public bool HasError=>!string.IsNullOrWhiteSpace(Error);
  public bool IsLoading{get=>loading;private set=>Set(ref loading,value);}
  public ICommand RefreshCommand{get;}
  async Task Refresh(){try{IsLoading=true;Error=null;var statsTask=stats.GetDataStatisticsAsync(CancellationToken.None);var healthTask=health.GetSnapshotAsync(CancellationToken.None);await Task.WhenAll(statsTask,healthTask);Snapshot=await statsTask;Health=await healthTask;}catch(Exception e){Error="Không thể đọc dữ liệu thống kê: "+e.Message;}finally{IsLoading=false;}}
  static string FormatBytes(long value){var units=new[]{"B","KB","MB","GB","TB"};double size=Math.Max(0,value);var index=0;while(size>=1024&&index<units.Length-1){size/=1024;index++;}return size.ToString(index==0?"N0":"N1")+" "+units[index];}
 }
}
