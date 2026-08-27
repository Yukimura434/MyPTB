using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
namespace PhotoBooth.Core.Services
{
    public interface IBeautyRetouchService
    {
        Task<BeautyRetouchResult> ProcessAsync(string inputPath, string outputPath, BeautySettings settings, CancellationToken token);
    }
}
