using System; using System.Threading; using System.Threading.Tasks;
namespace PhotoBooth.Core.Pipelines { public interface IPrintPipeline { Task ExecuteAsync(Guid sessionId, Guid printerProfileId, CancellationToken token); } }
