using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Pipelines;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Pipelines
{
 public sealed class PrintPipeline:IPrintPipeline
 {
  readonly ISessionRepository sessions;readonly IDeliverableRepository deliverables;readonly IPrinterProfileRepository profiles;readonly IPrinterService printer;readonly IPrintJobRepository printJobs;readonly IDurableOutputJobRepository durableJobs;
  public PrintPipeline(ISessionRepository sessions,IDeliverableRepository deliverables,IPrinterProfileRepository profiles,IPrinterService printer,IPrintQueueService queue,IPrintJobRepository printJobs,IDurableOutputJobRepository durableOutputJobs=null){this.sessions=sessions;this.deliverables=deliverables;this.profiles=profiles;this.printer=printer;this.printJobs=printJobs;durableJobs=durableOutputJobs;}
  public async Task ExecuteAsync(Guid sessionId,Guid profileId,int copies,CancellationToken token)
  {
   var session=await sessions.GetAsync(sessionId,token);var profile=await profiles.GetAsync(profileId,token);var file=session?.FinalImagePath??session?.CapturedFiles?.LastOrDefault();
   if(file==null||profile==null)throw new InvalidOperationException("Session output or printer profile is missing.");
   var currentDefault=await profiles.GetDefaultAsync(token);
   if(currentDefault==null||currentDefault.Id!=profile.Id)throw new InvalidOperationException("Default printer changed. Reconnect the printer.");
   if(!await printer.IsConnectedAsync(profile.PrinterId,token))throw new InvalidOperationException("Default printer is offline or a different printer is connected.");
    // The print provider reads the immutable final asset directly and creates no extra copy.
    copies=Math.Max(1,copies);
    DurableOutputJobRecord durable=null;
    if(session.IsBoothSession&&durableJobs!=null&&!string.IsNullOrWhiteSpace(session.FinalImageId))
    {
     durable=await durableJobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=session.Id,AssetId=session.FinalImageId,JobType="Print",IdempotencyKey="print:"+session.Id.ToString("N")+":"+session.FinalImageId+":"+profile.Id.ToString("N")+":"+copies,State=DurableOutputJobStates.Pending,CreatedAtUtc=DateTime.UtcNow},token);
     if(durable.State==DurableOutputJobStates.Completed)return;
     if(durable.State==DurableOutputJobStates.Submitting||durable.State==DurableOutputJobStates.Submitted||durable.State==DurableOutputJobStates.UnknownOutcome)throw new InvalidOperationException("The previous print outcome requires operator confirmation before reprinting.");
     await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.Submitting,null,token);
    }
    try{await printer.PrintAsync(new PrintJob{Id=Guid.NewGuid(),FilePath=file,PrinterName=profile.PrinterName,Copies=copies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,Quality=profile.Quality,Landscape=profile.Landscape,UseDefaultBorder=profile.UseDefaultBorder,PrintInColor=profile.PrintInColor},token);if(durable!=null)await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.Completed,null,CancellationToken.None);}catch(Exception error){if(durable!=null)try{await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.UnknownOutcome,error.Message,CancellationToken.None);}catch{}throw;}
    var deliverable=(await deliverables.GetByBoothSessionAsync(session.Id,token)).OrderByDescending(x=>x.CreatedAtUtc).FirstOrDefault();
    await printJobs.AddAsync(new PrintJobRecord{Id=Guid.NewGuid(),SessionId=session.Id,DeliverableId=deliverable?.Id,PrinterProfileId=profile.Id,PrinterName=profile.PrinterName,Copies=copies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,Quality=profile.Quality,Landscape=profile.Landscape,PrintInColor=profile.PrintInColor,UseDefaultBorder=profile.UseDefaultBorder,Status="Success",PrintedAtUtc=DateTime.UtcNow},token);
   }
 }
}
