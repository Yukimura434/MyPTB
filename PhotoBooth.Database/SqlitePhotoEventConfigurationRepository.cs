using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
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
                    command.CommandText = @"SELECT BasePriceMinor,AdditionalPrintPriceMinor,Currency,PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,
 CustomerLayoutMode,ImageRotationDegrees,BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,BlemishRemoval,DarkCircleReduction,
 PrintPolicyMode,IncludedPrintCopies,MaximumPrintCopies,ColorPresetStrengthPercent,ModifiedAtUtc,RowVersion FROM EventConfigurations WHERE EventId=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return Task.FromResult<PhotoEventConfiguration>(null);
                        value = new PhotoEventConfiguration
                        {
                            EventId = eventId,
                            BasePriceMinor = reader.GetInt64(0), AdditionalPrintPriceMinor = reader.GetInt64(1), Currency = reader.GetString(2),
                            PhotoCount = reader.GetInt32(3),
                            CountdownSeconds = reader.GetInt32(4),
                            GifFrameDurationMilliseconds = reader.GetInt32(5),
                            WaitingTimeoutSeconds = reader.GetInt32(6),
                            CustomerLayoutMode = reader.GetInt32(7) == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                            ImageRotationDegrees = reader.GetInt32(8),
                            Beauty = new BeautySettings
                            {
                                Enabled = reader.GetInt32(9) != 0,
                                SmoothSkin = reader.GetInt32(10),
                                BrightenSkin = reader.GetInt32(11),
                                SkinTone = reader.GetInt32(12),
                                Sharpen = reader.GetInt32(13),
                                EyeSize = reader.GetInt32(14),
                                SlimFace = reader.GetInt32(15),BlemishRemoval=reader.GetInt32(16),DarkCircleReduction=reader.GetInt32(17)
                            },
                            PrintPolicyMode = ParsePrintPolicyMode(reader.GetString(18)),
                            IncludedPrintCopies = reader.GetInt32(19),
                            MaximumPrintCopies = reader.GetInt32(20),
                            ColorPresetStrengthPercent = reader.GetInt32(21),
                            ModifiedAtUtc = DateTime.Parse(reader.GetString(22)).ToUniversalTime(),
                            RowVersion = reader.GetInt64(23)
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
                var presets = new List<Guid>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT PresetId FROM EventPresets WHERE EventId=$event ORDER BY SortOrder";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        presets.Add(Guid.Parse(reader.GetString(0)));
                    }
                }
                value.PresetIds = presets;
                return Task.FromResult(value);
            }
        }

        public Task<PhotoEventConfiguration> GetActiveAsync(Guid eventId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ConfigurationJson,VersionNumber FROM ConfigurationVersions
WHERE EventId=$event AND State='Active' AND ContentHashSha256<>$bootstrap
ORDER BY VersionNumber DESC LIMIT 1";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                command.Parameters.AddWithValue("$bootstrap", new string('0', 64));
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<PhotoEventConfiguration>(null);
                    var payload = ReadActivePayload(reader.GetString(0));
                    var frameIds = ParseIds(payload.FrameIds);
                    if (frameIds.Count == 0) return Task.FromResult<PhotoEventConfiguration>(null);
                    return Task.FromResult(new PhotoEventConfiguration
                    {
                        EventId = eventId,
                        BasePriceMinor = Math.Max(0, payload.BasePriceMinor),
                        AdditionalPrintPriceMinor = Math.Max(0, payload.AdditionalPrintPriceMinor),
                        Currency = string.IsNullOrWhiteSpace(payload.Currency) ? "VND" : payload.Currency,
                        PhotoCount = payload.PhotoCount,
                        CountdownSeconds = payload.CountdownSeconds,
                        GifFrameDurationMilliseconds = payload.GifFrameDurationMilliseconds,
                        WaitingTimeoutSeconds = payload.WaitingTimeoutSeconds,
                        CustomerLayoutMode = payload.CustomerLayoutMode == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                        ImageRotationDegrees = payload.ImageRotationDegrees,
                        Beauty = payload.Beauty == null ? new BeautySettings() : new BeautySettings
                        {
                            Enabled = payload.Beauty.Enabled,
                            SmoothSkin = payload.Beauty.SmoothSkin,
                            BrightenSkin = payload.Beauty.BrightenSkin,
                            SkinTone = payload.Beauty.SkinTone,
                            Sharpen = payload.Beauty.Sharpen,
                            EyeSize = payload.Beauty.EyeSize,
                            SlimFace = payload.Beauty.SlimFace,
                            BlemishRemoval = payload.Beauty.BlemishRemoval,
                            DarkCircleReduction = payload.Beauty.DarkCircleReduction
                        },
                        PrintPolicyMode = ParsePrintPolicyMode(payload.PrintPolicy?.Mode),
                        IncludedPrintCopies = payload.PrintPolicy == null ? 1 : Math.Max(0,Math.Min(10,payload.PrintPolicy.IncludedCopies)),
                        MaximumPrintCopies = payload.PrintPolicy == null ? 10 : Math.Max(0,Math.Min(20,payload.PrintPolicy.MaximumCopies)),
                        ColorPresetStrengthPercent = Math.Max(0,Math.Min(100,payload.ColorPresetStrengthPercent ?? 50)),
                        FrameIds = frameIds,
                        PresetIds = ParseIds(payload.PresetIds),
                        RowVersion = reader.GetInt64(1)
                    });
                }
            }
        }

        public Task<PhotoEventConfiguration> SaveAsync(string eventName, PhotoEventConfiguration value, CancellationToken token)
            => SaveAsync(eventName, null, value, token);

        public Task<PhotoEventConfiguration> SaveAsync(string eventName, string outputDirectory, PhotoEventConfiguration value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value == null) throw new ArgumentNullException(nameof(value));
            var frameIds = (value.FrameIds ?? new Guid[0]).Distinct().ToList();
            var presetIds = (value.PresetIds ?? new Guid[0]).Distinct().ToList();
            if (frameIds.Count == 0) throw new InvalidOperationException("Event phải có ít nhất 1 frame.");
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                EnsureEvent(connection, transaction, value.EventId);
                foreach (var frameId in frameIds) EnsureFrame(connection, transaction, frameId);
                foreach (var presetId in presetIds) EnsurePreset(connection, transaction, presetId);
                var now = DateTime.UtcNow;
                if (value.RowVersion == 0)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO EventConfigurations
(EventId,PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,CustomerLayoutMode,ImageRotationDegrees,
 BasePriceMinor,AdditionalPrintPriceMinor,Currency,BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,BlemishRemoval,DarkCircleReduction,
 PrintPolicyMode,IncludedPrintCopies,MaximumPrintCopies,ColorPresetStrengthPercent,ModifiedAtUtc,RowVersion)
VALUES($event,$photos,$countdown,$gif,$waiting,$layout,$rotation,$basePrice,$additionalPrintPrice,$currency,$enabled,$smooth,$brighten,$tone,$sharpen,$eye,$slim,$blemish,$darkCircle,$printMode,$includedCopies,$maximumCopies,$presetStrength,$modified,1)";
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
 CustomerLayoutMode=$layout,ImageRotationDegrees=$rotation,BasePriceMinor=$basePrice,AdditionalPrintPriceMinor=$additionalPrintPrice,Currency=$currency,BeautyEnabled=$enabled,SmoothSkin=$smooth,
 BrightenSkin=$brighten,SkinTone=$tone,Sharpen=$sharpen,EyeSize=$eye,SlimFace=$slim,BlemishRemoval=$blemish,DarkCircleReduction=$darkCircle,
 PrintPolicyMode=$printMode,IncludedPrintCopies=$includedCopies,MaximumPrintCopies=$maximumCopies,ColorPresetStrengthPercent=$presetStrength,
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
                    command.CommandText = @"UPDATE Events SET Name=$name,
OutputDirectory=CASE WHEN $path IS NULL THEN OutputDirectory ELSE $path END,
UpdatedAtUtc=$modified,RowVersion=RowVersion+1 WHERE Id=$event";
                    command.Parameters.AddWithValue("$name", eventName);
                    command.Parameters.AddWithValue("$path", string.IsNullOrWhiteSpace(outputDirectory) ? (object)DBNull.Value : Path.GetFullPath(outputDirectory));
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
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM EventPresets WHERE EventId=$event";
                    command.Parameters.AddWithValue("$event", value.EventId.ToString());
                    command.ExecuteNonQuery();
                }
                for (var index = 0; index < presetIds.Count; index++)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO EventPresets(EventId,PresetId,SortOrder) VALUES($event,$preset,$sort)";
                        command.Parameters.AddWithValue("$event", value.EventId.ToString());
                        command.Parameters.AddWithValue("$preset", presetIds[index].ToString());
                        command.Parameters.AddWithValue("$sort", index);
                        command.ExecuteNonQuery();
                    }
                }
                SaveReadyVersion(connection,transaction,value,frameIds,presetIds,now);
                transaction.Commit();
                value.FrameIds = frameIds;
                value.PresetIds = presetIds;
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
                var presetIds = ReadPresetIds(connection, transaction, eventId);
                if (configuration == null) throw new InvalidOperationException("Hãy lưu cấu hình event trước khi sử dụng.");
                if (frameIds.Count == 0) throw new InvalidOperationException("Event phải có ít nhất 1 frame hợp lệ.");
                foreach (var frameId in frameIds) EnsureFrame(connection, transaction, frameId);
                foreach (var presetId in presetIds) EnsurePreset(connection, transaction, presetId);
                var activatedAtUtc = DateTime.UtcNow.ToString("O");
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT LifecycleState FROM Events WHERE Id=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    var lifecycle=command.ExecuteScalar() as string;
                    if(!string.Equals(lifecycle,PhotoEventLifecycleStates.Open,StringComparison.Ordinal))throw new InvalidOperationException("Chỉ sự kiện đang mở mới có thể được chọn để vận hành.");
                }
                ActivateLatestVersion(connection,transaction,eventId);
                string activeConfigurationId;
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText="SELECT Id FROM ConfigurationVersions WHERE EventId=$event AND State='Active' ORDER BY VersionNumber DESC LIMIT 1";command.Parameters.AddWithValue("$event",eventId.ToString());activeConfigurationId=command.ExecuteScalar() as string;
                }
                if(string.IsNullOrWhiteSpace(activeConfigurationId))throw new InvalidOperationException("Sự kiện chưa có phiên bản cấu hình sẵn sàng.");
                using(var command=connection.CreateCommand())
                {
                    command.Transaction=transaction;command.CommandText=@"INSERT INTO DeviceRuntimeState(Id,SelectedEventId,SelectedConfigurationVersionId,SelectedAtUtc,UpdatedAtUtc)
VALUES(1,$event,$configuration,$now,$now)
ON CONFLICT(Id) DO UPDATE SET SelectedEventId=excluded.SelectedEventId,SelectedConfigurationVersionId=excluded.SelectedConfigurationVersionId,SelectedAtUtc=excluded.SelectedAtUtc,UpdatedAtUtc=excluded.UpdatedAtUtc";
                    command.Parameters.AddWithValue("$event",eventId.ToString());command.Parameters.AddWithValue("$configuration",activeConfigurationId);command.Parameters.AddWithValue("$now",activatedAtUtc);command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE AdminPresets SET IsPinned=CASE WHEN Id IN (SELECT PresetId FROM EventPresets WHERE EventId=$event) THEN 1 ELSE 0 END";
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
 WaitingTimeoutSeconds=$waiting,CustomerLayoutMode=$layout,ImageRotationDegrees=$rotation,DefaultFrameId=$frame,DefaultPresetId=$preset,PrintingEnabled=$printing WHERE Id=1";
                    command.Parameters.AddWithValue("$photos", configuration.PhotoCount);
                    command.Parameters.AddWithValue("$countdown", configuration.CountdownSeconds);
                    command.Parameters.AddWithValue("$gif", configuration.GifFrameDurationMilliseconds);
                    command.Parameters.AddWithValue("$waiting", configuration.WaitingTimeoutSeconds);
                    command.Parameters.AddWithValue("$layout", (int)configuration.CustomerLayoutMode);
                    command.Parameters.AddWithValue("$rotation", configuration.ImageRotationDegrees);
                    command.Parameters.AddWithValue("$frame", frameIds[0].ToString());
                    command.Parameters.AddWithValue("$preset", presetIds.Count == 0 ? (object)DBNull.Value : presetIds[0].ToString());
                    command.Parameters.AddWithValue("$printing",configuration.PrintPolicyMode==EventPrintPolicyMode.Disabled?0:1);
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT OR REPLACE INTO BeautySettings
(Id,Enabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,BlemishRemoval,DarkCircleReduction,ModifiedAtUtc)
VALUES(1,$enabled,$smooth,$brighten,$tone,$sharpen,$eye,$slim,$blemish,$darkCircle,$modified)";
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
                    command.CommandText = "SELECT SessionNumber FROM Events WHERE Id=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                        throw new InvalidOperationException("Sự kiện mặc định luôn được giữ lại và không thể xóa.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT COUNT(*) FROM DeviceRuntimeState WHERE Id=1 AND SelectedEventId=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Không thể xóa sự kiện đang được dùng trên máy này. Hãy chọn sự kiện khác trước.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT COUNT(*) FROM BoothSessions WHERE EventId=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException("Không thể xóa event đã có lượt chụp vì sẽ làm mất liên kết dữ liệu.");
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM Events WHERE Id=$event";
                    command.Parameters.AddWithValue("$event", eventId.ToString());
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Không tìm thấy event.");
                }
                transaction.Commit();
                return Task.CompletedTask;
            }
        }

        static void SaveReadyVersion(SqliteConnection connection,SqliteTransaction transaction,PhotoEventConfiguration value,IReadOnlyList<Guid> frameIds,IReadOnlyList<Guid> presetIds,DateTime now)
        {
            var beauty=value.Beauty??new BeautySettings();
            var payload="{\"basePriceMinor\":"+value.BasePriceMinor+",\"additionalPrintPriceMinor\":"+value.AdditionalPrintPriceMinor+",\"currency\":\""+(string.IsNullOrWhiteSpace(value.Currency)?"VND":value.Currency)+"\",\"photoCount\":"+value.PhotoCount+",\"countdownSeconds\":"+value.CountdownSeconds+",\"gifFrameDurationMs\":"+value.GifFrameDurationMilliseconds+",\"waitingTimeoutSeconds\":"+value.WaitingTimeoutSeconds+",\"customerLayoutMode\":"+((int)value.CustomerLayoutMode)+",\"imageRotationDegrees\":"+value.ImageRotationDegrees+",\"beauty\":{\"enabled\":"+(beauty.Enabled?"true":"false")+",\"smoothSkin\":"+beauty.SmoothSkin+",\"brightenSkin\":"+beauty.BrightenSkin+",\"skinTone\":"+beauty.SkinTone+",\"sharpen\":"+beauty.Sharpen+",\"eyeSize\":"+beauty.EyeSize+",\"slimFace\":"+beauty.SlimFace+",\"blemishRemoval\":"+beauty.BlemishRemoval+",\"darkCircleReduction\":"+beauty.DarkCircleReduction+"},\"frameIds\":["+string.Join(",",frameIds.Select(x=>"\""+x.ToString()+"\""))+"],\"presetIds\":["+string.Join(",",presetIds.Select(x=>"\""+x.ToString()+"\""))+"]}";
            var printMode=value.PrintPolicyMode.ToString().ToLowerInvariant();
            payload=payload.Substring(0,payload.Length-1)+",\"schemaVersion\":2,\"printPolicy\":{\"mode\":\""+printMode+"\",\"includedCopies\":"+value.IncludedPrintCopies+",\"maxCopies\":"+value.MaximumPrintCopies+"},\"colorPresetStrengthPercent\":"+value.ColorPresetStrengthPercent+"}";
            string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-",string.Empty).ToLowerInvariant();
            string existingId=null;string existingState=null;
            using(var existing=connection.CreateCommand())
            {
                existing.Transaction=transaction;existing.CommandText="SELECT Id,State FROM ConfigurationVersions WHERE EventId=$event AND ContentHashSha256=$hash LIMIT 1";existing.Parameters.AddWithValue("$event",value.EventId.ToString());existing.Parameters.AddWithValue("$hash",hash);
                using(var reader=existing.ExecuteReader())if(reader.Read()){existingId=reader.GetString(0);existingState=reader.GetString(1);}
            }
            using(var retire=connection.CreateCommand()){retire.Transaction=transaction;retire.CommandText="UPDATE ConfigurationVersions SET State='Retired' WHERE EventId=$event AND State='Ready' AND Id<>COALESCE($existing,'')";retire.Parameters.AddWithValue("$event",value.EventId.ToString());retire.Parameters.AddWithValue("$existing",(object)existingId??DBNull.Value);retire.ExecuteNonQuery();}
            if(existingId!=null)
            {
                if(existingState!="Active")using(var ready=connection.CreateCommand()){ready.Transaction=transaction;ready.CommandText="UPDATE ConfigurationVersions SET State='Ready',ActivatedAtUtc=NULL WHERE Id=$id";ready.Parameters.AddWithValue("$id",existingId);ready.ExecuteNonQuery();}
                return;
            }
            int version;using(var read=connection.CreateCommand()){read.Transaction=transaction;read.CommandText="SELECT COALESCE(MAX(VersionNumber),0)+1 FROM ConfigurationVersions WHERE EventId=$event";read.Parameters.AddWithValue("$event",value.EventId.ToString());version=Convert.ToInt32(read.ExecuteScalar());}
            using(var insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="INSERT INTO ConfigurationVersions(Id,EventId,VersionNumber,ConfigurationJson,ContentHashSha256,State,CreatedAtUtc) VALUES($id,$event,$version,$payload,$hash,'Ready',$now)";insert.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));insert.Parameters.AddWithValue("$event",value.EventId.ToString());insert.Parameters.AddWithValue("$version",version);insert.Parameters.AddWithValue("$payload",payload);insert.Parameters.AddWithValue("$hash",hash);insert.Parameters.AddWithValue("$now",now.ToString("O"));insert.ExecuteNonQuery();}
        }

        static void ActivateLatestVersion(SqliteConnection connection,SqliteTransaction transaction,Guid eventId)
        {
            string id;using(var read=connection.CreateCommand()){read.Transaction=transaction;read.CommandText="SELECT Id FROM ConfigurationVersions WHERE EventId=$event AND State='Ready' ORDER BY VersionNumber DESC LIMIT 1";read.Parameters.AddWithValue("$event",eventId.ToString());id=read.ExecuteScalar() as string;}
            if(string.IsNullOrWhiteSpace(id))return;
            using(var retire=connection.CreateCommand()){retire.Transaction=transaction;retire.CommandText="UPDATE ConfigurationVersions SET State='Retired' WHERE EventId=$event AND State='Active'";retire.Parameters.AddWithValue("$event",eventId.ToString());retire.ExecuteNonQuery();}
            using(var activate=connection.CreateCommand()){activate.Transaction=transaction;activate.CommandText="UPDATE ConfigurationVersions SET State='Active',ActivatedAtUtc=$now WHERE Id=$id AND State='Ready'";activate.Parameters.AddWithValue("$id",id);activate.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));if(activate.ExecuteNonQuery()!=1)throw new InvalidOperationException("Không thể kích hoạt phiên bản cấu hình event.");}
        }

        static void EnsureEvent(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM Events WHERE Id=$event";
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

        static void EnsurePreset(SqliteConnection connection, SqliteTransaction transaction, Guid presetId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT COUNT(*) FROM AdminPresets preset
INNER JOIN PresetColorSettings color ON color.PresetId=preset.Id WHERE preset.Id=$preset";
                command.Parameters.AddWithValue("$preset", presetId.ToString());
                if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("Một preset đã chọn không còn tồn tại hoặc không có LUT.");
            }
        }

        static PhotoEventConfiguration ReadConfiguration(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT BasePriceMinor,AdditionalPrintPriceMinor,Currency,PhotoCount,CountdownSeconds,GifFrameDurationMs,WaitingTimeoutSeconds,CustomerLayoutMode,
 ImageRotationDegrees,BeautyEnabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,BlemishRemoval,DarkCircleReduction,
 PrintPolicyMode,IncludedPrintCopies,MaximumPrintCopies,ColorPresetStrengthPercent FROM EventConfigurations WHERE EventId=$event";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new PhotoEventConfiguration
                    {
                        EventId = eventId, BasePriceMinor=reader.GetInt64(0),AdditionalPrintPriceMinor=reader.GetInt64(1),Currency=reader.GetString(2),PhotoCount = reader.GetInt32(3), CountdownSeconds = reader.GetInt32(4),
                        GifFrameDurationMilliseconds = reader.GetInt32(5), WaitingTimeoutSeconds = reader.GetInt32(6),
                        CustomerLayoutMode = reader.GetInt32(7) == 1 ? CustomerLayoutMode.Portrait : CustomerLayoutMode.Landscape,
                        ImageRotationDegrees = reader.GetInt32(8),
                        Beauty = new BeautySettings { Enabled=reader.GetInt32(9)!=0,SmoothSkin=reader.GetInt32(10),BrightenSkin=reader.GetInt32(11),SkinTone=reader.GetInt32(12),Sharpen=reader.GetInt32(13),EyeSize=reader.GetInt32(14),SlimFace=reader.GetInt32(15),BlemishRemoval=reader.GetInt32(16),DarkCircleReduction=reader.GetInt32(17) },
                        PrintPolicyMode=ParsePrintPolicyMode(reader.GetString(18)),IncludedPrintCopies=reader.GetInt32(19),MaximumPrintCopies=reader.GetInt32(20),ColorPresetStrengthPercent=reader.GetInt32(21)
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

        static List<Guid> ReadPresetIds(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
        {
            var values = new List<Guid>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT PresetId FROM EventPresets WHERE EventId=$event ORDER BY SortOrder";
                command.Parameters.AddWithValue("$event", eventId.ToString());
                using (var reader = command.ExecuteReader()) while (reader.Read()) values.Add(Guid.Parse(reader.GetString(0)));
            }
            return values;
        }

        static void BindConfiguration(SqliteCommand command, PhotoEventConfiguration value, DateTime modified)
        {
            command.Parameters.AddWithValue("$event", value.EventId.ToString());
            command.Parameters.AddWithValue("$basePrice", Math.Max(0,value.BasePriceMinor));
            command.Parameters.AddWithValue("$additionalPrintPrice", Math.Max(0,value.AdditionalPrintPriceMinor));
            command.Parameters.AddWithValue("$currency", string.IsNullOrWhiteSpace(value.Currency)?"VND":value.Currency.Trim().ToUpperInvariant());
            command.Parameters.AddWithValue("$photos", value.PhotoCount);
            command.Parameters.AddWithValue("$countdown", value.CountdownSeconds);
            command.Parameters.AddWithValue("$gif", value.GifFrameDurationMilliseconds);
            command.Parameters.AddWithValue("$waiting", value.WaitingTimeoutSeconds);
            command.Parameters.AddWithValue("$layout", (int)value.CustomerLayoutMode);
            command.Parameters.AddWithValue("$rotation", value.ImageRotationDegrees);
            var included=value.PrintPolicyMode==EventPrintPolicyMode.Disabled?0:Math.Max(1,Math.Min(10,value.IncludedPrintCopies));
            var maximum=value.PrintPolicyMode==EventPrintPolicyMode.Disabled?0:Math.Max(included,Math.Min(20,value.MaximumPrintCopies));
            command.Parameters.AddWithValue("$printMode",value.PrintPolicyMode.ToString());
            command.Parameters.AddWithValue("$includedCopies",included);
            command.Parameters.AddWithValue("$maximumCopies",maximum);
            command.Parameters.AddWithValue("$presetStrength",Math.Max(0,Math.Min(100,value.ColorPresetStrengthPercent)));
            BindBeauty(command, value.Beauty);
            command.Parameters.AddWithValue("$modified", modified.ToString("O"));
        }

        static ActiveConfigurationPayload ReadActivePayload(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                return (ActiveConfigurationPayload)new DataContractJsonSerializer(typeof(ActiveConfigurationPayload)).ReadObject(stream);
        }

        static List<Guid> ParseIds(IEnumerable<string> values)
        {
            var result = new List<Guid>();
            foreach (var value in values ?? new string[0])
                if (Guid.TryParse(value, out var id) && id != Guid.Empty && !result.Contains(id)) result.Add(id);
            return result;
        }

        [DataContract]
        sealed class ActiveConfigurationPayload
        {
            [DataMember(Name = "basePriceMinor")] public long BasePriceMinor { get; set; }
            [DataMember(Name = "additionalPrintPriceMinor")] public long AdditionalPrintPriceMinor { get; set; }
            [DataMember(Name = "currency")] public string Currency { get; set; }
            [DataMember(Name = "photoCount")] public int PhotoCount { get; set; }
            [DataMember(Name = "countdownSeconds")] public int CountdownSeconds { get; set; }
            [DataMember(Name = "gifFrameDurationMs")] public int GifFrameDurationMilliseconds { get; set; }
            [DataMember(Name = "waitingTimeoutSeconds")] public int WaitingTimeoutSeconds { get; set; }
            [DataMember(Name = "customerLayoutMode")] public int CustomerLayoutMode { get; set; }
            [DataMember(Name = "imageRotationDegrees")] public int ImageRotationDegrees { get; set; }
            [DataMember(Name = "beauty")] public ActiveBeautyPayload Beauty { get; set; }
            [DataMember(Name = "printPolicy")] public ActivePrintPolicyPayload PrintPolicy { get; set; }
            [DataMember(Name = "colorPresetStrengthPercent")] public int? ColorPresetStrengthPercent { get; set; }
            [DataMember(Name = "frameIds")] public string[] FrameIds { get; set; }
            [DataMember(Name = "presetIds")] public string[] PresetIds { get; set; }
        }

        [DataContract]
        sealed class ActiveBeautyPayload
        {
            [DataMember(Name = "enabled")] public bool Enabled { get; set; }
            [DataMember(Name = "smoothSkin")] public int SmoothSkin { get; set; }
            [DataMember(Name = "brightenSkin")] public int BrightenSkin { get; set; }
            [DataMember(Name = "skinTone")] public int SkinTone { get; set; }
            [DataMember(Name = "sharpen")] public int Sharpen { get; set; }
            [DataMember(Name = "eyeSize")] public int EyeSize { get; set; }
            [DataMember(Name = "slimFace")] public int SlimFace { get; set; }
            [DataMember(Name = "blemishRemoval")] public int BlemishRemoval { get; set; }
            [DataMember(Name = "darkCircleReduction")] public int DarkCircleReduction { get; set; }
        }

        [DataContract]
        sealed class ActivePrintPolicyPayload
        {
            [DataMember(Name="mode")] public string Mode { get; set; }
            [DataMember(Name="includedCopies")] public int IncludedCopies { get; set; }
            [DataMember(Name="maxCopies")] public int MaximumCopies { get; set; }
        }

        static EventPrintPolicyMode ParsePrintPolicyMode(string value)
        {
            return Enum.TryParse(value,true,out EventPrintPolicyMode parsed)?parsed:EventPrintPolicyMode.Manual;
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
            command.Parameters.AddWithValue("$blemish", beauty.BlemishRemoval);
            command.Parameters.AddWithValue("$darkCircle", beauty.DarkCircleReduction);
        }
    }
}
