using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePresetRepository : IPresetRepository
    {
        private readonly SqliteDatabase _database;
        public SqlitePresetRepository(SqliteDatabase database) { _database = database; }

        public Task<IReadOnlyList<Preset>> GetAllAsync(CancellationToken cancellationToken)
        {
            var result = new List<Preset>();
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,Name,SettingsJson,FrameId,PrinterProfileId,Countdown,CreatedAtUtc,ModifiedAtUtc,IsDefault FROM AdminPresets ORDER BY Name";
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new Preset { Id=Guid.Parse(reader.GetString(0)),Name=reader.GetString(1),SettingsJson=reader.IsDBNull(2)?null:reader.GetString(2),FrameId=reader.IsDBNull(3)?(Guid?)null:Guid.Parse(reader.GetString(3)),PrinterProfileId=reader.IsDBNull(4)?(Guid?)null:Guid.Parse(reader.GetString(4)),CaptureCountdownSeconds=reader.GetInt32(5),CreatedAtUtc=DateTime.Parse(reader.GetString(6)).ToUniversalTime(),ModifiedAtUtc=DateTime.Parse(reader.GetString(7)).ToUniversalTime(),IsDefault=reader.GetInt32(8)!=0 });
            }
            foreach(var preset in result){var normalized=LoadSettings(preset.Id);if(normalized==null){normalized=Deserialize(preset.SettingsJson);SaveSettings(preset.Id,normalized);}preset.SettingsJson=Serialize(normalized);}
            return Task.FromResult<IReadOnlyList<Preset>>(result);
        }

        public async Task<Preset> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            var values = await GetAllAsync(cancellationToken).ConfigureAwait(false);
            return values.FirstOrDefault(x => x.Id == id);
        }

        public Task SaveAsync(Preset preset, CancellationToken cancellationToken)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO AdminPresets VALUES($id,$name,$json,$frame,$printer,$countdown,$created,$modified,$default)";
                command.Parameters.AddWithValue("$id", preset.Id.ToString());
                command.Parameters.AddWithValue("$name", preset.Name);
                command.Parameters.AddWithValue("$json", (object)preset.SettingsJson ?? DBNull.Value);
                command.Parameters.AddWithValue("$frame", preset.FrameId.HasValue?(object)preset.FrameId.Value.ToString():DBNull.Value);
                command.Parameters.AddWithValue("$printer", preset.PrinterProfileId.HasValue?(object)preset.PrinterProfileId.Value.ToString():DBNull.Value);
                command.Parameters.AddWithValue("$countdown", preset.CaptureCountdownSeconds);
                command.Parameters.AddWithValue("$created", preset.CreatedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$modified", preset.ModifiedAtUtc.ToString("O"));
                command.Parameters.AddWithValue("$default", preset.IsDefault?1:0);
                command.ExecuteNonQuery();
            }
            SaveSettings(preset.Id,Deserialize(preset.SettingsJson));
            return Task.CompletedTask;
        }

        PresetProcessingOptions LoadSettings(Guid id){using(var c=_database.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT Brightness,Contrast,Saturation,Gamma,Exposure,Temperature,Tint,Sharpen,Blur,Vignette,BlackAndWhite,Sepia,WatermarkPath,WatermarkOpacity,OutputWidth,OutputHeight,Dpi FROM PresetProcessingSettings WHERE PresetId=$id";q.Parameters.AddWithValue("$id",id.ToString());using(var r=q.ExecuteReader()){if(!r.Read())return null;return new PresetProcessingOptions{Brightness=(float)r.GetDouble(0),Contrast=(float)r.GetDouble(1),Saturation=(float)r.GetDouble(2),Gamma=(float)r.GetDouble(3),Exposure=(float)r.GetDouble(4),Temperature=(float)r.GetDouble(5),Tint=(float)r.GetDouble(6),Sharpen=(float)r.GetDouble(7),Blur=(float)r.GetDouble(8),Vignette=(float)r.GetDouble(9),BlackAndWhite=r.GetInt32(10)!=0,Sepia=r.GetInt32(11)!=0,WatermarkPath=r.IsDBNull(12)?null:r.GetString(12),WatermarkOpacity=(float)r.GetDouble(13),OutputWidth=r.GetInt32(14),OutputHeight=r.GetInt32(15),Dpi=r.GetInt32(16)};}}}
        void SaveSettings(Guid id,PresetProcessingOptions o){using(var c=_database.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT OR REPLACE INTO PresetProcessingSettings VALUES($id,$b,$c,$s,$g,$e,$temp,$tint,$sharp,$blur,$v,$bw,$sepia,$watermark,$opacity,$w,$h,$dpi)";q.Parameters.AddWithValue("$id",id.ToString());q.Parameters.AddWithValue("$b",o.Brightness);q.Parameters.AddWithValue("$c",o.Contrast);q.Parameters.AddWithValue("$s",o.Saturation);q.Parameters.AddWithValue("$g",o.Gamma);q.Parameters.AddWithValue("$e",o.Exposure);q.Parameters.AddWithValue("$temp",o.Temperature);q.Parameters.AddWithValue("$tint",o.Tint);q.Parameters.AddWithValue("$sharp",o.Sharpen);q.Parameters.AddWithValue("$blur",o.Blur);q.Parameters.AddWithValue("$v",o.Vignette);q.Parameters.AddWithValue("$bw",o.BlackAndWhite?1:0);q.Parameters.AddWithValue("$sepia",o.Sepia?1:0);q.Parameters.AddWithValue("$watermark",(object)o.WatermarkPath??DBNull.Value);q.Parameters.AddWithValue("$opacity",o.WatermarkOpacity);q.Parameters.AddWithValue("$w",o.OutputWidth);q.Parameters.AddWithValue("$h",o.OutputHeight);q.Parameters.AddWithValue("$dpi",o.Dpi);q.ExecuteNonQuery();}}
        static PresetProcessingOptions Deserialize(string json){if(string.IsNullOrWhiteSpace(json))return new PresetProcessingOptions();try{using(var s=new MemoryStream(Encoding.UTF8.GetBytes(json)))return (PresetProcessingOptions)new DataContractJsonSerializer(typeof(PresetProcessingOptions)).ReadObject(s);}catch{return new PresetProcessingOptions();}}
        static string Serialize(PresetProcessingOptions value){using(var s=new MemoryStream()){new DataContractJsonSerializer(typeof(PresetProcessingOptions)).WriteObject(s,value);return Encoding.UTF8.GetString(s.ToArray());}}

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM AdminPresets WHERE Id=$id";
                command.Parameters.AddWithValue("$id", id.ToString());
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }
    }
}
