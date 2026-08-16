using System.IO;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IFrameAnalyzer
    {
        Frame Analyze(string pngPath, FrameAnalysisOptions options);
        Frame Analyze(Stream pngStream, string sourceName, FrameAnalysisOptions options);
    }
}
