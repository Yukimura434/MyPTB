using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class ColorLutService : IColorLutService
    {
        readonly IColorLutAssetRepository assets;readonly IPresetColorRepository colors;readonly IPresetRepository presets;readonly IColorLutParser parser;readonly IColorLutPathResolver paths;readonly ILogger<ColorLutService> log;
        public ColorLutService(IColorLutAssetRepository assets,IPresetColorRepository colors,IPresetRepository presets,IColorLutParser parser,IColorLutPathResolver paths,ILogger<ColorLutService> log){this.assets=assets;this.colors=colors;this.presets=presets;this.parser=parser;this.paths=paths;this.log=log;}
        public Task<IReadOnlyList<ColorLutAsset>> GetAllAsync(CancellationToken token)=>assets.GetAllAsync(token);
        public async Task<ColorLutData> GetLiveAsync(Guid presetId,CancellationToken token)
        {
            var setting=await colors.GetAsync(presetId,token).ConfigureAwait(false);
            if(setting==null||!setting.Enabled||!setting.LutAssetId.HasValue)return null;
            var asset=await assets.GetAsync(setting.LutAssetId.Value,token).ConfigureAwait(false);
            if(asset==null||asset.Status!=ColorLutAssetStatus.Ready||!asset.SupportsLiveView)return null;
            var full=paths.GetFullPath(asset.RelativePath);
            var data=await Task.Run(()=>parser.Parse(full,token),token).ConfigureAwait(false);
            if(data.Metadata.DomainMinR!=0||data.Metadata.DomainMinG!=0||data.Metadata.DomainMinB!=0||data.Metadata.DomainMaxR!=1||data.Metadata.DomainMaxG!=1||data.Metadata.DomainMaxB!=1){data.Dispose();return null;}
            data.Strength=Math.Max(0,Math.Min(1,setting.Strength));
            return data;
        }

        public async Task ApplyCaptureAsync(Guid presetId,string imagePath,CancellationToken token)
        {
            if(string.IsNullOrWhiteSpace(imagePath))throw new ArgumentException("Capture image path is required.",nameof(imagePath));
            var setting=await colors.GetAsync(presetId,token).ConfigureAwait(false);
            if(setting==null||!setting.Enabled||!setting.LutAssetId.HasValue||setting.Strength<=0)return;
            var asset=await assets.GetAsync(setting.LutAssetId.Value,token).ConfigureAwait(false);
            if(asset==null||asset.Status!=ColorLutAssetStatus.Ready)return;
            using(var data=await Task.Run(()=>parser.Parse(paths.GetFullPath(asset.RelativePath),token),token).ConfigureAwait(false))
            {
                data.Strength=Math.Max(0,Math.Min(1,setting.Strength));
                await Task.Run(()=>ApplyTetrahedral(imagePath,data,token),token).ConfigureAwait(false);
            }
        }

        static void ApplyTetrahedral(string imagePath,ColorLutData lut,CancellationToken token)
        {
            if(lut?.Metadata==null||lut.Values==null)throw new InvalidDataException("LUT data is unavailable.");
            var temporary=imagePath+"."+Guid.NewGuid().ToString("N")+".lut.jpg";
            try
            {
                using(var source=Image.FromFile(imagePath))
                using(var bitmap=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb))
                {
                    bitmap.SetResolution(source.HorizontalResolution>0?source.HorizontalResolution:96,source.VerticalResolution>0?source.VerticalResolution:96);
                    using(var graphics=Graphics.FromImage(bitmap))graphics.DrawImageUnscaled(source,0,0);
                    foreach(var property in source.PropertyItems)try{bitmap.SetPropertyItem(property);}catch(ArgumentException){}
                    var rect=new Rectangle(0,0,bitmap.Width,bitmap.Height);var bits=bitmap.LockBits(rect,ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
                    try
                    {
                        unsafe
                        {
                            var row=(byte*)bits.Scan0;
                            for(var y=0;y<bitmap.Height;y++,row+=bits.Stride)
                            {
                                token.ThrowIfCancellationRequested();
                                for(var x=0;x<bitmap.Width;x++)
                                {
                                    var pixel=row+x*4;float rr,gg,bb;var r=pixel[2]/255f;var g=pixel[1]/255f;var b=pixel[0]/255f;
                                    Sample(lut,r,g,b,out rr,out gg,out bb);var strength=lut.Strength;
                                    pixel[2]=ToByte(r+(rr-r)*strength);pixel[1]=ToByte(g+(gg-g)*strength);pixel[0]=ToByte(b+(bb-b)*strength);
                                }
                            }
                        }
                    }
                    finally{bitmap.UnlockBits(bits);}
                    var codec=ImageCodecInfo.GetImageEncoders().First(x=>x.FormatID==ImageFormat.Jpeg.Guid);
                    using(var parameters=new EncoderParameters(1)){parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,100L);bitmap.Save(temporary,codec,parameters);}
                }
                File.Replace(temporary,imagePath,null);
            }
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
        }

        internal static void Sample(ColorLutData lut,float r,float g,float b,out float or,out float og,out float ob)
        {
            var m=lut.Metadata;var n=m.CubeSize;
            var pr=Position(r,m.DomainMinR,m.DomainMaxR,n);var pg=Position(g,m.DomainMinG,m.DomainMaxG,n);var pb=Position(b,m.DomainMinB,m.DomainMaxB,n);
            var r0=(int)pr;var g0=(int)pg;var b0=(int)pb;var r1=Math.Min(r0+1,n-1);var g1=Math.Min(g0+1,n-1);var b1=Math.Min(b0+1,n-1);
            var fr=pr-r0;var fg=pg-g0;var fb=pb-b0;var values=lut.Values;
            var c000=Index(n,r0,g0,b0);int a,bx,c;
            if(fr>=fg){if(fg>=fb){a=Index(n,r1,g0,b0);bx=Index(n,r1,g1,b0);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fr,fg,fb,out or,out og,out ob);}
                else if(fr>=fb){a=Index(n,r1,g0,b0);bx=Index(n,r1,g0,b1);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fr,fb,fg,out or,out og,out ob);}
                else{a=Index(n,r0,g0,b1);bx=Index(n,r1,g0,b1);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fb,fr,fg,out or,out og,out ob);}}
            else{if(fb>=fg){a=Index(n,r0,g0,b1);bx=Index(n,r0,g1,b1);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fb,fg,fr,out or,out og,out ob);}
                else if(fb>=fr){a=Index(n,r0,g1,b0);bx=Index(n,r0,g1,b1);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fg,fb,fr,out or,out og,out ob);}
                else{a=Index(n,r0,g1,b0);bx=Index(n,r1,g1,b0);c=Index(n,r1,g1,b1);Mix(values,c000,a,bx,c,fg,fr,fb,out or,out og,out ob);}}
        }
        static float Position(float value,float min,float max,int size)=>Math.Max(0,Math.Min(size-1,(value-min)/(max-min)*(size-1)));
        static int Index(int n,int r,int g,int b)=>((b*n+g)*n+r)*3;
        static void Mix(float[] v,int p0,int p1,int p2,int p3,float f1,float f2,float f3,out float r,out float g,out float b){r=v[p0]+f1*(v[p1]-v[p0])+f2*(v[p2]-v[p1])+f3*(v[p3]-v[p2]);g=v[p0+1]+f1*(v[p1+1]-v[p0+1])+f2*(v[p2+1]-v[p1+1])+f3*(v[p3+1]-v[p2+1]);b=v[p0+2]+f1*(v[p1+2]-v[p0+2])+f2*(v[p2+2]-v[p1+2])+f3*(v[p3+2]-v[p2+2]);}
        static byte ToByte(float value)=>(byte)Math.Round(Math.Max(0,Math.Min(1,value))*255);

        public async Task<ColorLutImportResult> ImportAsync(string sourcePath,string displayName,CancellationToken token)
        {
            if(string.IsNullOrWhiteSpace(sourcePath)||!string.Equals(Path.GetExtension(sourcePath),".cube",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only .cube files are accepted.");
            var staging=Path.Combine(paths.StagingDirectory,Guid.NewGuid().ToString("N")+".cube");
            try
            {
                await Task.Run(()=>CopyFile(sourcePath,staging,token),token).ConfigureAwait(false);
                string hash=await Task.Run(()=>Hash(staging,token),token).ConfigureAwait(false);
                var duplicate=await assets.GetByHashAsync(hash,token).ConfigureAwait(false);
                if(duplicate!=null){return new ColorLutImportResult{Asset=duplicate,WasDuplicate=true};}
                ColorLutMetadata metadata;IReadOnlyList<string> warnings;
                using(var data=await Task.Run(()=>parser.Parse(staging,token),token).ConfigureAwait(false)){metadata=data.Metadata;warnings=metadata.CubeSize>65?new[]{"LUT is capture-only because live view supports up to 65³."}:new string[0];}
                var now=DateTime.UtcNow;var id=Guid.NewGuid();var relative=paths.CreateRelativeAssetPath(id,hash);var destination=paths.GetFullPath(relative);
                var asset=new ColorLutAsset{Id=id,DisplayName=string.IsNullOrWhiteSpace(displayName)?Path.GetFileNameWithoutExtension(sourcePath):displayName.Trim(),RelativePath=relative,ContentHashSha256=hash,FileLength=new FileInfo(staging).Length,CubeSize=metadata.CubeSize,DomainMinR=metadata.DomainMinR,DomainMinG=metadata.DomainMinG,DomainMinB=metadata.DomainMinB,DomainMaxR=metadata.DomainMaxR,DomainMaxG=metadata.DomainMaxG,DomainMaxB=metadata.DomainMaxB,Status=ColorLutAssetStatus.Staging,LastValidatedAtUtc=now,CreatedAtUtc=now,ModifiedAtUtc=now,RowVersion=1};
                try{await assets.InsertAsync(asset,token).ConfigureAwait(false);}catch(SqliteException){duplicate=await assets.GetByHashAsync(hash,token).ConfigureAwait(false);if(duplicate!=null)return new ColorLutImportResult{Asset=duplicate,WasDuplicate=true};throw;}
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));File.Move(staging,destination);
                    asset.Status=ColorLutAssetStatus.Ready;asset.ModifiedAtUtc=DateTime.UtcNow;if(!await assets.UpdateAsync(asset,1,token).ConfigureAwait(false))throw new InvalidOperationException("LUT import lost its database update race.");
                }
                catch
                {
                    // If the final file exists, retain Staging so startup reconciliation can
                    // safely complete the state transition. Otherwise remove the unusable row.
                    if(!File.Exists(destination))await assets.DeleteAsync(asset.Id,1,CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                return new ColorLutImportResult{Asset=asset,Warnings=warnings};
            }
            finally{try{if(File.Exists(staging))File.Delete(staging);}catch(Exception e){log.LogWarning(e,"Unable to clean LUT staging file {Path}",staging);}}
        }

        public async Task AttachAsync(Guid presetId,Guid assetId,float strength,CancellationToken token)
        {
            if(strength<0||strength>1)throw new ArgumentOutOfRangeException(nameof(strength));
            if(await presets.GetAsync(presetId,token).ConfigureAwait(false)==null)throw new InvalidOperationException("Preset does not exist.");
            var asset=await assets.GetAsync(assetId,token).ConfigureAwait(false);if(asset==null||asset.Status!=ColorLutAssetStatus.Ready)throw new InvalidOperationException("LUT asset is not ready.");
            var existing=await colors.GetAsync(presetId,token).ConfigureAwait(false);var value=existing??new PresetColorSettings{PresetId=presetId};value.LutAssetId=assetId;value.Strength=strength;value.Enabled=true;value.ModifiedAtUtc=DateTime.UtcNow;await colors.SaveAsync(value,existing?.RowVersion,token).ConfigureAwait(false);
        }
        public Task DetachAsync(Guid presetId,CancellationToken token)=>colors.RemoveAsync(presetId,token);

        public async Task DeleteAsync(Guid assetId,long expectedRowVersion,CancellationToken token)
        {
            var asset=await assets.GetAsync(assetId,token).ConfigureAwait(false);if(asset==null)return;if(asset.RowVersion!=expectedRowVersion)throw new InvalidOperationException("LUT asset was modified by another operation.");
            if(await assets.GetUsageCountAsync(assetId,token).ConfigureAwait(false)>0)throw new InvalidOperationException("LUT is still used by one or more presets.");
            asset.Status=ColorLutAssetStatus.PendingDelete;asset.ModifiedAtUtc=DateTime.UtcNow;if(!await assets.UpdateAsync(asset,expectedRowVersion,token).ConfigureAwait(false))throw new InvalidOperationException("LUT asset was modified by another operation.");
            var full=paths.GetFullPath(asset.RelativePath);var trashDirectory=Path.Combine(paths.StagingDirectory,"Trash");Directory.CreateDirectory(trashDirectory);var trash=Path.Combine(trashDirectory,Path.GetFileName(full));
            try{if(File.Exists(full)){if(File.Exists(trash))File.Delete(trash);File.Move(full,trash);}if(!await assets.DeleteAsync(asset.Id,asset.RowVersion,token).ConfigureAwait(false))throw new InvalidOperationException("LUT delete lost its database update race.");}
            catch
            {
                var restored=!File.Exists(trash);
                if(!restored)try{if(!File.Exists(full)){Directory.CreateDirectory(Path.GetDirectoryName(full));File.Move(trash,full);}restored=File.Exists(full);}catch(Exception e){log.LogError(e,"Unable to restore LUT file after failed delete for {AssetId}",asset.Id);}
                if(restored){asset.Status=ColorLutAssetStatus.Ready;asset.ModifiedAtUtc=DateTime.UtcNow;await assets.UpdateAsync(asset,asset.RowVersion,CancellationToken.None).ConfigureAwait(false);}
                throw;
            }
        }

        public async Task ReconcileAsync(CancellationToken token)
        {
            foreach(var asset in await assets.GetAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();string full;
                try{full=paths.GetFullPath(asset.RelativePath);}catch(Exception e){await SetStatus(asset,ColorLutAssetStatus.Corrupt,token).ConfigureAwait(false);log.LogError(e,"Unsafe LUT path for {AssetId}",asset.Id);continue;}
                if(asset.Status==ColorLutAssetStatus.PendingDelete){if(!File.Exists(full))await assets.DeleteAsync(asset.Id,asset.RowVersion,token).ConfigureAwait(false);continue;}
                if(!File.Exists(full))
                {
                    if(asset.Status==ColorLutAssetStatus.Staging)await assets.DeleteAsync(asset.Id,asset.RowVersion,token).ConfigureAwait(false);
                    else await SetStatus(asset,ColorLutAssetStatus.Missing,token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var info=new FileInfo(full);var hash=await Task.Run(()=>Hash(full,token),token).ConfigureAwait(false);
                    if(info.Length!=asset.FileLength||!string.Equals(hash,asset.ContentHashSha256,StringComparison.OrdinalIgnoreCase)){await SetStatus(asset,ColorLutAssetStatus.Corrupt,token).ConfigureAwait(false);continue;}
                    using(var data=await Task.Run(()=>parser.Parse(full,token),token).ConfigureAwait(false)){if(data.Metadata.CubeSize!=asset.CubeSize){await SetStatus(asset,ColorLutAssetStatus.Corrupt,token).ConfigureAwait(false);continue;}}
                    if(asset.Status!=ColorLutAssetStatus.Ready)await SetStatus(asset,ColorLutAssetStatus.Ready,token).ConfigureAwait(false);
                }
                catch(OperationCanceledException){throw;}catch(Exception e){await SetStatus(asset,ColorLutAssetStatus.Corrupt,token).ConfigureAwait(false);log.LogWarning(e,"LUT reconciliation failed for {AssetId}",asset.Id);}
            }
        }
        async Task SetStatus(ColorLutAsset asset,ColorLutAssetStatus status,CancellationToken token){if(asset.Status==status)return;var version=asset.RowVersion;asset.Status=status;asset.ModifiedAtUtc=DateTime.UtcNow;await assets.UpdateAsync(asset,version,token).ConfigureAwait(false);}
        static void CopyFile(string source,string destination,CancellationToken token){using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var buffer=new byte[81920];int read;while((read=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,read);}output.Flush(true);}}
        static string Hash(string path,CancellationToken token){using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create()){var buffer=new byte[81920];int read;while((read=stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,read,null,0);}sha.TransformFinalBlock(new byte[0],0,0);return string.Concat(sha.Hash.Select(x=>x.ToString("x2")));}}
    }
}
