using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqlitePcAccountRepository : IPcAccountRepository
    {
        readonly SqliteDatabase database;

        public SqlitePcAccountRepository(SqliteDatabase database)
        {
            this.database = database;
        }

        public Task<PcAccountRecord> GetByEmailAsync(string email, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT AccountId,Email,DisplayName,OrganizationId,Role,PasswordHash,IsActive,IsDevelopmentAccount,LastOnlineAuthenticationAtUtc,UpdatedAtUtc
FROM PcAccounts WHERE Email=$email COLLATE NOCASE";
                command.Parameters.AddWithValue("$email", (email ?? string.Empty).Trim());
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<PcAccountRecord>(null);
                    return Task.FromResult(new PcAccountRecord
                    {
                        AccountId = reader.GetString(0),
                        Email = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        OrganizationId = Text(reader, 3),
                        Role = ParseRole(reader.GetString(4)),
                        PasswordHash = reader.GetString(5),
                        IsActive = reader.GetInt32(6) != 0,
                        IsDevelopmentAccount = reader.GetInt32(7) != 0,
                        LastOnlineAuthenticationAtUtc = Date(reader, 8),
                        UpdatedAtUtc = Date(reader, 9) ?? DateTime.MinValue
                    });
                }
            }
        }

        public Task UpsertAsync(PcAccountRecord account, CancellationToken cancellationToken)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO PcAccounts(AccountId,Email,DisplayName,OrganizationId,Role,PasswordHash,IsActive,IsDevelopmentAccount,LastOnlineAuthenticationAtUtc,UpdatedAtUtc)
VALUES($id,$email,$name,$organization,$role,$hash,$active,$development,$lastOnline,$updated)
ON CONFLICT(Email) DO UPDATE SET AccountId=excluded.AccountId,DisplayName=excluded.DisplayName,OrganizationId=excluded.OrganizationId,Role=excluded.Role,PasswordHash=excluded.PasswordHash,IsActive=excluded.IsActive,IsDevelopmentAccount=excluded.IsDevelopmentAccount,LastOnlineAuthenticationAtUtc=excluded.LastOnlineAuthenticationAtUtc,UpdatedAtUtc=excluded.UpdatedAtUtc";
                command.Parameters.AddWithValue("$id", account.AccountId);
                command.Parameters.AddWithValue("$email", account.Email.Trim().ToLowerInvariant());
                command.Parameters.AddWithValue("$name", account.DisplayName);
                command.Parameters.AddWithValue("$organization", Db(account.OrganizationId));
                command.Parameters.AddWithValue("$role", account.Role.ToString());
                command.Parameters.AddWithValue("$hash", account.PasswordHash);
                command.Parameters.AddWithValue("$active", account.IsActive ? 1 : 0);
                command.Parameters.AddWithValue("$development", account.IsDevelopmentAccount ? 1 : 0);
                command.Parameters.AddWithValue("$lastOnline", Db(account.LastOnlineAuthenticationAtUtc?.ToString("O")));
                command.Parameters.AddWithValue("$updated", (account.UpdatedAtUtc == default(DateTime) ? DateTime.UtcNow : account.UpdatedAtUtc).ToString("O"));
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task<PcRememberedLogin> GetRememberedLoginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT AccountId,OrganizationId,ProtectedToken,ExpiresAtUtc,UpdatedAtUtc FROM PcRememberedLogin WHERE Id=1";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<PcRememberedLogin>(null);
                    return Task.FromResult(new PcRememberedLogin
                    {
                        AccountId = reader.GetString(0),
                        OrganizationId = reader.GetString(1),
                        ProtectedToken = (byte[])reader[2],
                        ExpiresAtUtc = Date(reader, 3) ?? DateTime.MinValue,
                        UpdatedAtUtc = Date(reader, 4) ?? DateTime.MinValue
                    });
                }
            }
        }

        public Task SaveRememberedLoginAsync(PcRememberedLogin login, CancellationToken cancellationToken)
        {
            if (login == null) throw new ArgumentNullException(nameof(login));
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO PcRememberedLogin(Id,AccountId,OrganizationId,ProtectedToken,ExpiresAtUtc,UpdatedAtUtc)
VALUES(1,$account,$organization,$token,$expires,$updated)
ON CONFLICT(Id) DO UPDATE SET AccountId=excluded.AccountId,OrganizationId=excluded.OrganizationId,ProtectedToken=excluded.ProtectedToken,ExpiresAtUtc=excluded.ExpiresAtUtc,UpdatedAtUtc=excluded.UpdatedAtUtc";
                command.Parameters.AddWithValue("$account", login.AccountId);
                command.Parameters.AddWithValue("$organization", login.OrganizationId);
                command.Parameters.AddWithValue("$token", login.ProtectedToken);
                command.Parameters.AddWithValue("$expires", login.ExpiresAtUtc.ToUniversalTime().ToString("O"));
                command.Parameters.AddWithValue("$updated", (login.UpdatedAtUtc == default(DateTime) ? DateTime.UtcNow : login.UpdatedAtUtc).ToUniversalTime().ToString("O"));
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        public Task ClearRememberedLoginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM PcRememberedLogin WHERE Id=1";
                command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        static object Db(object value) => value ?? DBNull.Value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? (DateTime?)null : DateTime.TryParse(reader.GetString(index), out var value) ? value.ToUniversalTime() : (DateTime?)null;
        static PcAppRole ParseRole(string value) => Enum.TryParse(value, true, out PcAppRole role) ? role : PcAppRole.BoothOperator;
    }
}
