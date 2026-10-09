using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePrintJobRepository:IPrintJobRepository
    {
        readonly SqliteDatabase database;
        public SqlitePrintJobRepository(SqliteDatabase value){database=value;}
        public Task AddAsync(PrintJobRecord value,CancellationToken token)
        {
            if(value==null||!value.SessionId.HasValue||string.IsNullOrWhiteSpace(value.OutputJobId)||string.IsNullOrWhiteSpace(value.DeliverableId))throw new ArgumentException("A print audit must belong to a durable output job and deliverable.");token.ThrowIfCancellationRequested();
            using(var connection=database.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText=@"INSERT INTO PrintJobs(Id,OutputJobId,BoothSessionId,DeliverableId,PrinterProfileId,PrinterNameSnapshot,Copies,SettingsJson,Outcome,RequestedAtUtc,CompletedAtUtc)
VALUES($id,$output,$session,$deliverable,$profile,$printer,$copies,$settings,$outcome,$requested,$completed)
ON CONFLICT(Id) DO UPDATE SET SpoolerJobId=excluded.SpoolerJobId,Outcome=excluded.Outcome,CompletedAtUtc=excluded.CompletedAtUtc";
                var pending=string.Equals(value.Status,"Pending",StringComparison.Ordinal);
                command.Parameters.AddWithValue("$id",value.Id.ToString());command.Parameters.AddWithValue("$output",Db(value.OutputJobId));command.Parameters.AddWithValue("$session",value.SessionId.Value.ToString());command.Parameters.AddWithValue("$deliverable",Db(value.DeliverableId));command.Parameters.AddWithValue("$profile",value.PrinterProfileId.HasValue?(object)value.PrinterProfileId.Value.ToString():DBNull.Value);command.Parameters.AddWithValue("$printer",value.PrinterName);command.Parameters.AddWithValue("$copies",Math.Max(1,value.Copies));command.Parameters.AddWithValue("$settings",Settings(value));command.Parameters.AddWithValue("$outcome",pending?"Pending":value.Status=="Failed"?"Failed":value.Status=="UnknownOutcome"?"UnknownOutcome":"Success");command.Parameters.AddWithValue("$requested",value.PrintedAtUtc.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$completed",pending?(object)DBNull.Value:value.PrintedAtUtc.ToUniversalTime().ToString("O"));command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }
        static string Settings(PrintJobRecord value)=>"{\"paperSize\":\""+Escape(value.PaperSize)+"\",\"paperType\":\""+Escape(value.PaperType)+"\",\"quality\":\""+Escape(value.Quality)+"\",\"landscape\":"+(value.Landscape?"true":"false")+",\"printInColor\":"+(value.PrintInColor?"true":"false")+",\"useDefaultBorder\":"+(value.UseDefaultBorder?"true":"false")+"}";
        static string Escape(string value)=>(value??string.Empty).Replace("\\","\\\\").Replace("\"","\\\"");
        static object Db(string value)=>string.IsNullOrWhiteSpace(value)?(object)DBNull.Value:value;
    }
}
