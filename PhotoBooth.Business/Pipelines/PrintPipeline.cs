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
  readonly ISessionRepository sessions;readonly IPrinterProfileRepository profiles;readonly IPrinterService printer;readonly IPrintJobRepository printJobs;
  public PrintPipeline(ISessionRepository sessions,IPrinterProfileRepository profiles,IPrinterService printer,IPrintQueueService queue,IPrintJobRepository printJobs){this.sessions=sessions;this.profiles=profiles;this.printer=printer;this.printJobs=printJobs;}
  public async Task ExecuteAsync(Guid sessionId,Guid profileId,CancellationToken token)
  {
   var session=await sessions.GetAsync(sessionId,token);var profile=await profiles.GetAsync(profileId,token);var file=session?.FinalImagePath??session?.CapturedFiles?.LastOrDefault();
   if(file==null||profile==null)throw new InvalidOperationException("Session output or printer profile is missing.");
   var currentDefault=await profiles.GetDefaultAsync(token);
   if(currentDefault==null||currentDefault.Id!=profile.Id)throw new InvalidOperationException("Default printer changed. Reconnect the printer.");
   if(!await printer.IsConnectedAsync(profile.PrinterId,token))throw new InvalidOperationException("Default printer is offline or a different printer is connected.");
// PrintDocument reads the canonical frm directly and creates no PNG copy.
    await printer.PrintAsync(new PrintJob{Id=Guid.NewGuid(),FilePath=file,PrinterName=profile.PrinterName,Copies=profile.DefaultCopies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,Quality=profile.Quality,Landscape=profile.Landscape,UseDefaultBorder=profile.UseDefaultBorder,PrintInColor=profile.PrintInColor},token);
    await printJobs.AddAsync(new PrintJobRecord{Id=Guid.NewGuid(),SessionId=session.Id,PrinterProfileId=profile.Id,PrinterName=profile.PrinterName,Copies=profile.DefaultCopies,PaperSize=profile.PaperSize,PaperType=profile.PaperType,Quality=profile.Quality,Landscape=profile.Landscape,PrintInColor=profile.PrintInColor,UseDefaultBorder=profile.UseDefaultBorder,Status="Success",PrintedAtUtc=DateTime.UtcNow},token);
   }
 }
}
