using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
namespace PhotoBooth.Customer.UI.Controls
{
 public static class AsyncPhoto
 {
  public static readonly DependencyProperty PathProperty=DependencyProperty.RegisterAttached("Path",typeof(string),typeof(AsyncPhoto),new PropertyMetadata(null,Changed));
  public static readonly DependencyProperty WidthProperty=DependencyProperty.RegisterAttached("Width",typeof(int),typeof(AsyncPhoto),new PropertyMetadata(1280,Changed));
  static readonly DependencyProperty VersionProperty=DependencyProperty.RegisterAttached("Version",typeof(int),typeof(AsyncPhoto),new PropertyMetadata(0));
  static readonly SemaphoreSlim Decoders=new SemaphoreSlim(2);
  static readonly object Sync=new object();
  static readonly Dictionary<string,BitmapSource> Cache=new Dictionary<string,BitmapSource>();
  static readonly Queue<string> Order=new Queue<string>();
  static long cacheBytes; const long MaxBytes=32*1024*1024;
  public static string GetPath(DependencyObject item)=>(string)item.GetValue(PathProperty);
  public static void SetPath(DependencyObject item,string value)=>item.SetValue(PathProperty,value);
  public static int GetWidth(DependencyObject item)=>(int)item.GetValue(WidthProperty);
  public static void SetWidth(DependencyObject item,int value)=>item.SetValue(WidthProperty,value);
  static async void Changed(DependencyObject target,DependencyPropertyChangedEventArgs args)
  {
   var image=target as Image;if(image==null)return;
   var version=(int)image.GetValue(VersionProperty)+1;image.SetValue(VersionProperty,version);image.Source=null;
   var path=GetPath(image);var width=Math.Max(1,Math.Min(2048,GetWidth(image)));if(string.IsNullOrWhiteSpace(path))return;
   var result=await LoadAsync(path,width);
   if (!image.Dispatcher.HasShutdownStarted) await image.Dispatcher.InvokeAsync(() => { if((int)image.GetValue(VersionProperty)==version)image.Source=result; });
  }
  internal static async Task<BitmapSource> LoadAsync(string path,int width)
  {
   try { return await Task.Run(async()=>{await Decoders.WaitAsync().ConfigureAwait(false);try{return Load(path,width);}finally{Decoders.Release();}}).ConfigureAwait(false); }
   catch(IOException){}catch(UnauthorizedAccessException){}catch(NotSupportedException){}catch(ArgumentException){}catch(FormatException){}
   return null;
  }
  static BitmapSource Load(string path,int width)
  {
   var info=new FileInfo(path);if(!info.Exists)return null;
   var key=info.FullName+"|"+info.LastWriteTimeUtc.Ticks+"|"+info.Length+"|"+width;
   lock(Sync){if(Cache.TryGetValue(key,out var found))return found;}
   var bitmap=new BitmapImage();
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
   {bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=width;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();}
   var bytes=Size(bitmap);
   lock(Sync){if(!Cache.ContainsKey(key)&&bytes<=MaxBytes){while(Order.Count>0&&(cacheBytes+bytes>MaxBytes||Order.Count>=64)){var oldest=Order.Dequeue();cacheBytes-=Size(Cache[oldest]);Cache.Remove(oldest);}Cache.Add(key,bitmap);Order.Enqueue(key);cacheBytes+=bytes;}}
   return bitmap;
  }
  static long Size(BitmapSource bitmap)=>(long)bitmap.PixelWidth*bitmap.PixelHeight*Math.Max(4,(bitmap.Format.BitsPerPixel+7)/8);
 }
}

