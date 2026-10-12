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
  readonly ISessionRepository sessions;readonly IDeliverableRepository deliverables;readonly IPrinterProfileRepository profiles;readonly IPrinterService printer;readonly IPrintJobRepository printJobs;readonly IDurableOutputJobRepository durableJobs;readonly IEventPrintAuthorizationService eventPrinting;
  public PrintPipeline(ISessionRepository sessions,IDeliverableRepository deliverables,IPrinterProfileRepository profiles,IPrinterService printer,IPrintQueueService queue,IPrintJobRepository printJobs,IDurableOutputJobRepository durableOutputJobs=null,IEventPrintAuthorizationService eventPrintAuthorization=null){this.sessions=sessions;this.deliverables=deliverables;this.profiles=profiles;this.printer=printer;this.printJobs=printJobs;durableJobs=durableOutputJobs;eventPrinting=eventPrintAuthorization;}
  public async Task ExecuteAsync(Guid sessionId,Guid profileId,int copies,CancellationToken token)
  {
   var session=await sessions.GetAsync(sessionId,token);var profile=await profiles.GetAsync(profileId,token);
   if(session==null||!session.IsBoothSession)throw new InvalidOperationException("Printing requires a booth session.");
   var file=session.FinalImagePath;
   if(string.IsNullOrWhiteSpace(file)||string.IsNullOrWhiteSpace(session.FinalImageId)||profile==null)throw new InvalidOperationException("Final booth asset or printer profile is missing.");
   if(durableJobs==null)throw new InvalidOperationException("Durable print persistence is not configured.");
   if(!await printer.IsConnectedAsync(profile.PrinterId,token))throw new InvalidOperationException("The selected printer is offline or a different printer is connected.");
    // The print provider reads the immutable final asset directly and creates no extra copy.
    copies=Math.Max(1,copies);
    var deliverable=(await deliverables.GetByBoothSessionAsync(session.Id,token)).OrderByDescending(x=>x.CreatedAtUtc).FirstOrDefault();
    if(deliverable==null)throw new InvalidOperationException("A ready deliverable is required before printing.");
    DurableOutputJobRecord durable=null;
    EventPrintReservation reservation=null;
    if(session.IsBoothSession)
    {
     durable=await durableJobs.CreateIntentAsync(new DurableOutputJobRecord{Id=Guid.NewGuid().ToString("N"),SessionId=session.Id,DeliverableId=deliverable.Id,AssetId=session.FinalImageId,JobType="Print",IdempotencyKey="print:"+session.Id.ToString("N")+":"+deliverable.Id+":"+session.FinalImageId+":"+profile.Id.ToString("N")+":"+copies,State=DurableOutputJobStates.Pending,CreatedAtUtc=DateTime.UtcNow},token);
     if(durable.State==DurableOutputJobStates.Completed)return;
     if(durable.State==DurableOutputJobStates.Submitting||durable.State==DurableOutputJobStates.Submitted||durable.State==DurableOutputJobStates.UnknownOutcome)throw new InvalidOperationException("The previous print outcome requires operator confirmation before reprinting.");
     if(eventPrinting!=null || !string.IsNullOrWhiteSpace(session.RemoteEventId))
     {
      if(eventPrinting==null)throw new InvalidOperationException("Chưa thể kiểm tra giới hạn in của sự kiện.");
      reservation=await eventPrinting.ReserveAsync(sessionId,copies,durable.IdempotencyKey,token);
      if(reservation?.State=="committed"){await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.Completed,null,token);return;}
      if(reservation!=null && reservation.State!="reserved")throw new InvalidOperationException("Lượt in trước đang chờ xác minh. Không gửi in lại.");
     }
     await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.Submitting,null,token);
    }
    // Frame artwork owns the margins; new print submissions always request full-sheet output.
    var audit=new PrintJobRecord{Id=Guid.NewGuid(),OutputJobId=durable?.Id,SessionId=session.Id,DeliverableId=deliverable?.Id,PrinterProfileId=profile.Id,PrinterName=profile.PrinterName,Copies=copies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,MediaType=profile.MediaType,Quality=profile.Quality,Landscape=profile.Landscape,PrintInColor=profile.PrintInColor,UseDefaultBorder=false,Status="Pending",PrintedAtUtc=DateTime.UtcNow};
    await printJobs.AddAsync(audit,token);
    try
    {
     if(reservation!=null)await eventPrinting.SubmitAsync(reservation,token);
     await printer.PrintAsync(new PrintJob{Id=Guid.NewGuid(),FilePath=file,PrinterName=profile.PrinterName,Copies=copies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,MediaType=profile.MediaType,Quality=profile.Quality,Landscape=profile.Landscape,UseDefaultBorder=false,PrintInColor=profile.PrintInColor},token);
     if(reservation!=null)await eventPrinting.CommitAsync(reservation,CancellationToken.None);
     if(durable!=null)await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.Completed,null,CancellationToken.None);
     audit.Status="Success";audit.PrintedAtUtc=DateTime.UtcNow;await printJobs.AddAsync(audit,CancellationToken.None);
    }
    catch(Exception error)
    {
     if(durable!=null)try{await durableJobs.SetStateAsync(durable.Id,DurableOutputJobStates.UnknownOutcome,error.Message,CancellationToken.None);}catch{}
     audit.Status="UnknownOutcome";audit.PrintedAtUtc=DateTime.UtcNow;try{await printJobs.AddAsync(audit,CancellationToken.None);}catch{}
     throw;
    }
   }
 }
}
