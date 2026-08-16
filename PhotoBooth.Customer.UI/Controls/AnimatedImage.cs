using System;using System.Drawing.Imaging;using System.IO;using System.Windows;using System.Windows.Media.Imaging;using System.Windows.Threading;
namespace PhotoBooth.Customer.UI.Controls
{
 public sealed class AnimatedImage:System.Windows.Controls.Image
 {
  public static readonly DependencyProperty SourcePathProperty=DependencyProperty.Register(nameof(SourcePath),typeof(string),typeof(AnimatedImage),new PropertyMetadata(null,(d,e)=>((AnimatedImage)d).LoadPath(e.NewValue as string)));
  readonly DispatcherTimer timer=new DispatcherTimer();System.Drawing.Image animation;MemoryStream animationStream;FrameDimension dimension;int frame,frameCount;int[] delays;
  public AnimatedImage(){Stretch=System.Windows.Media.Stretch.UniformToFill;timer.Tick+=(s,e)=>Next();Loaded+=(s,e)=>{if(frameCount>1)timer.Start();};Unloaded+=(s,e)=>timer.Stop();}
  public string SourcePath{get=>(string)GetValue(SourcePathProperty);set=>SetValue(SourcePathProperty,value);}
  void LoadPath(string path){timer.Stop();animation?.Dispose();animationStream?.Dispose();animation=null;animationStream=null;Source=null;frame=0;frameCount=0;if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return;try{if(!string.Equals(Path.GetExtension(path),".gif",StringComparison.OrdinalIgnoreCase)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(path,UriKind.Absolute);bitmap.EndInit();bitmap.Freeze();Source=bitmap;return;}animationStream=new MemoryStream(File.ReadAllBytes(path));animation=System.Drawing.Image.FromStream(animationStream);dimension=new FrameDimension(animation.FrameDimensionsList[0]);frameCount=animation.GetFrameCount(dimension);delays=ReadDelays(animation,frameCount);Show();if(frameCount>1){timer.Interval=TimeSpan.FromMilliseconds(delays[0]);if(IsLoaded)timer.Start();}}catch{animation?.Dispose();animationStream?.Dispose();animation=null;animationStream=null;}}
  void Next(){if(animation==null||frameCount<2)return;frame=(frame+1)%frameCount;Show();timer.Interval=TimeSpan.FromMilliseconds(delays[frame]);}
  void Show(){animation.SelectActiveFrame(dimension,frame);using(var stream=new MemoryStream()){animation.Save(stream,ImageFormat.Png);stream.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();Source=bitmap;}}
  static int[] ReadDelays(System.Drawing.Image image,int count){var values=new int[count];for(var i=0;i<count;i++)values[i]=100;try{var bytes=image.GetPropertyItem(0x5100).Value;for(var i=0;i<count&&i*4+3<bytes.Length;i++)values[i]=Math.Max(20,BitConverter.ToInt32(bytes,i*4)*10);}catch{}return values;}
 }
}
