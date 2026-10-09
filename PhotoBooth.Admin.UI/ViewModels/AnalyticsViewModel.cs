using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class AnalyticsViewModel : PageViewModel
    {
        readonly IStatsRepository statistics;
        IReadOnlyList<CaptureLibraryItem> captures = new CaptureLibraryItem[0];
        AnalyticsPeriodOption selectedPeriod;
        bool loading;
        string error;
        public AnalyticsViewModel(IStatsRepository statistics)
        {
            this.statistics = statistics;
            PeriodOptions = new[] { new AnalyticsPeriodOption("7 ngày gần nhất",7), new AnalyticsPeriodOption("30 ngày gần nhất",30), new AnalyticsPeriodOption("90 ngày gần nhất",90), new AnalyticsPeriodOption("500 lượt gần nhất",0) };
            selectedPeriod = PeriodOptions[1];
            RefreshCommand = new AsyncCommand(_ => RefreshAsync(), _ => !IsLoading);
            _ = RefreshAsync();
        }
        public override string Title => "Phân tích & báo cáo";
        public IReadOnlyList<AnalyticsPeriodOption> PeriodOptions { get; }
        public AnalyticsPeriodOption SelectedPeriod { get => selectedPeriod; set { if(Set(ref selectedPeriod,value)) Apply(); } }
        public bool IsLoading { get => loading; private set => Set(ref loading,value); }
        public string Error { get => error; private set { if(Set(ref error,value)) Raise(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public bool HasData { get; private set; }
        public string CaptureCountText { get; private set; } = "0";
        public string PictureCountText { get; private set; } = "0";
        public string ReadyCountText { get; private set; } = "0";
        public string PendingCountText { get; private set; } = "0";
        public string FailedCountText { get; private set; } = "0";
        public string DataScopeText { get; private set; } = "Chưa có dữ liệu";
        public IReadOnlyList<AnalyticsDayPoint> DailyPoints { get; private set; } = new AnalyticsDayPoint[0];
        public ICommand RefreshCommand { get; }
        public async Task RefreshAsync()
        {
            if(IsLoading)return;
            try
            {
                IsLoading=true;Error=null;
                var snapshot=await statistics.SearchCaptureLibraryAsync(new CaptureLibraryFilter { Mode=CaptureLibraryFilterModes.All,MaximumItems=500 },CancellationToken.None);
                captures=snapshot?.Captures??new CaptureLibraryItem[0];Apply();
            }
            catch(Exception exception){Error="Chưa thể tải báo cáo. Hãy thử làm mới.";System.Diagnostics.Debug.WriteLine(exception);}
            finally{IsLoading=false;}
        }
        void Apply()
        {
            var days=SelectedPeriod?.Days??30;
            var from=days==0?DateTime.MinValue:DateTime.Now.Date.AddDays(1-days);
            var items=captures.Where(x=>x.CreatedAtLocal>=from).ToList();
            CaptureCountText=items.Count.ToString("N0");PictureCountText=items.Sum(x=>Math.Max(0,x.PictureCount)).ToString("N0");
            ReadyCountText=items.Count(x=>string.Equals(x.Status,"GalleryReady",StringComparison.OrdinalIgnoreCase)).ToString("N0");
            PendingCountText=items.Count(x=>x.PendingSyncCount>0).ToString("N0");
            FailedCountText=items.Count(x=>x.FailedSyncCount>0).ToString("N0");
            HasData=items.Count>0;
            DataScopeText="Dữ liệu trên máy · tối đa 500 lượt gần nhất · cập nhật "+DateTime.Now.ToString("HH:mm dd/MM/yyyy");
            var chartDays=days==0?14:Math.Min(14,days);
            var grouped=items.GroupBy(x=>x.CreatedAtLocal.Date).ToDictionary(x=>x.Key,x=>x.Count());
            var dates=Enumerable.Range(0,chartDays).Select(x=>DateTime.Now.Date.AddDays(x+1-chartDays)).ToList();
            var maximum=Math.Max(1,dates.Max(x=>grouped.TryGetValue(x,out var count)?count:0));
            DailyPoints=dates.Select(x=>{var count=grouped.TryGetValue(x,out var n)?n:0;return new AnalyticsDayPoint { Label=x.ToString("dd/MM"),CaptureCount=count,CaptureHeight=count==0?2:count*150d/maximum,ToolTip=x.ToString("dd/MM/yyyy")+": "+count+" lượt chụp" };}).ToList();
            foreach(var name in new[]{nameof(CaptureCountText),nameof(PictureCountText),nameof(ReadyCountText),nameof(PendingCountText),nameof(FailedCountText),nameof(HasData),nameof(DataScopeText),nameof(DailyPoints)})Raise(name);
        }
    }
    public sealed class AnalyticsPeriodOption
    {
        public AnalyticsPeriodOption(string label,int days){Label=label;Days=days;}
        public string Label { get; }
        public int Days { get; }
        public override string ToString()=>Label;
    }
    public sealed class AnalyticsDayPoint
    {
        public string Label { get; set; }
        public int CaptureCount { get; set; }
        public double CaptureHeight { get; set; }
        public string ToolTip { get; set; }
    }
}
