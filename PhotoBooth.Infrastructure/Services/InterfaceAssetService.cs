using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class InterfaceAssetService : IInterfaceAssetService
    {
        readonly IInterfaceAssetRepository repository; readonly string directory;
        public InterfaceAssetService(IInterfaceAssetRepository repo,ApplicationOptions options){repository=repo;directory=Path.Combine(options.DataDirectory,"Assets","Background");Directory.CreateDirectory(directory);}
        public Task<IReadOnlyList<InterfaceAsset>> GetAllAsync(CancellationToken token)=>repository.GetAllAsync(token);
        public Task<InterfaceAsset> GetSelectedAsync(CancellationToken token)=>repository.GetSelectedAsync(token);
        public async Task<InterfaceAsset> ImportAsync(string sourcePath,CancellationToken token){token.ThrowIfCancellationRequested();var extension=Path.GetExtension(sourcePath)?.ToLowerInvariant();if(extension!=".png"&&extension!=".jpg"&&extension!=".jpeg"&&extension!=".gif"&&extension!=".bmp")throw new InvalidOperationException("Only PNG, JPG, BMP and GIF files are supported.");var id=Guid.NewGuid();var target=Path.Combine(directory,id.ToString("N")+extension);using(var input=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))await input.CopyToAsync(output,81920,token);var asset=new InterfaceAsset{Id=id,Name=Path.GetFileName(sourcePath),FilePath=target,IsAnimated=extension==".gif",CreatedAtUtc=DateTime.UtcNow};await repository.AddAsync(asset,token);return asset;}
        public Task SelectAsync(Guid id,CancellationToken token)=>repository.SelectAsync(id,token);
    }
}
