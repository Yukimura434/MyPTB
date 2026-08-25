using System;
using System.IO;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class ColorLutPathResolver : IColorLutPathResolver
    {
        readonly string root;
        public ColorLutPathResolver(ApplicationOptions options)
        {
            root=EnsureSeparator(Path.GetFullPath(options.DataDirectory));
            CubeDirectory=Path.Combine(root,"Assets","Presets","Cubes");
            StagingDirectory=Path.Combine(root,"Temp","LutImports");
            Directory.CreateDirectory(CubeDirectory);Directory.CreateDirectory(StagingDirectory);
        }
        public string CubeDirectory{get;}
        public string StagingDirectory{get;}
        public string GetFullPath(string relativePath)
        {
            if(string.IsNullOrWhiteSpace(relativePath)||Path.IsPathRooted(relativePath))throw new InvalidDataException("LUT path must be relative to the PhotoBooth data directory.");
            var full=Path.GetFullPath(Path.Combine(root,relativePath.Replace('/',Path.DirectorySeparatorChar)));
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("LUT path escapes the PhotoBooth data directory.");
            return full;
        }
        public string CreateRelativeAssetPath(Guid assetId,string sha256)
        {
            if(string.IsNullOrWhiteSpace(sha256)||sha256.Length!=64)throw new ArgumentException("A SHA-256 hash is required.",nameof(sha256));
            return ("Assets/Presets/Cubes/"+assetId.ToString("N")+"-"+sha256.Substring(0,12)+".cube");
        }
        static string EnsureSeparator(string path)=>path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
    }
}
