using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePhotoEventConfigurationRepository : IPhotoEventConfigurationRepository
    {
        readonly SqliteDatabase db;
        public SqlitePhotoEventConfigurationRepository(SqliteDatabase database) { db = database; }

        public Task<PhotoEventConfiguration> GetAsync(Guid eventId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            {
                PhotoEventConfiguration value;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,
 CustomerLayoutMode,ImageRotationDegrees,BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,
 ModifiedAtUtc,RowVersion FROM EventConfigurations WHERE EventId=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return Task.FromResult<PhotoEventConfiguration>(null);
                        value = new PhotoEventConfiguration
                        {
                            EventId = eventId,
                            PhotoCount = reader.GetInt32(0),
                            CountdownSeconds = reader.GetInt32(1),
                            GifFrameDurationMilliseconds = reader.GetInt32(2),
                            WaitingTimeoutSeconds = reader.GetInt32(3),
                            CustomerLayoutMode = reader.GetInt32(4) == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                            ImageRotationDegrees = reader.GetInt32(5),
                            Beauty = new BeautySettings
                            {
                                Enabled = reader.GetInt32(6) != 0,
                                SmoothSkin = reader.GetInt32(7),
                                BrightenSkin = reader.GetInt32(8),
                                SkinTone = reader.GetInt32(9),
                                Sharpen = reader.GetInt32(10),
                                EyeSize = reader.GetInt32(11),
                                SlimFace = reader.GetInt32(12)
                            },
                            ModifiedAtUtc = DateTime.Parse(reader.GetString(13)).ToUniversalTime(),
                            RowVersion = reader.GetInt64(14)
                        };
                    }
                }
                var frames = new List<Guid>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT FrameId FROM EventFrames WHERE EventId=$event ORDER BY SortOrder";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        frames.Add(Guid.Parse(reader.GetString(0)));
                    }
                }
                value.FrameIds = frames;
                return Task.FromResult(value);
            }
        }

        public Task<PhotoEventConfiguration> SaveAsync(string eventName, PhotoEventConfiguration value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value == null) throw new ArgumentNullException(nameof(value));
            var frameIds = (value.FrameIds ?? new Guid[0]).Distinct().ToList();
            if (frameIds.Count == 0 || frameIds.Count > 10) throw new InvalidOperationException("Event phải có từ 1 đến 10 frame.");
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                EnsureEvent(connection, transaction, value.EventId);
                foreach (var frameId in frameIds) EnsureFrame(connection, transaction, frameId);
                var now = DateTime.UtcNow;
                if (value.RowVersion == 0)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO EventConfigurations
(EventId,PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,CustomerLayoutMode,ImageRotationDegrees,
 BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,ModifiedAtUtc,RowVersion)
VALUES($event,$photos,$countdown,$gif,$waiting,$layout,$rotation,$enabled,$smooth,$brighten,$tone,$sharpen,$eye,$slim,$modified,1)";
                        BindConfiguration(command, value, now);
                        try { command.ExecuteNonQuery(); }
                        catch (SqliteException error) when (error.SqliteErrorCode == 19)
                        { throw new InvalidOperationException("Event đã được cập nhật ở nơi khác. Hãy tải lại trước khi lưu.", error); }
                    }
                    value.RowVersion = 1;
                }
                else
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"UPDATE EventConfigurations SET
 PhotoCount=$photos,CountdownSeconds=$countdown,GifFrameDurationMs=$gif,WaitingTimeoutSeconds=$waiting,
 CustomerLayoutMode=$layout,ImageRotationDegrees=$rotation,BeautyEnabled=$enabled,SmoothSkin=$smooth,
 BrightenSkin=$brighten,SkinTone=$tone,Sharpen=$sharpen,EyeSize=$eye,SlimFace=$slim,
 ModifiedAtUtc=$modified,RowVersion=RowVersion+1 WHERE EventId=$event AND RowVersion=$version";
                        BindConfiguration(command, value, now);
                        command.Parameters.AddWithValue("$version", value.RowVersion);
                        if (command.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("Event đã được cập nhật ở nơi khác. Hãy tải lại trước khi lưu.");
                    }
                    value.RowVersion++;
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE CustomerSessions SET SessionName=$name,UpdatedAtUtc=$modified WHERE Id=$event AND Kind='Event'";
                    command.Parameters.AddWithValue("$name", eventName);
                    command.Parameters.AddWithValue("$modified", now.ToString("O"));
                    command.Parameters.AddWithValue("$event", value.EventId.ToString());
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Không tìm thấy event.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM EventFrames WHERE EventId=$event";
                    command.Parameters.AddWithValue("$event", value.EventId.ToString());
                    command.ExecuteNonQuery();
                }
                for (var index = 0; index < frameIds.Count; index++)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO EventFrames(EventId,FrameId,SortOrder) VALUES($event,$frame,$sort)";
                        command.Parameters.AddWithValue("$event", value.EventId.ToString());
                        command.Parameters.AddWithValue("$frame", frameIds[index].ToString());
                        command.Parameters.AddWithValue("$sort", index);
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
                value.FrameIds = frameIds;
                value.ModifiedAtUtc = now;
                return Task.FromResult(value);
            }
        }

        public Task ActivateAsync(Guid eventId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                EnsureEvent(connection, transaction, eventId);
                var configuration = ReadConfiguration(connection, transaction, eventId);
                var frameIds = ReadFrameIds(connection, transaction, eventId);
                if (configuration == null) throw new InvalidOperationException("Hãy lưu cấu hình event trước khi sử dụng.");
                if (frameIds.Count == 0 || frameIds.Count > 10) throw new InvalidOperationException("Event phải có từ 1 đến 10 frame hợp lệ.");
                foreach (var frameId in frameIds) EnsureFrame(connection, transaction, frameId);
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE CustomerSessions SET IsDefault=CASE WHEN Id=$event THEN 1 ELSE 0 END WHERE Kind='Event'";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE Frames SET IsPinned=CASE WHEN Id IN (SELECT FrameId FROM EventFrames WHERE EventId=$event) THEN 1 ELSE 0 END";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT OR IGNORE INTO WorkflowSettings
(Id,Culture,CaptureDirectory,AlphaThreshold,MinimumSlotArea,MinimumSlotWidth,MinimumSlotHeight,IgnoreBorder,MaximumSlots,
 PhotoCount,CountdownSeconds,DefaultFrameId,DefaultPresetId,DefaultPrinterProfileId,KioskMode,KeepFinal,DelayBetweenShots,
 AutoFlip,SaveLocation,GifFrameDurationMs,WaitingTimeoutSeconds,ShowWaitingLiveView,WaitingLiveViewX,WaitingLiveViewY,
 RotateLiveView180,CustomerLayoutMode,ImageRotationDegrees,WaitingBackgroundZoom,WaitingBackgroundPanX,WaitingBackgroundPanY,WaitingLiveViewAreaPercent)
VALUES(1,NULL,NULL,8,10000,40,40,1,8,$photos,$countdown,$frame,NULL,NULL,1,1,1,0,0,$gif,$waiting,1,10,10,0,$layout,$rotation,100,0,0,5);
UPDATE WorkflowSettings SET PhotoCount=$photos,CountdownSeconds=$countdown,GifFrameDurationMs=$gif,
 WaitingTimeoutSeconds=$waiting,CustomerLayoutMode=$layout,ImageRotationDegrees=$rotation,DefaultFrameId=$frame WHERE Id=1";
                    command.Parameters.AddWithValue("$photos", configuration.PhotoCount);
                    command.Parameters.AddWithValue("$countdown", configuration.CountdownSeconds);
                    command.Parameters.AddWithValue("$gif", configuration.GifFrameDurationMilliseconds);
                    command.Parameters.AddWithValue("$waiting", configuration.WaitingTimeoutSeconds);
                    command.Parameters.AddWithValue("$layout", (int)configuration.CustomerLayoutMode);
                    command.Parameters.AddWithValue("$rotation", configuration.ImageRotationDegrees);
                    command.Parameters.AddWithValue("$frame", frameIds[0].ToString());
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT OR REPLACE INTO BeautySettings
(Id,Enabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,ModifiedAtUtc)
VALUES(1,$enabled,$smooth,$brighten,$tone,$sharpen,$eye,$slim,$modified)";
                    BindBeauty(command, configuration.Beauty);
                    command.Parameters.AddWithValue("$modified", DateTime.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                return Task.CompletedTask;
            }
        }

        public Task DeleteAsync(Guid eventId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                EnsureEvent(connection, transaction, eventId);
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT IsDefault FROM CustomerSessions WHERE Id=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Không thể xóa event đang được sử dụng. Hãy kích hoạt event khác trước.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT COUNT(*) FROM CustomerSessions WHERE Kind='Booth' AND EventId=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Không thể xóa event đã có lượt chụp vì sẽ làm mất liên kết dữ liệu.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM CustomerSessions WHERE Id=$event AND Kind='Event'";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Không tìm thấy event.");
                }
                transaction.Commit();
                return Task.CompletedTask;
            }
        }

        static void EnsureEvent(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM CustomerSessions WHERE Id=$event AND Kind='Event'";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("Không tìm thấy event.");
            }
        }

        static void EnsureFrame(SqliteConnection connection, SqliteTransaction transaction, Guid frameId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM Frames WHERE Id=$frame";
                command.Parameters.AddWithValue("$frame", frameId.ToString());
                if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("Một frame đã chọn không còn tồn tại.");
            }
        }

        static PhotoEventConfiguration ReadConfiguration(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,CustomerLayoutMode,
 ImageRotationDegrees,BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace FROM EventConfigurations WHERE EventId=$event";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new PhotoEventConfiguration
                    {
                        EventId = eventId, PhotoCount = reader.GetInt32(0), CountdownSeconds = reader.GetInt32(1),
                        GifFrameDurationMilliseconds = reader.GetInt32(2), WaitingTimeoutSeconds = reader.GetInt32(3),
                        CustomerLayoutMode = reader.GetInt32(4) == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                        ImageRotationDegrees = reader.GetInt32(5),
                        Beauty = new BeautySettings { Enabled=reader.GetInt32(6)!=0,SmoothSkin=reader.GetInt32(7),BrightenSkin=reader.GetInt32(8),SkinTone=reader.GetInt32(9),Sharpen=reader.GetInt32(10),EyeSize=reader.GetInt32(11),SlimFace=reader.GetInt32(12) }
                    };
                }
            }
        }

        static List<Guid> ReadFrameIds(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            var values = new List<Guid>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT FrameId FROM EventFrames WHERE EventId=$event ORDER BY SortOrder";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(Guid.Parse(reader.GetString(0)));
            }
            return values;
        }

        static void BindConfiguration(SqliteCommand command, PhotoEventConfiguration value, DateTime modified)
        {
            command.Parameters.AddWithValue("$event", value.EventId.ToString());
            command.Parameters.AddWithValue("$photos", value.PhotoCount);
            command.Parameters.AddWithValue("$countdown", value.CountdownSeconds);
            command.Parameters.AddWithValue("$gif", value.GifFrameDurationMilliseconds);
            command.Parameters.AddWithValue("$waiting", value.WaitingTimeoutSeconds);
            command.Parameters.AddWithValue("$layout", (int)value.CustomerLayoutMode);
            command.Parameters.AddWithValue("$rotation", value.ImageRotationDegrees);
            BindBeauty(command, value.Beauty);
            command.Parameters.AddWithValue("$modified", modified.ToString("O"));
        }

        static void BindBeauty(SqliteCommand command, BeautySettings beauty)
        {
            beauty = beauty ?? new BeautySettings();
            command.Parameters.AddWithValue("$enabled", beauty.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$smooth", beauty.SmoothSkin);
            command.Parameters.AddWithValue("$brighten", beauty.BrightenSkin);
            command.Parameters.AddWithValue("$tone", beauty.SkinTone);
            command.Parameters.AddWithValue("$sharpen", beauty.Sharpen);
            command.Parameters.AddWithValue("$eye", beauty.EyeSize);
            command.Parameters.AddWithValue("$slim", beauty.SlimFace);
        }
    }
}
