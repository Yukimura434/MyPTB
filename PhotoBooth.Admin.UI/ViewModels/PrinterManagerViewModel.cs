using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
 public sealed class PrinterManagerViewModel:PageViewModel
 {
  readonly IPrinterService service;readonly ISettingsService settings;readonly ILogger<PrinterManagerViewModel> log;
  DiscoveredPrinter selectedPrinter;PrinterProfile editing;string status="Chưa kết nối",message;
  public PrinterManagerViewModel(IPrinterService service,ISettingsService settings,ILogger<PrinterManagerViewModel> log)
  {
   this.service=service;this.settings=settings;this.log=log;
   ScanCommand=new AsyncCommand(_=>Scan());ConnectCommand=new AsyncCommand(_=>Connect());
   // These commands validate inside Execute. Their previous CanExecute predicate was never refreshed after connection.
   SaveCommand=new AsyncCommand(_=>Save(false));SaveAndTestCommand=new AsyncCommand(_=>Save(true));
   PaperSizes=new ObservableCollection<string>();PaperTypes=new ObservableCollection<string>();Qualities=new ObservableCollection<string>();Copies=new[]{1,2,3,4};_=Scan();
  }
  public override string Title=>"Printer Manager";
  public ObservableCollection<DiscoveredPrinter> Printers{get;}=new ObservableCollection<DiscoveredPrinter>();
  public ObservableCollection<string> PaperSizes{get;}public ObservableCollection<string> PaperTypes{get;}public ObservableCollection<string> Qualities{get;}public int[] Copies{get;}
  public DiscoveredPrinter SelectedPrinter{get=>selectedPrinter;set=>Set(ref selectedPrinter,value);}public PrinterProfile EditingProfile{get=>editing;private set=>Set(ref editing,value);}public string PrinterStatus{get=>status;private set=>Set(ref status,value);}public string Message{get=>message;private set=>Set(ref message,value);}
  public ICommand ScanCommand{get;}public ICommand ConnectCommand{get;}public ICommand SaveCommand{get;}public ICommand SaveAndTestCommand{get;}
  async Task Scan(){try{Message=null;PrinterStatus="Đang quét qua Windows…";Printers.Clear();foreach(var x in await service.ScanAsync(CancellationToken.None))Printers.Add(x);SelectedPrinter=Printers.FirstOrDefault();PrinterStatus=Printers.Count==0?"Không tìm thấy máy in đang hoạt động":Printers.Count+" máy in khả dụng";}catch(Exception e){Fail(e,"Không thể quét máy in");}}
  async Task Connect()
  {
   if(SelectedPrinter==null){Message="Hãy chọn một máy in trước khi kết nối.";return;}
   try
   {
    PrinterStatus="Đang kết nối…";var device=await service.ConnectAsync(SelectedPrinter.Id,CancellationToken.None);
    var saved=(await service.GetProfilesAsync(CancellationToken.None)).FirstOrDefault(x=>string.Equals(x.PrinterId,device.Id,StringComparison.OrdinalIgnoreCase));
    EditingProfile=saved??new PrinterProfile{Id=Guid.NewGuid(),Name=device.Name,PrinterName=device.Name,PrinterId=device.Id,DefaultCopies=1,UseDefaultBorder=false,PrintInColor=device.SupportsColor};
    EditingProfile.Name=device.Name;EditingProfile.PrinterName=device.Name;EditingProfile.PrinterId=device.Id;
    Fill(PaperSizes,device.PaperSizes);Fill(PaperTypes,device.PaperSources);Fill(Qualities,device.Resolutions);
    EditingProfile.PaperSize=ChoosePaper(EditingProfile.PaperSize,PaperSizes);EditingProfile.PaperType=Choose(EditingProfile.PaperType,PaperTypes);EditingProfile.Quality=Choose(EditingProfile.Quality,Qualities);Raise(nameof(EditingProfile));
    PrinterStatus="Đã kết nối qua "+device.ConnectionType;Message=saved==null?"Máy in mới: hãy kiểm tra và lưu profile.":"Đã tải profile từng lưu của máy in.";
   }catch(Exception e){Fail(e,"Kết nối máy in thất bại");}
  }
  async Task Save(bool test)
  {
   if(EditingProfile==null){Message="Hãy kết nối máy in trước khi lưu.";return;}
   try
   {
    Message="Đang lưu profile…";await service.SaveProfileAsync(EditingProfile,CancellationToken.None);
    if(EditingProfile.IsDefault){var app=await settings.GetAsync(CancellationToken.None)??new Settings();app.DefaultPrinterProfileId=EditingProfile.Id;await settings.SaveAsync(app,CancellationToken.None);}
    var verified=(await service.GetProfilesAsync(CancellationToken.None)).FirstOrDefault(x=>string.Equals(x.PrinterId,EditingProfile.PrinterId,StringComparison.OrdinalIgnoreCase));
    if(verified==null)throw new InvalidOperationException("Profile was not found after saving.");EditingProfile=verified;
    if(test){Message="Đang gửi trang in thử…";await service.PrintAsync(CreateTestJob(verified),CancellationToken.None);}
    PrinterStatus="Đã lưu";Message=test?"Đã lưu profile và gửi trang in thử PhotoBooth.":"Đã lưu profile máy in. Lần kết nối sau sẽ nhận diện bằng Printer ID.";
   }catch(Exception e){Fail(e,test?"Lưu hoặc in thử thất bại: "+e.Message:"Lưu profile thất bại: "+e.Message);}
  }
  static PrintJob CreateTestJob(PrinterProfile p)=>new PrintJob{Id=Guid.NewGuid(),PrinterName=p.PrinterName,Copies=1,PaperSize=p.PaperSize,PaperType=p.PaperType,Quality=p.Quality,Landscape=p.Landscape,UseDefaultBorder=p.UseDefaultBorder,PrintInColor=p.PrintInColor};
  static void Fill(ObservableCollection<string> target,string[] values){target.Clear();foreach(var x in values??new string[0])target.Add(x);}static string Choose(string value,ObservableCollection<string> values)=>values.Contains(value)?value:values.FirstOrDefault();
  static string ChoosePaper(string saved,ObservableCollection<string> values)=>values.FirstOrDefault(x=>PrinterPaperNames.Match(x,saved))??saved;
  void Fail(Exception e,string text){log.LogError(e,text);PrinterStatus="Lỗi";Message=text;}
 }
}
