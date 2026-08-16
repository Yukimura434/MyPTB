using System;using System.Collections.Concurrent;using System.IO;using Microsoft.Extensions.Logging;
namespace PhotoBooth.Infrastructure.Logging
{
 public sealed class RotatingFileLoggerProvider:ILoggerProvider
 {
  readonly string directory;readonly long maximumBytes;readonly int retained;readonly ConcurrentDictionary<string,object> gates=new ConcurrentDictionary<string,object>();
  public RotatingFileLoggerProvider(string directory,long maximumBytes=5242880,int retained=7){this.directory=directory;this.maximumBytes=maximumBytes;this.retained=retained;Directory.CreateDirectory(directory);}
  public ILogger CreateLogger(string categoryName)=>new FileLogger(this,categoryName);public void Dispose(){}
  void Write(string category,LogLevel level,EventId id,string message,Exception error){var channel=level>=LogLevel.Error?"Error":category.IndexOf("Camera",StringComparison.OrdinalIgnoreCase)>=0?"Camera":category.IndexOf("Printer",StringComparison.OrdinalIgnoreCase)>=0||category.IndexOf("PrintQueue",StringComparison.OrdinalIgnoreCase)>=0?"Printer":category.IndexOf("Session",StringComparison.OrdinalIgnoreCase)>=0||category.IndexOf("Capture",StringComparison.OrdinalIgnoreCase)>=0?"Session":"Application";var path=Path.Combine(directory,channel+".log");lock(gates.GetOrAdd(path,_=>new object())){Rotate(path);File.AppendAllText(path,$"{DateTime.UtcNow:O} [{level}] {category} {id}: {message}{(error==null?"":Environment.NewLine+error)}{Environment.NewLine}");}}
  void Rotate(string path){if(!File.Exists(path)||new FileInfo(path).Length<maximumBytes)return;for(var i=retained-1;i>=1;i--){var from=path+"."+i;var to=path+"."+(i+1);if(File.Exists(to))File.Delete(to);if(File.Exists(from))File.Move(from,to);}File.Move(path,path+".1");}
  sealed class FileLogger:ILogger{readonly RotatingFileLoggerProvider owner;readonly string category;public FileLogger(RotatingFileLoggerProvider o,string c){owner=o;category=c;}public IDisposable BeginScope<TState>(TState state)=>NullScope.Instance;public bool IsEnabled(LogLevel level)=>level!=LogLevel.None;public void Log<TState>(LogLevel level,EventId id,TState state,Exception error,Func<TState,Exception,string> formatter){if(IsEnabled(level))owner.Write(category,level,id,formatter(state,error),error);}sealed class NullScope:IDisposable{public static readonly NullScope Instance=new NullScope();public void Dispose(){}}}
 }
}
