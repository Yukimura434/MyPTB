using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteCaptureRepository : ICaptureRepository
    {
        private readonly SqliteDatabase database;

        public SqliteCaptureRepository(SqliteDatabase database) { this.database = database; }

        public Task<PhotoCapture> GetAsync(string captureId, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(captureId)) return Task.FromResult<PhotoCapture>(null);
            using (var connection = database.OpenConnection())
            using (var command = CreateCaptureQuery(connection, "WHERE Id=$id"))
            {
                command.Parameters.AddWithValue("$id", captureId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<PhotoCapture>(null);
                    var capture = ReadCapture(reader);
                    capture.Photos = LoadPhotos(connection, capture.Id);
                    return Task.FromResult(capture);
                }
            }
        }

        public Task<PhotoCapture> GetAsync(Guid sessionId, string captureId, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(captureId)) return Task.FromResult<PhotoCapture>(null);
            using (var connection = database.OpenConnection())
            using (var command = CreateCaptureQuery(connection, "WHERE SessionId=$session AND Id=$id"))
            {
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                command.Parameters.AddWithValue("$id", captureId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<PhotoCapture>(null);
                    var capture = ReadCapture(reader);
                    capture.Photos = LoadPhotos(connection, capture.Id);
                    return Task.FromResult(capture);
                }
            }
        }

        public Task<IReadOnlyList<PhotoCapture>> GetBySessionAsync(Guid sessionId, CancellationToken token)
        {
            var captures = new List<PhotoCapture>();
            using (var connection = database.OpenConnection())
            using (var command = CreateCaptureQuery(connection, "WHERE SessionId=$session ORDER BY CreatedAtUtc DESC"))
            {
                command.Parameters.AddWithValue("$session", sessionId.ToString());
                using (var reader = command.ExecuteReader()) while (reader.Read()) captures.Add(ReadCapture(reader));
                foreach (var capture in captures) capture.Photos = LoadPhotos(connection, capture.Id);
            }
            return Task.FromResult<IReadOnlyList<PhotoCapture>>(captures);
        }

        public Task SaveAsync(PhotoCapture capture, CancellationToken token)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            if (string.IsNullOrWhiteSpace(capture.Id)) throw new ArgumentException("Capture ID is required.", nameof(capture));
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO Captures (Id,SessionId,FrameId,CompositeImageId,CompositePath,LocalSharePath,MediaMode,Status,UploadAttempts,CreatedAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError)
VALUES($id,$session,$frame,$compositeId,$path,$sharePath,$mediaMode,$status,$attempts,$created,$uploaded,$expires,$error)
ON CONFLICT(Id) DO UPDATE SET FrameId=excluded.FrameId,CompositeImageId=excluded.CompositeImageId,CompositePath=excluded.CompositePath,LocalSharePath=excluded.LocalSharePath,MediaMode=excluded.MediaMode,Status=excluded.Status,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,ExpiresAtUtc=excluded.ExpiresAtUtc,LastError=excluded.LastError";
                    command.Parameters.AddWithValue("$id", capture.Id);
                    command.Parameters.AddWithValue("$session", capture.SessionId.ToString());
                    command.Parameters.AddWithValue("$frame", capture.FrameId.HasValue ? (object)capture.FrameId.Value.ToString() : DBNull.Value);
                    command.Parameters.AddWithValue("$compositeId", Db(capture.CompositeImageId));
                    command.Parameters.AddWithValue("$path", capture.CompositePath);
                    command.Parameters.AddWithValue("$sharePath", Db(capture.SharePath));
                    command.Parameters.AddWithValue("$mediaMode", string.IsNullOrWhiteSpace(capture.MediaMode) ? CaptureMediaModes.PictureOnly : capture.MediaMode);
                    command.Parameters.AddWithValue("$status", capture.Status ?? "Pending");
                    command.Parameters.AddWithValue("$attempts", capture.UploadAttempts);
                    command.Parameters.AddWithValue("$created", capture.CreatedAtUtc.ToString("O"));
                    command.Parameters.AddWithValue("$uploaded", Date(capture.UploadedAtUtc));
                    command.Parameters.AddWithValue("$expires", Date(capture.ExpiresAtUtc));
                    command.Parameters.AddWithValue("$error", Db(capture.LastError));
                    command.ExecuteNonQuery();
                }
                foreach (var photo in capture.Photos ?? new CapturePhoto[0]) SavePhoto(connection, transaction, capture.Id, photo);
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        static SqliteCommand CreateCaptureQuery(SqliteConnection connection, string suffix)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id,SessionId,FrameId,CompositeImageId,CompositePath,LocalSharePath,MediaMode,Status,UploadAttempts,CreatedAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError FROM Captures " + suffix;
            return command;
        }

        static PhotoCapture ReadCapture(SqliteDataReader reader) => new PhotoCapture
        {
            Id = reader.GetString(0), SessionId = Guid.Parse(reader.GetString(1)), FrameId = reader.IsDBNull(2) ? (Guid?)null : Guid.Parse(reader.GetString(2)),
            CompositeImageId = Text(reader, 3), CompositePath = reader.GetString(4), SharePath = Text(reader, 5), MediaMode=Text(reader,6), Status = reader.GetString(7), UploadAttempts = reader.GetInt32(8),
            CreatedAtUtc = Parse(reader.GetString(9)), UploadedAtUtc = NullableDate(reader, 10), ExpiresAtUtc = NullableDate(reader, 11), LastError = Text(reader, 12)
        };

        static IReadOnlyList<CapturePhoto> LoadPhotos(SqliteConnection connection, string captureId)
        {
            var photos = new List<CapturePhoto>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,CloudinaryPublicId,UploadAttempts,UploadedAtUtc,LastError,IsUploaded,MimeType,FileLength,ContentHashSha256,CreatedAtUtc,AssetStatus FROM CapturePhotos WHERE CaptureId=$capture ORDER BY CASE PhotoType WHEN 'Picture' THEN 0 WHEN 'MotionPhoto' THEN 0 WHEN 'Composite' THEN 1 WHEN 'MotionPhotoComposite' THEN 2 WHEN 'Gif' THEN 3 ELSE 4 END,Position";
                command.Parameters.AddWithValue("$capture", captureId);
                using (var reader = command.ExecuteReader()) while (reader.Read()) photos.Add(new CapturePhoto
                {
                    Id=reader.GetString(0), CaptureId=reader.GetString(1), CapturedImageId=Text(reader,2), LocalPath=reader.GetString(3), PhotoType=reader.GetString(4), Position=reader.GetInt32(5), CloudinaryPublicId=Text(reader,6), UploadAttempts=reader.GetInt32(7), UploadedAtUtc=NullableDate(reader,8), LastError=Text(reader,9), IsUploaded=reader.GetInt32(10)!=0,MimeType=Text(reader,11),FileLength=reader.GetInt64(12),ContentHashSha256=Text(reader,13),CreatedAtUtc=reader.IsDBNull(14)?DateTime.MinValue:Parse(reader.GetString(14)),AssetStatus=Text(reader,15)
                });
            }
            foreach(var photo in photos)photo.SourceAssetIds=LoadSources(connection,photo.Id);
            return photos;
        }

        static IReadOnlyList<string> LoadSources(SqliteConnection connection,string assetId){var values=new List<string>();using(var command=connection.CreateCommand()){command.CommandText="SELECT SourceAssetId FROM CaptureAssetSources WHERE AssetId=$id ORDER BY SourceAssetId";command.Parameters.AddWithValue("$id",assetId);using(var reader=command.ExecuteReader())while(reader.Read())values.Add(reader.GetString(0));}return values;}

        static void SavePhoto(SqliteConnection connection, SqliteTransaction transaction, string captureId, CapturePhoto photo)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO CapturePhotos (Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,CloudinaryPublicId,UploadAttempts,UploadedAtUtc,LastError,IsUploaded,MimeType,FileLength,ContentHashSha256,CreatedAtUtc,AssetStatus)
VALUES($id,$capture,$image,$path,$type,$position,$publicId,$attempts,$uploaded,$error,$isUploaded,$mime,$length,$hash,$created,$assetStatus)
ON CONFLICT(Id) DO UPDATE SET CapturedImageId=excluded.CapturedImageId,LocalPath=excluded.LocalPath,PhotoType=excluded.PhotoType,Position=excluded.Position,CloudinaryPublicId=excluded.CloudinaryPublicId,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,LastError=excluded.LastError,IsUploaded=excluded.IsUploaded,MimeType=excluded.MimeType,FileLength=excluded.FileLength,ContentHashSha256=excluded.ContentHashSha256,CreatedAtUtc=excluded.CreatedAtUtc,AssetStatus=excluded.AssetStatus";
                command.Parameters.AddWithValue("$id", photo.Id);
                command.Parameters.AddWithValue("$capture", captureId);
                command.Parameters.AddWithValue("$image", Db(photo.CapturedImageId));
                command.Parameters.AddWithValue("$path", photo.LocalPath);
                command.Parameters.AddWithValue("$type", NormalizeType(photo.PhotoType,photo.LocalPath));
                command.Parameters.AddWithValue("$position", photo.Position);
                command.Parameters.AddWithValue("$publicId", Db(photo.CloudinaryPublicId));
                command.Parameters.AddWithValue("$attempts", photo.UploadAttempts);
                command.Parameters.AddWithValue("$uploaded", Date(photo.UploadedAtUtc));
                command.Parameters.AddWithValue("$error", Db(photo.LastError));
                command.Parameters.AddWithValue("$isUploaded", photo.IsUploaded ? 1 : 0);
                command.Parameters.AddWithValue("$mime",Db(photo.MimeType));command.Parameters.AddWithValue("$length",photo.FileLength);command.Parameters.AddWithValue("$hash",Db(photo.ContentHashSha256));command.Parameters.AddWithValue("$created",photo.CreatedAtUtc==DateTime.MinValue?(object)DBNull.Value:photo.CreatedAtUtc.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$assetStatus",string.IsNullOrWhiteSpace(photo.AssetStatus)?"Ready":photo.AssetStatus);
                command.ExecuteNonQuery();
            }
            using(var delete=connection.CreateCommand()){delete.Transaction=transaction;delete.CommandText="DELETE FROM CaptureAssetSources WHERE AssetId=$id";delete.Parameters.AddWithValue("$id",photo.Id);delete.ExecuteNonQuery();}
            foreach(var sourceId in photo.SourceAssetIds??new string[0])using(var source=connection.CreateCommand()){source.Transaction=transaction;source.CommandText="INSERT INTO CaptureAssetSources(AssetId,SourceAssetId) VALUES($asset,$source)";source.Parameters.AddWithValue("$asset",photo.Id);source.Parameters.AddWithValue("$source",sourceId);source.ExecuteNonQuery();}
        }

        static object Db(string value) => string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        static string NormalizeType(string type,string path){if(!string.Equals(type,"Original",StringComparison.OrdinalIgnoreCase))return type;return !string.IsNullOrWhiteSpace(path)&&path.EndsWith("_MP.jpg",StringComparison.OrdinalIgnoreCase)?CaptureAssetTypes.MotionPhoto:CaptureAssetTypes.Picture;}
        static object Date(DateTime? value) => value.HasValue ? (object)value.Value.ToUniversalTime().ToString("O") : DBNull.Value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime Parse(string value) => DateTime.Parse(value).ToUniversalTime();
        static DateTime? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? (DateTime?)null : Parse(reader.GetString(index));
    }
}
