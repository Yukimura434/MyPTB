using System; using System.Threading; using System.Threading.Tasks; using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Pipelines { public interface ICapturePipeline { Task<Session> ExecuteAsync(Guid sessionId, string cameraId, CancellationToken token); Task<Session> ExecuteAsync(Guid sessionId, string cameraId, string workingDirectory, CancellationToken token); } }
