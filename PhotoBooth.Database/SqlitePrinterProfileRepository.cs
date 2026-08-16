using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
 public sealed class SqlitePrinterProfileRepository:IPrinterProfileRepository
 {
  readonly SqliteDatabase db;public SqlitePrinterProfileRepository(SqliteDatabase db){this.db=db;}
  public async Task<PrinterProfile> GetAsync(Guid id,CancellationToken token)=>(await GetAllAsync(token)).FirstOrDefault(x=>x.Id==id);
  public async Task<PrinterProfile> GetByPrinterIdAsync(string id,CancellationToken token)=>(await GetAllAsync(token)).FirstOrDefault(x=>string.Equals(x.PrinterId,id,StringComparison.OrdinalIgnoreCase));
  public async Task<PrinterProfile> GetDefaultAsync(CancellationToken token)=>(await GetAllAsync(token)).SingleOrDefault(x=>x.IsDefault);
  public Task<IReadOnlyList<PrinterProfile>> GetAllAsync(CancellationToken token)
  {
   token.ThrowIfCancellationRequested();var list=new List<PrinterProfile>();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand())
   {
    q.CommandText="SELECT Id,Name,PrinterName,PrinterId,PaperSize,PaperType,Quality,UseDefaultBorder,DefaultCopies,Landscape,IsDefault,PrintInColor FROM PrinterProfiles";
    using(var r=q.ExecuteReader())while(r.Read())list.Add(new PrinterProfile{Id=Guid.Parse(r.GetString(0)),Name=Text(r,1),PrinterName=Text(r,2),PrinterId=Text(r,3),PaperSize=Text(r,4),PaperType=Text(r,5),Quality=Text(r,6),UseDefaultBorder=r.GetInt32(7)!=0,DefaultCopies=r.GetInt32(8),Landscape=r.GetInt32(9)!=0,IsDefault=r.GetInt32(10)!=0,PrintInColor=r.GetInt32(11)!=0});
   }
   return Task.FromResult<IReadOnlyList<PrinterProfile>>(list);
  }
  public Task SaveAsync(PrinterProfile p,CancellationToken token)
  {
   token.ThrowIfCancellationRequested();if(p==null)throw new ArgumentNullException(nameof(p));if(string.IsNullOrWhiteSpace(p.PrinterId))throw new InvalidOperationException("Printer ID is required before saving the profile.");
   using(var c=db.OpenConnection())using(var tx=c.BeginTransaction())
   {
    // Reuse the existing row for this physical Windows queue even when legacy UI generated another Guid.
    using(var find=c.CreateCommand()){find.Transaction=tx;find.CommandText="SELECT Id FROM PrinterProfiles WHERE PrinterId=$pid LIMIT 1";find.Parameters.AddWithValue("$pid",p.PrinterId);var existing=find.ExecuteScalar() as string;if(!string.IsNullOrWhiteSpace(existing))p.Id=Guid.Parse(existing);}
    if(p.IsDefault)using(var clear=c.CreateCommand()){clear.Transaction=tx;clear.CommandText="UPDATE PrinterProfiles SET IsDefault=0 WHERE IsDefault=1 AND Id<>$id";clear.Parameters.AddWithValue("$id",p.Id.ToString());clear.ExecuteNonQuery();}
    using(var q=c.CreateCommand())
    {
     q.Transaction=tx;q.CommandText=@"INSERT INTO PrinterProfiles (Id,Name,PrinterName,PrinterId,PaperSize,PaperType,Quality,UseDefaultBorder,DefaultCopies,Landscape,IsDefault,PrintInColor)
VALUES($id,$n,$p,$pid,$s,$t,$q,$b,$c,$l,$d,$color)
ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,PrinterName=excluded.PrinterName,PrinterId=excluded.PrinterId,PaperSize=excluded.PaperSize,PaperType=excluded.PaperType,Quality=excluded.Quality,UseDefaultBorder=excluded.UseDefaultBorder,DefaultCopies=excluded.DefaultCopies,Landscape=excluded.Landscape,IsDefault=excluded.IsDefault,PrintInColor=excluded.PrintInColor";
     q.Parameters.AddWithValue("$id",p.Id.ToString());q.Parameters.AddWithValue("$n",(object)p.Name??DBNull.Value);q.Parameters.AddWithValue("$p",(object)p.PrinterName??DBNull.Value);q.Parameters.AddWithValue("$pid",p.PrinterId);q.Parameters.AddWithValue("$s",(object)p.PaperSize??DBNull.Value);q.Parameters.AddWithValue("$t",(object)p.PaperType??DBNull.Value);q.Parameters.AddWithValue("$q",(object)p.Quality??DBNull.Value);q.Parameters.AddWithValue("$b",p.UseDefaultBorder?1:0);q.Parameters.AddWithValue("$c",Math.Max(1,p.DefaultCopies));q.Parameters.AddWithValue("$l",p.Landscape?1:0);q.Parameters.AddWithValue("$d",p.IsDefault?1:0);q.Parameters.AddWithValue("$color",p.PrintInColor?1:0);q.ExecuteNonQuery();
    }
    tx.Commit();
   }
   return Task.CompletedTask;
  }
  public Task DeleteAsync(Guid id,CancellationToken token){token.ThrowIfCancellationRequested();using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="DELETE FROM PrinterProfiles WHERE Id=$id";q.Parameters.AddWithValue("$id",id.ToString());q.ExecuteNonQuery();}return Task.CompletedTask;}
  static string Text(Microsoft.Data.Sqlite.SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
 }
}
