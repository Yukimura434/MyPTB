using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
 public sealed class PrinterConnectionViewModel:ObservableObject
 {
  readonly IPrinterService printers;readonly ISettingsService settings;readonly CustomerWorkflowContext context;DiscoveredPrinter selected;PrinterProfile profile;string message="Bạn có muốn sử dụng máy in cho phiên này?";bool choosing;
  public PrinterConnectionViewModel(IPrinterService printers,ISettingsService settings,CustomerWorkflowContext context){this.printers=printers;this.settings=settings;this.context=context;ConnectDefaultCommand=new AsyncCommand(ConnectDefault);SkipCommand=new RelayCommand(Skip);ScanCommand=new AsyncCommand(Scan);ConnectCommand=new AsyncCommand(ConnectSelected);SaveAndContinueCommand=new AsyncCommand(SaveAndContinue);PaperSizes=new ObservableCollection<string>();PaperTypes=new ObservableCollection<string>();Qualities=new ObservableCollection<string>();Copies=new[]{1,2,3,4};}
  public event EventHandler Completed;public ObservableCollection<DiscoveredPrinter> Printers{get;}=new ObservableCollection<DiscoveredPrinter>();public ObservableCollection<string> PaperSizes{get;}public ObservableCollection<string> PaperTypes{get;}public ObservableCollection<string> Qualities{get;}public int[] Copies{get;}
  public DiscoveredPrinter SelectedPrinter{get=>selected;set=>Set(ref selected,value);}public PrinterProfile Profile{get=>profile;private set=>Set(ref profile,value);}public string Message{get=>message;private set=>Set(ref message,value);}public bool IsChoosing{get=>choosing;private set=>Set(ref choosing,value);}public bool IsIntro=>!IsChoosing;
  public ICommand ConnectDefaultCommand{get;}public ICommand SkipCommand{get;}public ICommand ScanCommand{get;}public ICommand ConnectCommand{get;}public ICommand SaveAndContinueCommand{get;}
  public async Task RequireConnectionAsync(string reason){Message=reason;IsChoosing=true;Raise(nameof(IsIntro));await Scan();}
  async Task ConnectDefault(){try{var all=await printers.GetProfilesAsync(CancellationToken.None);var saved=all.SingleOrDefault(x=>x.IsDefault);if(saved!=null&&await printers.IsConnectedAsync(saved.PrinterId,CancellationToken.None)){context.PrintingEnabled=true;context.ConnectedPrinterId=saved.PrinterId;Message="Đã kết nối máy in mặc định: "+saved.PrinterName;Completed?.Invoke(this,EventArgs.Empty);return;}await RequireConnectionAsync(saved==null?"Chưa cấu hình máy in mặc định. Hãy chọn máy in.":"Máy in mặc định không khả dụng. Hãy kết nối lại.");}catch(Exception e){await RequireConnectionAsync("Không thể kết nối máy in mặc định: "+e.Message);}}
  void Skip(){context.PrintingEnabled=false;context.ConnectedPrinterId=null;Completed?.Invoke(this,EventArgs.Empty);}
  async Task Scan(){try{IsChoosing=true;Raise(nameof(IsIntro));Printers.Clear();foreach(var x in await printers.ScanAsync(CancellationToken.None))Printers.Add(x);SelectedPrinter=Printers.FirstOrDefault();Message=Printers.Count==0?"Không tìm thấy máy in đang hoạt động.":"Chỉ hiển thị máy in Windows đang quét được.";}catch(Exception e){Message="Quét máy in thất bại: "+e.Message;}}
  async Task ConnectSelected(){if(SelectedPrinter==null){Message="Hãy chọn một máy in trước khi kết nối.";return;}try{var device=await printers.ConnectAsync(SelectedPrinter.Id,CancellationToken.None);var saved=(await printers.GetProfilesAsync(CancellationToken.None)).FirstOrDefault(x=>string.Equals(x.PrinterId,device.Id,StringComparison.OrdinalIgnoreCase));Profile=saved??new PrinterProfile{Id=Guid.NewGuid(),Name=device.Name,PrinterName=device.Name,PrinterId=device.Id,DefaultCopies=1,UseDefaultBorder=false,PrintInColor=device.SupportsColor};Profile.Name=device.Name;Profile.PrinterName=device.Name;Profile.PrinterId=device.Id;Fill(PaperSizes,device.PaperSizes);Fill(PaperTypes,device.PaperSources);Fill(Qualities,device.Resolutions);Profile.PaperSize=Pick(Profile.PaperSize,PaperSizes);Profile.PaperType=Pick(Profile.PaperType,PaperTypes);Profile.Quality=Pick(Profile.Quality,Qualities);Raise(nameof(Profile));Message=saved==null?"Đã kết nối. Hãy kiểm tra thông số rồi lưu profile.":"Đã kết nối và tải profile đã lưu.";}catch(Exception e){Message="Kết nối thất bại: "+e.Message;}}
  async Task SaveAndContinue(){if(Profile==null){Message="Hãy kết nối máy in và kiểm tra thông số trước khi lưu.";return;}try{await printers.SaveProfileAsync(Profile,CancellationToken.None);if(Profile.IsDefault){var app=await settings.GetAsync(CancellationToken.None)??new Settings();app.DefaultPrinterProfileId=Profile.Id;await settings.SaveAsync(app,CancellationToken.None);}context.PrintingEnabled=true;context.ConnectedPrinterId=Profile.PrinterId;Message="Đã lưu profile máy in.";Completed?.Invoke(this,EventArgs.Empty);}catch(Exception e){Message="Không thể lưu profile: "+e.Message;}}
  static void Fill(ObservableCollection<string> target,string[] values){target.Clear();foreach(var x in values??new string[0])target.Add(x);}static string Pick(string value,ObservableCollection<string> values)=>values.Contains(value)?value:values.FirstOrDefault();
 }
}
