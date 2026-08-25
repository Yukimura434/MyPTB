using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Admin.UI.ViewModels
{
 public sealed class DiagnosticsViewModel:PageViewModel
 {
  readonly IStatsRepository stats;DataStatisticsSnapshot snapshot=new DataStatisticsSnapshot{RecentCaptures=new RecentCaptureStatistics[0]};string error;bool loading;
  public DiagnosticsViewModel(IStatsRepository stats){this.stats=stats;RefreshCommand=new AsyncCommand(_=>Refresh());_=Refresh();}
  public override string Title=>"Dữ liệu & thống kê";
  public DataStatisticsSnapshot Snapshot{get=>snapshot;private set{if(Set(ref snapshot,value)){Raise(nameof(TotalOriginalCount));Raise(nameof(TotalAssetCount));Raise(nameof(TotalAssetSize));Raise(nameof(UpdatedText));}}}
  public long TotalOriginalCount=>(Snapshot?.PictureCount??0)+(Snapshot?.MotionPhotoCount??0);
  public long TotalAssetCount=>(Snapshot?.PictureCount??0)+(Snapshot?.MotionPhotoCount??0)+(Snapshot?.CompositeCount??0)+(Snapshot?.GifCount??0)+(Snapshot?.ShareArchiveCount??0);
  public string TotalAssetSize=>FormatBytes(Snapshot?.TotalAssetBytes??0);
  public string UpdatedText=>Snapshot==null?string.Empty:"Cập nhật "+Snapshot.GeneratedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
  public string Error{get=>error;private set{Set(ref error,value);Raise(nameof(HasError));}}public bool HasError=>!string.IsNullOrWhiteSpace(Error);
  public bool IsLoading{get=>loading;private set=>Set(ref loading,value);}
  public ICommand RefreshCommand{get;}
  async Task Refresh(){try{IsLoading=true;Error=null;Snapshot=await stats.GetDataStatisticsAsync(CancellationToken.None);}catch(Exception e){Error="Không thể đọc dữ liệu thống kê: "+e.Message;}finally{IsLoading=false;}}
  static string FormatBytes(long value){var units=new[]{"B","KB","MB","GB","TB"};double size=Math.Max(0,value);var index=0;while(size>=1024&&index<units.Length-1){size/=1024;index++;}return size.ToString(index==0?"N0":"N1")+" "+units[index];}
 }
}
