using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    public sealed class RevenueSyncService : IRevenueSyncService
    {
        readonly IRevenueReportRepository reports;
        readonly IRemoteMediaDeliveryClient remote;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        public RevenueSyncService(IRevenueReportRepository reports,IRemoteMediaDeliveryClient remote){this.reports=reports;this.remote=remote;}

        public async Task SynchronizeAsync(CancellationToken token)
        {
            if(!await gate.WaitAsync(0,token).ConfigureAwait(false))return;
            try
            {
                foreach(var report in await reports.GetPendingAsync(25,token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var result=await remote.ReportRevenueAsync(report,"revenue:"+report.BoothSessionId.ToString("N"),token).ConfigureAwait(false);
                        if(result.Succeeded)await reports.MarkSyncedAsync(report.BoothSessionId,token).ConfigureAwait(false);
                        else await reports.MarkRetryAsync(report.BoothSessionId,result.ErrorCode??"remote_error",token).ConfigureAwait(false);
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                    catch(Exception error){await reports.MarkRetryAsync(report.BoothSessionId,error.GetType().Name,token).ConfigureAwait(false);}
                }
            }
            finally{gate.Release();}
        }
    }
}
