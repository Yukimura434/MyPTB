using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;

namespace PhotoBooth.Database
{
    public sealed class SqliteDeviceIdentityRepository : IDeviceIdentityRepository
    {
        readonly SqliteDatabase db;

        public SqliteDeviceIdentityRepository(SqliteDatabase database)
        {
            db = database;
        }

        public Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT p.OrganizationId,p.DeviceId,p.LocationId,p.DeviceName,p.EnrollmentState,p.CredentialVersion,p.LastAuthenticatedAtUtc,NULL,p.Id,i.PublicId
FROM AppState a JOIN DeviceProfiles p ON p.Id=a.ActiveProfileId JOIN DeviceInstallations i ON i.Id=p.InstallationId WHERE a.Id=1";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return Task.FromResult<DeviceIdentity>(null);
                    return Task.FromResult(new DeviceIdentity
                    {
                        OrganizationId = Text(reader, 0),
                        DeviceId = Text(reader, 1),
                        LocationId = Text(reader, 2),
                        DeviceName = Text(reader, 3),
                        EnrollmentState = ParseState(Text(reader, 4)),
                        CredentialVersion = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                        LastAuthenticatedAtUtc = Date(reader, 6),
                        LastHeartbeatAtUtc = Date(reader, 7),
                        ProfileId = Text(reader, 8),
                        InstallationId = Text(reader, 9)
                    });
                }
            }
        }

        public Task<byte[]> GetProtectedCredentialAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT s.ProtectedValue FROM AppState a JOIN ProfileSecureArtifacts s ON s.ProfileId=a.ActiveProfileId AND s.ArtifactType='DeviceCredential' WHERE a.Id=1";
                var value = command.ExecuteScalar();
                return Task.FromResult(value == null || value == DBNull.Value ? null : (byte[])value);
            }
        }

        public Task SaveEnrolledAsync(DeviceIdentity identity, byte[] protectedCredential, CancellationToken cancellationToken)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (protectedCredential == null || protectedCredential.Length == 0) throw new ArgumentException("A protected credential is required.", nameof(protectedCredential));
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow.ToString("O");
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO DeviceIdentity(Id,OrganizationId,DeviceId,LocationId,DeviceName,EnrollmentState,CredentialVersion,LastAuthenticatedAtUtc,LastHeartbeatAtUtc,UpdatedAtUtc) VALUES(1,$org,$device,$location,$name,$state,$version,$auth,$heartbeat,$updated)";
                    command.Parameters.AddWithValue("$org", Db(identity.OrganizationId));
                    command.Parameters.AddWithValue("$device", Db(identity.DeviceId));
                    command.Parameters.AddWithValue("$location", Db(identity.LocationId));
                    command.Parameters.AddWithValue("$name", Db(identity.DeviceName));
                    command.Parameters.AddWithValue("$state", identity.EnrollmentState.ToString());
                    command.Parameters.AddWithValue("$version", Db(identity.CredentialVersion));
                    command.Parameters.AddWithValue("$auth", Db((identity.LastAuthenticatedAtUtc ?? DateTime.UtcNow).ToString("O")));
                    command.Parameters.AddWithValue("$heartbeat", Db(identity.LastHeartbeatAtUtc?.ToString("O")));
                    command.Parameters.AddWithValue("$updated", now);
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR REPLACE INTO SecureArtifacts(ArtifactType,ProtectedValue,CreatedAtUtc,ExpiresAtUtc,UpdatedAtUtc) VALUES('DeviceCredential',$credential,$created,NULL,$updated)";
                    command.Parameters.AddWithValue("$credential", protectedCredential);
                    command.Parameters.AddWithValue("$created", now);
                    command.Parameters.AddWithValue("$updated", now);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE DeviceIdentity SET OrganizationId=NULL,DeviceId=NULL,LocationId=NULL,DeviceName=NULL,EnrollmentState='Unenrolled',CredentialVersion=NULL,LastAuthenticatedAtUtc=NULL,LastHeartbeatAtUtc=NULL,UpdatedAtUtc=$updated WHERE Id=1";
                    command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM SecureArtifacts WHERE ArtifactType='DeviceCredential'";
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task ResetRevokedInstallationAsync(string installationPublicId, string organizationId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(installationPublicId) || string.IsNullOrWhiteSpace(organizationId))
                throw new ArgumentException("The revoked installation and organization are required.");
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT i.Id FROM AppState a JOIN DeviceInstallations i ON i.Id=a.InstallationId WHERE a.Id=1 AND i.PublicId=$public";
                command.Parameters.AddWithValue("$public", installationPublicId);
                var previous = command.ExecuteScalar() as string;
                // A delayed response from another process must not reset a newer installation.
                if (previous == null) return Task.CompletedTask;
                var replacement = Guid.NewGuid().ToString("N");
                command.Parameters.AddWithValue("$previous", previous);
                command.Parameters.AddWithValue("$org", organizationId);
                command.Parameters.AddWithValue("$replacement", replacement);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                command.CommandText = @"
DELETE FROM ProfileSecureArtifacts WHERE ProfileId IN (SELECT Id FROM DeviceProfiles WHERE InstallationId=$previous AND OrganizationId=$org);
DELETE FROM DeviceProfiles WHERE InstallationId=$previous AND OrganizationId=$org;
INSERT INTO DeviceInstallations(Id,PublicId,Platform,CreatedAtUtc,UpdatedAtUtc) VALUES($replacement,$replacement,'windows',$now,$now);
UPDATE AppState SET InstallationId=$replacement,ActiveProfileId=NULL,UpdatedAtUtc=$now WHERE Id=1;
DELETE FROM PcRememberedLogin;
DELETE FROM PcAccounts;
DELETE FROM SecureArtifacts WHERE ArtifactType IN ('DeviceCredential','LicenseCache');
UPDATE DeviceIdentity SET OrganizationId=NULL,DeviceId=NULL,LocationId=NULL,DeviceName=NULL,EnrollmentState='Unenrolled',CredentialVersion=NULL,LastAuthenticatedAtUtc=NULL,LastHeartbeatAtUtc=NULL,UpdatedAtUtc=$now WHERE Id=1;
UPDATE DeviceRuntimeState SET SelectedEventId=NULL,SelectedConfigurationVersionId=NULL,SelectedAtUtc=NULL,UpdatedAtUtc=$now WHERE Id=1;
UPDATE OutputJobs SET ProtectedAuthorization=NULL,AuthorizationScope=NULL;";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            // Keep old CNG keys: unrelated saved organizations can still use that installation.
            return Task.CompletedTask;
        }

        public Task<string> GetInstallationPublicIdAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using(var connection=db.OpenConnection())using(var command=connection.CreateCommand())
            {command.CommandText="SELECT i.PublicId FROM AppState a JOIN DeviceInstallations i ON i.Id=a.InstallationId WHERE a.Id=1";return Task.FromResult(Convert.ToString(command.ExecuteScalar()));}
        }

        public Task<IReadOnlyList<DeviceIdentity>> ListProfilesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result=new List<DeviceIdentity>();
            using(var connection=db.OpenConnection())using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT p.Id,i.PublicId,p.OrganizationId,p.DeviceId,p.LocationId,p.DeviceName,p.OrganizationName,p.LocationName,p.EnrollmentState,p.CredentialVersion,p.LastAuthenticatedAtUtc FROM DeviceProfiles p JOIN DeviceInstallations i ON i.Id=p.InstallationId ORDER BY p.LastSelectedAtUtc DESC";
                using(var reader=command.ExecuteReader())while(reader.Read())result.Add(new DeviceIdentity{ProfileId=Text(reader,0),InstallationId=Text(reader,1),OrganizationId=Text(reader,2),DeviceId=Text(reader,3),LocationId=Text(reader,4),DeviceName=Text(reader,5),OrganizationName=Text(reader,6),LocationName=Text(reader,7),EnrollmentState=ParseState(Text(reader,8)),CredentialVersion=reader.GetInt32(9),LastAuthenticatedAtUtc=Date(reader,10)});
            }
            return Task.FromResult<IReadOnlyList<DeviceIdentity>>(result);
        }

        public Task SelectProfileAsync(string profileId,CancellationToken cancellationToken)
        {
            if(string.IsNullOrWhiteSpace(profileId))throw new ArgumentException("Profile id is required.",nameof(profileId));
            cancellationToken.ThrowIfCancellationRequested();
            using(var connection=db.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                var now=DateTime.UtcNow.ToString("O");
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE AppState SET ActiveProfileId=$id,InstallationId=(SELECT InstallationId FROM DeviceProfiles WHERE Id=$id),UpdatedAtUtc=$now WHERE Id=1 AND EXISTS(SELECT 1 FROM DeviceProfiles WHERE Id=$id AND EnrollmentState='Enrolled')";command.Parameters.AddWithValue("$id",profileId);command.Parameters.AddWithValue("$now",now);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Không tìm thấy hồ sơ cửa hàng hợp lệ.");}
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE DeviceProfiles SET LastSelectedAtUtc=$now,UpdatedAtUtc=$now WHERE Id=$id";command.Parameters.AddWithValue("$id",profileId);command.Parameters.AddWithValue("$now",now);if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Hồ sơ cửa hàng đã bị thu hồi hoặc không tồn tại.");}
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        public Task SaveProfileAsync(DeviceIdentity identity,byte[] protectedCredential,CancellationToken cancellationToken)
        {
            if(identity==null||string.IsNullOrWhiteSpace(identity.ProfileId)||string.IsNullOrWhiteSpace(identity.DeviceId))throw new ArgumentException("A complete device profile is required.",nameof(identity));
            if(protectedCredential==null||protectedCredential.Length==0)throw new ArgumentException("A protected credential is required.",nameof(protectedCredential));
            cancellationToken.ThrowIfCancellationRequested();
            var now=DateTime.UtcNow.ToString("O");
            using(var connection=db.OpenConnection())using(var transaction=connection.BeginTransaction())
            {
                string installationId;
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT InstallationId FROM AppState WHERE Id=1";installationId=Convert.ToString(command.ExecuteScalar());}
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT PublicId FROM DeviceInstallations WHERE Id=$id";command.Parameters.AddWithValue("$id",installationId);if(!string.Equals(Convert.ToString(command.ExecuteScalar()),identity.InstallationId,StringComparison.Ordinal))throw new DeviceReenrollmentRequiredException();}
                using(var command=connection.CreateCommand())
                {command.Transaction=transaction;command.CommandText=@"INSERT INTO DeviceProfiles(Id,InstallationId,OrganizationId,LocationId,DeviceId,DeviceName,OrganizationName,LocationName,EnrollmentState,CredentialVersion,LastAuthenticatedAtUtc,LastSelectedAtUtc,CreatedAtUtc,UpdatedAtUtc) VALUES($id,$installation,$org,$location,$device,$name,$orgName,$locationName,'Enrolled',$version,$now,$now,$now,$now) ON CONFLICT(Id) DO UPDATE SET OrganizationId=excluded.OrganizationId,LocationId=excluded.LocationId,DeviceId=excluded.DeviceId,DeviceName=excluded.DeviceName,OrganizationName=COALESCE(excluded.OrganizationName,DeviceProfiles.OrganizationName),LocationName=COALESCE(excluded.LocationName,DeviceProfiles.LocationName),EnrollmentState='Enrolled',CredentialVersion=excluded.CredentialVersion,LastAuthenticatedAtUtc=$now,LastSelectedAtUtc=$now,UpdatedAtUtc=$now";command.Parameters.AddWithValue("$id",identity.ProfileId);command.Parameters.AddWithValue("$installation",installationId);command.Parameters.AddWithValue("$org",identity.OrganizationId);command.Parameters.AddWithValue("$location",Db(identity.LocationId));command.Parameters.AddWithValue("$device",identity.DeviceId);command.Parameters.AddWithValue("$name",identity.DeviceName);command.Parameters.AddWithValue("$orgName",Db(identity.OrganizationName));command.Parameters.AddWithValue("$locationName",Db(identity.LocationName));command.Parameters.AddWithValue("$version",identity.CredentialVersion??3);command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();}
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="INSERT INTO ProfileSecureArtifacts(ProfileId,ArtifactType,ProtectedValue,CreatedAtUtc,UpdatedAtUtc) VALUES($id,'DeviceCredential',$value,$now,$now) ON CONFLICT(ProfileId,ArtifactType) DO UPDATE SET ProtectedValue=excluded.ProtectedValue,UpdatedAtUtc=excluded.UpdatedAtUtc";command.Parameters.AddWithValue("$id",identity.ProfileId);command.Parameters.AddWithValue("$value",protectedCredential);command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();}
                using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE AppState SET ActiveProfileId=$id,UpdatedAtUtc=$now WHERE Id=1";command.Parameters.AddWithValue("$id",identity.ProfileId);command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();}
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

        static object Db(object value) => value == null ? DBNull.Value : value;
        static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        static DateTime? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? (DateTime?)null : DateTime.TryParse(reader.GetString(index), out var value) ? value : (DateTime?)null;
        static DeviceEnrollmentState ParseState(string value) => Enum.TryParse(value, true, out DeviceEnrollmentState state) ? state : DeviceEnrollmentState.Unenrolled;
    }
}
