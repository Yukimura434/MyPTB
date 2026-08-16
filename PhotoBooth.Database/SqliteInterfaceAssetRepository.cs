using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteInterfaceAssetRepository : IInterfaceAssetRepository
    {
        readonly SqliteDatabase db;
        public SqliteInterfaceAssetRepository(SqliteDatabase database) { db = database; }
        public Task<IReadOnlyList<InterfaceAsset>> GetAllAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var values = new List<InterfaceAsset>();
            using (var c=db.OpenConnection()) using(var q=c.CreateCommand()) { q.CommandText="SELECT Id,Name,FilePath,IsAnimated,IsSelected,CreatedAtUtc FROM InterfaceAssets ORDER BY CreatedAtUtc DESC"; using(var r=q.ExecuteReader()) while(r.Read()) values.Add(Read(r)); }
            return Task.FromResult<IReadOnlyList<InterfaceAsset>>(values);
        }
        public Task<InterfaceAsset> GetSelectedAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using(var c=db.OpenConnection()) using(var q=c.CreateCommand()) { q.CommandText="SELECT Id,Name,FilePath,IsAnimated,IsSelected,CreatedAtUtc FROM InterfaceAssets WHERE IsSelected=1 LIMIT 1"; using(var r=q.ExecuteReader()) return Task.FromResult(r.Read()?Read(r):null); }
        }
        public Task AddAsync(InterfaceAsset a,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using(var c=db.OpenConnection()) using(var q=c.CreateCommand()){q.CommandText="INSERT INTO InterfaceAssets(Id,Name,FilePath,IsAnimated,IsSelected,CreatedAtUtc) VALUES($id,$name,$path,$animated,$selected,$created)";q.Parameters.AddWithValue("$id",a.Id.ToString());q.Parameters.AddWithValue("$name",a.Name);q.Parameters.AddWithValue("$path",a.FilePath);q.Parameters.AddWithValue("$animated",a.IsAnimated?1:0);q.Parameters.AddWithValue("$selected",a.IsSelected?1:0);q.Parameters.AddWithValue("$created",a.CreatedAtUtc.ToString("O"));q.ExecuteNonQuery();} return Task.CompletedTask;
        }
        public Task SelectAsync(Guid id,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using(var c=db.OpenConnection()) using(var tx=c.BeginTransaction()){using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="UPDATE InterfaceAssets SET IsSelected=0";clear.ExecuteNonQuery();}using(var set=c.CreateCommand()){set.Transaction=tx;set.CommandText="UPDATE InterfaceAssets SET IsSelected=1 WHERE Id=$id";set.Parameters.AddWithValue("$id",id.ToString());if(set.ExecuteNonQuery()!=1)throw new InvalidOperationException("Interface asset was not found.");}tx.Commit();}return Task.CompletedTask;
        }
        static InterfaceAsset Read(Microsoft.Data.Sqlite.SqliteDataReader r)=>new InterfaceAsset{Id=Guid.Parse(r.GetString(0)),Name=r.GetString(1),FilePath=r.GetString(2),IsAnimated=r.GetInt32(3)!=0,IsSelected=r.GetInt32(4)!=0,CreatedAtUtc=DateTime.Parse(r.GetString(5)).ToUniversalTime()};
    }
}
