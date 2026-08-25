using System;using System.Threading;using System.Threading.Tasks;using Microsoft.Data.Sqlite;using PhotoBooth.Core.Models;using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Database
{
 public sealed class SqlitePrintJobRepository:IPrintJobRepository
 {
  readonly SqliteDatabase db;public SqlitePrintJobRepository(SqliteDatabase db){this.db=db;}
  public Task AddAsync(PrintJobRecord r,CancellationToken t){using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="INSERT INTO PrintJobs (Id,SessionId,CaptureId,PrinterProfileId,PrinterName,Copies,PaperSize,PaperType,Quality,Landscape,PrintInColor,UseDefaultBorder,Status,PrintedAtUtc) VALUES($id,$session,$capture,$profile,$printer,$copies,$paperSize,$paperType,$quality,$landscape,$color,$border,$status,$printed)";q.Parameters.AddWithValue("$id",r.Id.ToString());q.Parameters.AddWithValue("$session",(object)(r.SessionId?.ToString())??DBNull.Value);q.Parameters.AddWithValue("$capture",(object)r.CaptureId??DBNull.Value);q.Parameters.AddWithValue("$profile",(object)(r.PrinterProfileId?.ToString())??DBNull.Value);q.Parameters.AddWithValue("$printer",r.PrinterName);q.Parameters.AddWithValue("$copies",r.Copies);q.Parameters.AddWithValue("$paperSize",(object)r.PaperSize??DBNull.Value);q.Parameters.AddWithValue("$paperType",(object)r.PaperType??DBNull.Value);q.Parameters.AddWithValue("$quality",(object)r.Quality??DBNull.Value);q.Parameters.AddWithValue("$landscape",r.Landscape?1:0);q.Parameters.AddWithValue("$color",r.PrintInColor?1:0);q.Parameters.AddWithValue("$border",r.UseDefaultBorder?1:0);q.Parameters.AddWithValue("$status",r.Status);q.Parameters.AddWithValue("$printed",r.PrintedAtUtc.ToString("O"));q.ExecuteNonQuery();}return Task.CompletedTask;}
 }
}
