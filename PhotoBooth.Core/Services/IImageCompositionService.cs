using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services { public interface IImageCompositionService { Task<string> ComposeAsync(Session session,Frame frame,Preset preset,bool final,CancellationToken token); } }
