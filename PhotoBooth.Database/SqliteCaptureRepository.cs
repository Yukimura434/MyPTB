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
                    command.CommandText = @"INSERT INTO Captures (Id,SessionId,FrameId,CompositeImageId,CompositePath,LocalSharePath,Status,UploadAttempts,CreatedAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError)
VALUES($id,$session,$frame,$compositeId,$path,$sharePath,$status,$attempts,$created,$uploaded,$expires,$error)
ON CONFLICT(Id) DO UPDATE SET FrameId=excluded.FrameId,CompositeImageId=excluded.CompositeImageId,CompositePath=excluded.CompositePath,LocalSharePath=excluded.LocalSharePath,Status=excluded.Status,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,ExpiresAtUtc=excluded.ExpiresAtUtc,LastError=excluded.LastError";
                    command.Parameters.AddWithValue("$id", capture.Id);
                    command.Parameters.AddWithValue("$session", capture.SessionId.ToString());
                    command.Parameters.AddWithValue("$frame", capture.FrameId.HasValue ? (object)capture.FrameId.Value.ToString() : DBNull.Value);
                    command.Parameters.AddWithValue("$compositeId", Db(capture.CompositeImageId));
                    command.Parameters.AddWithValue("$path", capture.CompositePath);
                    command.Parameters.AddWithValue("$sharePath", Db(capture.SharePath));
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
            command.CommandText = "SELECT Id,SessionId,FrameId,CompositeImageId,CompositePath,LocalSharePath,Status,UploadAttempts,CreatedAtUtc,UploadedAtUtc,ExpiresAtUtc,LastError FROM Captures " + suffix;
            return command;
        }

        static PhotoCapture ReadCapture(SqliteDataReader reader) => new PhotoCapture
        {
            Id = reader.GetString(0), SessionId = Guid.Parse(reader.GetString(1)), FrameId = reader.IsDBNull(2) ? (Guid?)null : Guid.Parse(reader.GetString(2)),
            CompositeImageId = Text(reader, 3), CompositePath = reader.GetString(4), SharePath = Text(reader, 5), Status = reader.GetString(6), UploadAttempts = reader.GetInt32(7),
            CreatedAtUtc = Parse(reader.GetString(8)), UploadedAtUtc = NullableDate(reader, 9), ExpiresAtUtc = NullableDate(reader, 10), LastError = Text(reader, 11)
        };

        static IReadOnlyList<CapturePhoto> LoadPhotos(SqliteConnection connection, string captureId)
        {
            var photos = new List<CapturePhoto>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,CloudinaryPublicId,UploadAttempts,UploadedAtUtc,LastError,IsUploaded FROM CapturePhotos WHERE CaptureId=$capture ORDER BY CASE PhotoType WHEN 'Original' THEN 0 ELSE 1 END,Position";
                command.Parameters.AddWithValue("$capture", captureId);
                using (var reader = command.ExecuteReader()) while (reader.Read()) photos.Add(new CapturePhoto
                {
                    Id=reader.GetString(0), CaptureId=reader.GetString(1), CapturedImageId=Text(reader,2), LocalPath=reader.GetString(3), PhotoType=reader.GetString(4), Position=reader.GetInt32(5), CloudinaryPublicId=Text(reader,6), UploadAttempts=reader.GetInt32(7), UploadedAtUtc=NullableDate(reader,8), LastError=Text(reader,9), IsUploaded=reader.GetInt32(10)!=0
                });
            }
            return photos;
        }

        static void SavePhoto(SqliteConnection connection, SqliteTransaction transaction, string captureId, CapturePhoto photo)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO CapturePhotos (Id,CaptureId,CapturedImageId,LocalPath,PhotoType,Position,CloudinaryPublicId,UploadAttempts,UploadedAtUtc,LastError,IsUploaded)
VALUES($id,$capture,$image,$path,$type,$position,$publicId,$attempts,$uploaded,$error,$isUploaded)
ON CONFLICT(Id) DO UPDATE SET CapturedImageId=excluded.CapturedImageId,LocalPath=excluded.LocalPath,PhotoType=excluded.PhotoType,Position=excluded.Position,CloudinaryPublicId=excluded.CloudinaryPublicId,UploadAttempts=excluded.UploadAttempts,UploadedAtUtc=excluded.UploadedAtUtc,LastError=excluded.LastError,IsUploaded=excluded.IsUploaded";
                command.Parameters.AddWithValue("$id", photo.Id);
                command.Parameters.AddWithValue("$capture", captureId);
                command.Parameters.AddWithValue("$image", Db(photo.CapturedImageId));
                command.Parameters.AddWithValue("$path", photo.LocalPath);
                command.Parameters.AddWithValue("$type", photo.PhotoType);
                command.Parameters.AddWithValue("$position", photo.Position);
                command.Parameters.AddWithValue("$publicId", Db(photo.CloudinaryPublicId));
                command.Parameters.AddWithValue("$attempts", photo.UploadAttempts);
                command.Parameters.AddWithValue("$uploaded", Date(photo.UploadedAtUtc));
                command.Parameters.AddWithValue("$error", Db(photo.LastError));
                command.Parameters.AddWithValue("$isUploaded", photo.IsUploaded ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        static object Db(string value) => string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        static object Date(DateTime? value) => value.HasValue ? (object)value.Value.ToUniversalTime().ToString("O") : DBNull.Value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime Parse(string value) => DateTime.Parse(value).ToUniversalTime();
        static DateTime? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? (DateTime?)null : Parse(reader.GetString(index));
    }
}
