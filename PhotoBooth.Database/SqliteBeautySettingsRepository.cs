using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Database
{
    public sealed class SqliteBeautySettingsRepository : IBeautySettingsRepository
    {
        readonly SqliteDatabase db;
        public SqliteBeautySettingsRepository(SqliteDatabase database) { db = database; }
        public Task<BeautySettings> GetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Enabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace FROM BeautySettings WHERE Id=1";
                using (var reader = command.ExecuteReader())
                    if (reader.Read()) return Task.FromResult(new BeautySettings { Enabled=reader.GetInt32(0)!=0,SmoothSkin=reader.GetInt32(1),BrightenSkin=reader.GetInt32(2),SkinTone=reader.GetInt32(3),Sharpen=reader.GetInt32(4),EyeSize=reader.GetInt32(5),SlimFace=reader.GetInt32(6) });
            }
            return Task.FromResult(new BeautySettings());
        }
        public Task SaveAsync(BeautySettings value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); value = value ?? new BeautySettings();
            using (var connection=db.OpenConnection()) using(var command=connection.CreateCommand())
            {
                command.CommandText="INSERT OR REPLACE INTO BeautySettings(Id,Enabled,SmoothSkin,BrightenSkin,SkinTone,Sharpen,EyeSize,SlimFace,ModifiedAtUtc) VALUES(1,$enabled,$smooth,$brighten,$tone,$sharpen,$eye,$slim,$modified)";
                command.Parameters.AddWithValue("$enabled",value.Enabled?1:0); command.Parameters.AddWithValue("$smooth",Clamp(value.SmoothSkin)); command.Parameters.AddWithValue("$brighten",Clamp(value.BrightenSkin)); command.Parameters.AddWithValue("$tone",Clamp(value.SkinTone)); command.Parameters.AddWithValue("$sharpen",Clamp(value.Sharpen)); command.Parameters.AddWithValue("$eye",Clamp(value.EyeSize)); command.Parameters.AddWithValue("$slim",Clamp(value.SlimFace)); command.Parameters.AddWithValue("$modified",DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }
        static int Clamp(int value)=>Math.Max(0,Math.Min(100,value));
    }
}
