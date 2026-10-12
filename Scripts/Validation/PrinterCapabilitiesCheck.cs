using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.Admin.UI.ViewModels;
using PhotoBooth.Admin.UI.Views;
using Microsoft.Extensions.Logging.Abstractions;

class PrinterCapabilitiesCheck
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
    static Application app; static string output; static bool print;
    [STAThread] static void Main(string[] args)
    {
        output=Path.GetFullPath(args[0]);print=args.Contains("--print");Directory.CreateDirectory(output);
        app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        foreach(var theme in new[]{"DarkTheme","Icons"}) app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/MiuPhotoBooth;component/Themes/"+theme+".xaml",UriKind.Relative)));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Dispatcher.BeginInvoke(new Action(async()=>{try {await Run();Console.WriteLine("PASS printer capability checks");}catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}finally{app.Shutdown();}}));app.Run();
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static async Task Run()
    {
        var db=new SqliteDatabase(Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N")+".db"));db.Initialize();
        var repo=new SqlitePrinterProfileRepository(db);var service=new PrinterService(repo);var none=CancellationToken.None;
        var timer=System.Diagnostics.Stopwatch.StartNew();var devices=await service.ScanAsync(none);
        Check(timer.ElapsedMilliseconds<5000,"cached Windows discovery under 5 seconds: "+timer.ElapsedMilliseconds+"ms / "+devices.Count+" printers");
        var selected=devices.Single(x=>x.Name=="Brother DCP-T220 Printer");
        var device=await service.ConnectAsync(selected.Id,none);
        Check(device.MediaTypes.Contains("Giấy ảnh bóng"),"Brother exposes photographic glossy independently of paper source");
        using(var document=new System.Drawing.Printing.PrintDocument())
        {
            document.PrinterSettings.PrinterName=device.Name;
            using(var server=new System.Printing.LocalPrintServer())using(var queue=server.GetPrintQueue(device.Name))using(var converter=new System.Printing.Interop.PrintTicketConverter(device.Name,1))
            {
                var ticket=queue.UserPrintTicket.Clone();ticket.PageMediaType=System.Printing.PageMediaType.PhotographicGlossy;
                var raw=converter.ConvertPrintTicketToDevMode(ticket,System.Printing.Interop.BaseDevModeType.UserDefault);
                Console.WriteLine("raw x86 ticket="+converter.ConvertDevModeToPrintTicket(raw).PageMediaType+" bytes="+raw.Length+" headers="+BitConverter.ToUInt16(raw,68)+","+BitConverter.ToUInt16(raw,70));
            }
            typeof(PrinterService).Assembly.GetType("PhotoBooth.Business.Services.WindowsPrinterOptions").GetMethod("ApplyMedia").Invoke(null,new object[]{document,"Giấy ảnh bóng"});
            var handle=document.PrinterSettings.GetHdevmode(document.DefaultPageSettings);
            var memory=GlobalLock(handle);
            try {
                int size=System.Runtime.InteropServices.Marshal.ReadInt16(memory,68)+System.Runtime.InteropServices.Marshal.ReadInt16(memory,70);
                Console.WriteLine("drawing headers W="+System.Runtime.InteropServices.Marshal.ReadInt16(memory,68)+","+System.Runtime.InteropServices.Marshal.ReadInt16(memory,70)+" A="+System.Runtime.InteropServices.Marshal.ReadInt16(memory,36)+","+System.Runtime.InteropServices.Marshal.ReadInt16(memory,38));
                var bytes=new byte[size];System.Runtime.InteropServices.Marshal.Copy(memory,bytes,0,size);
                using(var converter=new System.Printing.Interop.PrintTicketConverter(device.Name,System.Printing.Interop.PrintTicketConverter.MaxPrintSchemaVersion))
                {
                    var actual=converter.ConvertDevModeToPrintTicket(bytes).PageMediaType;
                    Console.WriteLine("DEVMODE media="+actual+" size="+size);
                    Check(actual==System.Printing.PageMediaType.PhotographicGlossy,"photo media survives driver DEVMODE conversion");
                }
            } finally {GlobalUnlock(handle);GlobalFree(handle);}
        }
        var profile=new PrinterProfile{Id=Guid.NewGuid(),PrinterId=device.Id,PrinterName=device.Name,Name=device.Name,PaperSize=device.PaperSizes.FirstOrDefault(x=>x.StartsWith("A5"))??device.PaperSizes[0],PaperType=device.PaperSources.FirstOrDefault(),MediaType="Giấy thường",Quality=device.Resolutions.FirstOrDefault(x=>x=="High")??device.Resolutions.FirstOrDefault(),DefaultCopies=1,IsDefault=true,PrintInColor=true};
        await repo.SaveAsync(profile,none);db.Initialize();Check((await repo.GetAllAsync(none)).Single().MediaType==profile.MediaType,"additive SQLite migration and media roundtrip");
        var settings=new SettingsService(new SqliteSettingsRepository(db));var current=await settings.GetAsync(none);current.DefaultPrinterProfileId=profile.Id;await settings.SaveAsync(current,none);
        var vm=new PrinterManagerViewModel(service,settings,NullLogger<PrinterManagerViewModel>.Instance);
        for(int i=0;i<400 && (vm.IsBusy||!vm.HasEditingProfile);i++)await Task.Delay(50);
        Check(vm.HasEditingProfile&&!vm.IsBusy,"actual Manager loads configured printer");
        var view=new PrinterManagerView {DataContext=vm};
        foreach(var dimensions in new[]{new[]{1366,900},new[]{900,1600}})
        {
            view.Width=dimensions[0];view.Height=dimensions[1];view.Measure(new Size(view.Width,view.Height));view.Arrange(new Rect(0,0,view.Width,view.Height));view.UpdateLayout();
            var image=new RenderTargetBitmap(dimensions[0],dimensions[1],96,96,PixelFormats.Pbgra32);image.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,"printer-"+dimensions[0]+".png")))encoder.Save(file);
        }
        if(print)
        {
            await service.PrintAsync(new PrintJob{Id=Guid.NewGuid(),PrinterName=profile.PrinterName,Copies=1,PaperSize=profile.PaperSize,PaperType=profile.PaperType,MediaType=profile.MediaType,Quality=profile.Quality,PrintInColor=true,UseDefaultBorder=false},none);
            Console.WriteLine("PRINT submitted one synthetic test through production PrinterService: "+profile.PaperSize+" / "+profile.MediaType+" / "+profile.Quality);
        }
    }
}
