using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public class SqliteDeliverableRepository : ICaptureRepository, IDeliverableRepository
    {
        readonly SqliteDatabase database;
        public SqliteDeliverableRepository(SqliteDatabase value){database=value;}

        public Task<PhotoCapture> GetAsync(string id,CancellationToken token)=>Get(null,id,token);
        public Task<PhotoCapture> GetAsync(Guid sessionId,string id,CancellationToken token)=>Get(sessionId,id,token);
        Task<PhotoCapture> Get(Guid? sessionId,string id,CancellationToken token)
        {
            if(string.IsNullOrWhiteSpace(id))return Task.FromResult<PhotoCapture>(null);token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var command=CreateQuery(connection,sessionId.HasValue?" WHERE d.BoothSessionId=$session AND d.Id=$id":" WHERE d.Id=$id"))
            {
                if(sessionId.HasValue)command.Parameters.AddWithValue("$session",sessionId.Value.ToString());command.Parameters.AddWithValue("$id",id);
                using(var reader=command.ExecuteReader()){if(!reader.Read())return Task.FromResult<PhotoCapture>(null);var value=Read(reader);value.Photos=LoadAssets(connection,value.Id);return Task.FromResult(value);}
            }
        }

        public Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId,CancellationToken token)
        {
            var values=new List<PhotoCapture>();using(var connection=database.OpenConnection())using(var command=CreateQuery(connection," WHERE d.BoothSessionId=$session ORDER BY d.CreatedAtUtc DESC"))
            {command.Parameters.AddWithValue("$session",sessionId.ToString());using(var reader=command.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();values.Add(Read(reader));}foreach(var value in values)value.Photos=LoadAssets(connection,value.Id);}
            return Task.FromResult<IReadOnlyList<PhotoCapture>>(values);
        }

        public Task SaveAsync(PhotoCapture value,CancellationToken token)
        {
            if(value==null||string.IsNullOrWhiteSpace(value.Id)||value.SessionId==Guid.Empty)throw new ArgumentException("Deliverable identity is required.");
            token.ThrowIfCancellationRequested();using(var connection=database.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                EnsureBooth(connection,transaction,value.SessionId);
                foreach(var asset in value.Photos??new CapturePhoto[0])UpsertMediaAsset(connection,transaction,value.SessionId,asset);
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText=@"INSERT INTO Deliverables(Id,BoothSessionId,Revision,FrameId,FinalCompositeAssetId,FinalCompositePath,LocalSharePath,MediaMode,State,UploadAttempts,CreatedAtUtc,ReadyAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError)
VALUES($id,$session,COALESCE((SELECT MAX(Revision)+1 FROM Deliverables WHERE BoothSessionId=$session),1),$frame,$compositeId,$path,$share,$mode,$state,$attempts,$created,$ready,$uploaded,$expires,$error)
ON CONFLICT(Id) DO UPDATE SET FrameId=excluded.FrameId,FinalCompositeAssetId=excluded.FinalCompositeAssetId,FinalCompositePath=excluded.FinalCompositePath,LocalSharePath=excluded.LocalSharePath,MediaMode=excluded.MediaMode,State=excluded.State,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,ExpiresAtUtc=excluded.ExpiresAtUtc,LastError=excluded.LastError";
                    command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$session",value.SessionId.ToString());command.Parameters.AddWithValue("$frame",Db(value.FrameId));command.Parameters.AddWithValue("$compositeId",Db(value.CompositeImageId));command.Parameters.AddWithValue("$path",ToRelative(value.CompositePath));command.Parameters.AddWithValue("$share",Db(value.SharePath));command.Parameters.AddWithValue("$mode",string.IsNullOrWhiteSpace(value.MediaMode)?CaptureMediaModes.PictureOnly:value.MediaMode);command.Parameters.AddWithValue("$state",NormalizeState(value.Status));command.Parameters.AddWithValue("$attempts",Math.Max(0,value.UploadAttempts));command.Parameters.AddWithValue("$created",Utc(value.CreatedAtUtc));command.Parameters.AddWithValue("$ready",Utc(value.CreatedAtUtc));command.Parameters.AddWithValue("$uploaded",Db(value.UploadedAtUtc));command.Parameters.AddWithValue("$expires",Db(value.ExpiresAtUtc));command.Parameters.AddWithValue("$error",Db(value.LastError));command.ExecuteNonQuery();
                }
                using(var clear=connection.CreateCommand()){clear.Transaction=transaction;clear.CommandText="DELETE FROM DeliverableAssets WHERE DeliverableId=$id";clear.Parameters.AddWithValue("$id",value.Id);clear.ExecuteNonQuery();}
                foreach(var asset in value.Photos??new CapturePhoto[0])
                {
                    using(var link=connection.CreateCommand()){link.Transaction=transaction;link.CommandText="INSERT INTO DeliverableAssets(DeliverableId,MediaAssetId,Role,Position) VALUES($deliverable,$asset,$role,$position)";link.Parameters.AddWithValue("$deliverable",value.Id);link.Parameters.AddWithValue("$asset",asset.Id);link.Parameters.AddWithValue("$role",NormalizeRole(asset.PhotoType,asset.LocalPath));link.Parameters.AddWithValue("$position",Math.Max(0,asset.Position));link.ExecuteNonQuery();}
                    using(var clearLineage=connection.CreateCommand()){clearLineage.Transaction=transaction;clearLineage.CommandText="DELETE FROM AssetLineage WHERE DerivedAssetId=$id";clearLineage.Parameters.AddWithValue("$id",asset.Id);clearLineage.ExecuteNonQuery();}
                    foreach(var sourceId in asset.SourceAssetIds??new string[0])using(var lineage=connection.CreateCommand()){lineage.Transaction=transaction;lineage.CommandText="INSERT INTO AssetLineage(DerivedAssetId,SourceAssetId,Purpose) VALUES($derived,$source,$purpose)";lineage.Parameters.AddWithValue("$derived",asset.Id);lineage.Parameters.AddWithValue("$source",sourceId);lineage.Parameters.AddWithValue("$purpose",NormalizeRole(asset.PhotoType,asset.LocalPath));lineage.ExecuteNonQuery();}
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        static void EnsureBooth(SqliteConnection connection,SqliteTransaction transaction,Guid id)
        {using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT COUNT(*) FROM BoothSessions WHERE Id=$id";command.Parameters.AddWithValue("$id",id.ToString());if(Convert.ToInt32(command.ExecuteScalar())!=1)throw new InvalidOperationException("Booth session not found.");}}

        void UpsertMediaAsset(SqliteConnection connection,SqliteTransaction transaction,Guid sessionId,CapturePhoto asset)
        {
            if(asset==null||string.IsNullOrWhiteSpace(asset.Id)||string.IsNullOrWhiteSpace(asset.LocalPath))throw new ArgumentException("Deliverable asset identity and path are required.");
            using(var command=connection.CreateCommand())
            {
                command.Transaction=transaction;command.CommandText=@"INSERT INTO MediaAssets(Id,BoothSessionId,CaptureAttemptId,Kind,RelativePath,MimeType,FileLength,ContentHashSha256,State,RetentionClass,CloudProviderReference,UploadAttempts,UploadedAtUtc,LastError,CreatedAtUtc,UpdatedAtUtc)
VALUES($id,$session,$attempt,$kind,$path,$mime,$length,$hash,$state,$retention,$cloud,$attempts,$uploaded,$error,$created,$updated)
ON CONFLICT(Id) DO UPDATE SET RelativePath=excluded.RelativePath,MimeType=excluded.MimeType,FileLength=excluded.FileLength,ContentHashSha256=COALESCE(excluded.ContentHashSha256,MediaAssets.ContentHashSha256),State=excluded.State,RetentionClass=excluded.RetentionClass,CloudProviderReference=excluded.CloudProviderReference,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,LastError=excluded.LastError,UpdatedAtUtc=excluded.UpdatedAtUtc";
                var role=NormalizeRole(asset.PhotoType,asset.LocalPath);var created=asset.CreatedAtUtc==DateTime.MinValue?DateTime.UtcNow:asset.CreatedAtUtc;
                command.Parameters.AddWithValue("$id",asset.Id);command.Parameters.AddWithValue("$session",sessionId.ToString());command.Parameters.AddWithValue("$attempt",Db(asset.CapturedImageId));command.Parameters.AddWithValue("$kind",Kind(role));command.Parameters.AddWithValue("$path",ToRelative(asset.LocalPath));command.Parameters.AddWithValue("$mime",string.IsNullOrWhiteSpace(asset.MimeType)?Mime(role,asset.LocalPath):asset.MimeType);command.Parameters.AddWithValue("$length",Math.Max(0,asset.FileLength));command.Parameters.AddWithValue("$hash",Db(asset.ContentHashSha256));command.Parameters.AddWithValue("$state",NormalizeAssetState(asset.AssetStatus));command.Parameters.AddWithValue("$retention",role==CaptureAssetTypes.Picture||role==CaptureAssetTypes.Video?MediaRetentionClasses.Original:MediaRetentionClasses.Deliverable);command.Parameters.AddWithValue("$cloud",Db(asset.CloudinaryPublicId));command.Parameters.AddWithValue("$attempts",Math.Max(0,asset.UploadAttempts));command.Parameters.AddWithValue("$uploaded",Db(asset.UploadedAtUtc));command.Parameters.AddWithValue("$error",Db(asset.LastError));command.Parameters.AddWithValue("$created",Utc(created));command.Parameters.AddWithValue("$updated",Utc(DateTime.UtcNow));command.ExecuteNonQuery();
            }
        }

        static SqliteCommand CreateQuery(SqliteConnection connection,string suffix)
        {var command=connection.CreateCommand();command.CommandText=@"SELECT d.Id,d.BoothSessionId,d.FrameId,d.FinalCompositeAssetId,d.FinalCompositePath,d.LocalSharePath,d.MediaMode,d.State,d.UploadAttempts,d.CreatedAtUtc,d.UploadedAtUtc,d.ExpiresAtUtc,d.LastError FROM Deliverables d"+suffix;return command;}
        PhotoCapture Read(SqliteDataReader reader)=>new PhotoCapture{Id=reader.GetString(0),SessionId=Guid.Parse(reader.GetString(1)),FrameId=GuidValue(reader,2),CompositeImageId=Text(reader,3),CompositePath=ToFull(reader.GetString(4)),SharePath=Text(reader,5),MediaMode=reader.GetString(6),Status=reader.GetString(7),UploadAttempts=reader.GetInt32(8),CreatedAtUtc=Parse(reader.GetString(9)),UploadedAtUtc=DateValue(reader,10),ExpiresAtUtc=DateValue(reader,11),LastError=Text(reader,12)};
        IReadOnlyList<CapturePhoto> LoadAssets(SqliteConnection connection,string deliverableId)
        {
            var values=new List<CapturePhoto>();using(var command=connection.CreateCommand())
            {
                command.CommandText=@"SELECT m.Id,a.DeliverableId,m.CaptureAttemptId,m.RelativePath,a.Role,a.Position,m.CloudProviderReference,m.UploadAttempts,m.UploadedAtUtc,m.LastError,m.MimeType,m.FileLength,m.ContentHashSha256,m.CreatedAtUtc,m.State FROM DeliverableAssets a INNER JOIN MediaAssets m ON m.Id=a.MediaAssetId WHERE a.DeliverableId=$id ORDER BY CASE a.Role WHEN 'Picture' THEN 0 WHEN 'Video' THEN 1 WHEN 'Composite' THEN 2 WHEN 'CompositeVideo' THEN 3 WHEN 'Gif' THEN 4 ELSE 5 END,a.Position";
                command.Parameters.AddWithValue("$id",deliverableId);using(var reader=command.ExecuteReader())while(reader.Read())values.Add(new CapturePhoto{Id=reader.GetString(0),CaptureId=reader.GetString(1),CapturedImageId=Text(reader,2),LocalPath=ToFull(reader.GetString(3)),PhotoType=reader.GetString(4),Position=reader.GetInt32(5),CloudinaryPublicId=Text(reader,6),UploadAttempts=reader.GetInt32(7),UploadedAtUtc=DateValue(reader,8),LastError=Text(reader,9),IsUploaded=!reader.IsDBNull(8),MimeType=reader.GetString(10),FileLength=reader.GetInt64(11),ContentHashSha256=Text(reader,12),CreatedAtUtc=Parse(reader.GetString(13)),AssetStatus=reader.GetString(14)});
            }
            foreach(var value in values)value.SourceAssetIds=LoadSources(connection,value.Id);return values;
        }
        static IReadOnlyList<string> LoadSources(SqliteConnection connection,string id){var values=new List<string>();using(var command=connection.CreateCommand()){command.CommandText="SELECT SourceAssetId FROM AssetLineage WHERE DerivedAssetId=$id ORDER BY SourceAssetId";command.Parameters.AddWithValue("$id",id);using(var reader=command.ExecuteReader())while(reader.Read())values.Add(reader.GetString(0));}return values;}

        string ToRelative(string path){if(string.IsNullOrWhiteSpace(path))throw new InvalidOperationException("Managed media path is required.");if(!Path.IsPathRooted(path))return path.Replace('\\','/');var root=database.DataRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;var full=Path.GetFullPath(path);if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Media path is outside the managed data root.");return full.Substring(root.Length).Replace('\\','/');}
        string ToFull(string relative){if(Path.IsPathRooted(relative))throw new InvalidOperationException("Stored media path must be relative.");var root=database.DataRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;var full=Path.GetFullPath(Path.Combine(database.DataRoot,relative.Replace('/',Path.DirectorySeparatorChar)));if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Stored media path escapes the managed data root.");return full;}
        static string NormalizeRole(string role,string path){if(!string.Equals(role,"Original",StringComparison.OrdinalIgnoreCase))return role;return path.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase)?CaptureAssetTypes.Video:CaptureAssetTypes.Picture;}
        static string Kind(string role){if(role==CaptureAssetTypes.Picture)return MediaAssetKinds.OriginalPicture;if(role==CaptureAssetTypes.Video)return MediaAssetKinds.OriginalVideo;if(role==CaptureAssetTypes.Composite)return MediaAssetKinds.FinalComposite;if(role==CaptureAssetTypes.CompositeVideo)return MediaAssetKinds.FinalVideo;if(role==CaptureAssetTypes.Gif)return MediaAssetKinds.Gif;if(role==CaptureAssetTypes.ShareArchive)return MediaAssetKinds.ShareArchive;throw new InvalidOperationException("Unsupported deliverable asset role: "+role);}
        static string Mime(string role,string path){if(role==CaptureAssetTypes.Video||role==CaptureAssetTypes.CompositeVideo)return "video/mp4";if(role==CaptureAssetTypes.Picture)return "image/jpeg";if(role==CaptureAssetTypes.Gif)return "image/gif";if(role==CaptureAssetTypes.ShareArchive)return "application/zip";return path.EndsWith(".png",StringComparison.OrdinalIgnoreCase)?"image/png":"application/octet-stream";}
        static string NormalizeState(string state){switch(state){case "Uploading":return "Uploading";case "Uploaded":return "Uploaded";case "GalleryReady":return "GalleryReady";case "Expired":return "Expired";case "Failed":case "UploadFailed":return "Failed";default:return "Ready";}}
        static string NormalizeAssetState(string state){switch(state){case "Staging":case "Ready":case "Missing":case "Corrupt":case "PendingDelete":case "Deleted":return state;default:return "Ready";}}
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;
        static object Db(Guid? value)=>value.HasValue?(object)value.Value.ToString():DBNull.Value;
        static object Db(DateTime? value)=>value.HasValue?(object)Utc(value.Value):DBNull.Value;
        static string Text(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
        static Guid? GuidValue(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(Guid?)null:Guid.Parse(reader.GetString(index));
        static DateTime? DateValue(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?(DateTime?)null:Parse(reader.GetString(index));
        static DateTime Parse(string value)=>DateTime.Parse(value).ToUniversalTime();
        static string Utc(DateTime value)=>(value==default(DateTime)?DateTime.UtcNow:value.ToUniversalTime()).ToString("O");

        async Task<Deliverable> IDeliverableRepository.GetAsync(string id,CancellationToken token)=>ToDeliverable(await GetAsync(id,token).ConfigureAwait(false));
        async Task<Deliverable> IDeliverableRepository.GetAsync(Guid sessionId,string id,CancellationToken token)=>ToDeliverable(await GetAsync(sessionId,id,token).ConfigureAwait(false));
        async Task<IReadOnlyList<Deliverable>> IDeliverableRepository.GetByBoothSessionAsync(Guid sessionId,CancellationToken token){var source=await GetBySessionAsync(sessionId,token).ConfigureAwait(false);var result=new List<Deliverable>();foreach(var value in source)result.Add(ToDeliverable(value));return result;}
        Task IDeliverableRepository.SaveAsync(Deliverable value,CancellationToken token)=>SaveAsync(value,token);
        static Deliverable ToDeliverable(PhotoCapture value){if(value==null)return null;var assets=new List<DeliverableAsset>();foreach(var photo in value.Photos??new CapturePhoto[0])assets.Add(new DeliverableAsset{Id=photo.Id,DeliverableId=photo.CaptureId,CapturedShotId=photo.CapturedImageId,LocalPath=photo.LocalPath,Role=photo.PhotoType,Position=photo.Position,MimeType=photo.MimeType,FileLength=photo.FileLength,ContentHashSha256=photo.ContentHashSha256,CreatedAtUtc=photo.CreatedAtUtc,AssetStatus=photo.AssetStatus,SourceAssetIds=photo.SourceAssetIds,CloudinaryPublicId=photo.CloudinaryPublicId,IsUploaded=photo.IsUploaded,UploadAttempts=photo.UploadAttempts,UploadedAtUtc=photo.UploadedAtUtc,LastError=photo.LastError});return new Deliverable{Id=value.Id,BoothSessionId=value.SessionId,FrameId=value.FrameId,CompositeImageId=value.CompositeImageId,CompositePath=value.CompositePath,SharePath=value.SharePath,Status=value.Status,MediaMode=value.MediaMode,UploadAttempts=value.UploadAttempts,CreatedAtUtc=value.CreatedAtUtc,UploadedAtUtc=value.UploadedAtUtc,ExpiresAtUtc=value.ExpiresAtUtc,LastError=value.LastError,Assets=assets};}
    }

    public sealed class SqliteCaptureRepository:SqliteDeliverableRepository{public SqliteCaptureRepository(SqliteDatabase database):base(database){}}
}
