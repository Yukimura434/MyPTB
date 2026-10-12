using System;
using System.IO;
using System.Threading;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using PhotoBooth.Infrastructure.Services;
using PhotoBooth.Shared;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class PcAuthenticationTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "photobooth-auth-tests-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public async System.Threading.Tasks.Task Development_accounts_authenticate_with_distinct_app_roles()
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db"));
            database.Initialize();
            var accounts = new SqlitePcAccountRepository(database);
            var devices = new SqliteDeviceIdentityRepository(database);
            var service = new PcAuthenticationService(accounts, new PcSessionContext(), devices, new PasswordService(), new ApplicationOptions
            {
                ApplicationName = "PhotoBooth.Tests",
                DataDirectory = root,
                DatabasePath = Path.Combine(root, "auth.db")
            });
            await SeedAsync(accounts);

            var admin = await service.AuthenticateAsync("ADMIN.APP.TEST@MYPTBOOTH.LOCAL", "PhotoBoothAdmin#2026", false, CancellationToken.None);
            var appOperator = await service.AuthenticateAsync("operator.app.test@myptbooth.local", "PhotoBoothOperator#2026", false, CancellationToken.None);

            Assert.True(admin.Succeeded);
            Assert.Equal(PcAppRole.LocalAdmin, admin.Session.Role);
            Assert.Equal(PcAuthenticationMode.DevelopmentAccount, admin.Session.AuthenticationMode);
            Assert.True(appOperator.Succeeded);
            Assert.Equal(PcAppRole.BoothOperator, appOperator.Session.Role);
        }

        [Theory]
        [InlineData("BoothOperator", PcAppRole.BoothOperator)]
        public void Worker_app_roles_are_mapped_exactly(string value, PcAppRole expected)
        {
            Assert.True(PcAuthenticationService.TryParseRemoteRole(value, out var role));
            Assert.Equal(expected, role);
        }

        [Theory]
        [InlineData(null, true, true)]
        [InlineData(BoothAccessCodeType.Event, true, true)]
        [InlineData(BoothAccessCodeType.PhotoBooth, false, false)]
        [InlineData(BoothAccessCodeType.SelfBooth, false, false)]
        public void Employee_and_event_have_manager_controls_but_guest_scopes_stay_bounded(BoothAccessCodeType? type, bool canManage, bool canConfigureDevice)
        {
            var session = new PcSession
            {
                Role = PcAppRole.BoothOperator,
                AuthenticationMode = PcAuthenticationMode.OnlineDeviceBound,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                EventAuthorization = type.HasValue ? new BoothAccessAuthorization { CodeType = type.Value } : null
            };
            Assert.Equal(canManage, session.CanManageConfiguration);
            Assert.Equal(canConfigureDevice, session.CanConfigureDevice);
            if (!type.HasValue) Assert.Equal("Quản lý booth", session.RoleDisplayName);
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            Assert.False(session.CanManageConfiguration);
            Assert.False(session.CanConfigureDevice);
        }

        [Theory]
        [InlineData(PcAppRole.LocalAdmin, PcAuthenticationMode.OnlineDeviceBound)]
        [InlineData(PcAppRole.BoothOperator, PcAuthenticationMode.OfflineCache)]
        [InlineData((PcAppRole)99, PcAuthenticationMode.OnlineDeviceBound)]
        public void Legacy_or_unverified_role_does_not_grant_manager_controls(PcAppRole role, PcAuthenticationMode mode)
        {
            var session = new PcSession { Role = role, AuthenticationMode = mode, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
            Assert.False(session.CanManageConfiguration);
            Assert.False(session.CanConfigureDevice);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("manager")]
        [InlineData("LocalAdmin")]
        [InlineData("Owner")]
        [InlineData("LocalAdmin ")]
        public void Unknown_worker_app_roles_fail_closed(string value)
        {
            Assert.False(PcAuthenticationService.TryParseRemoteRole(value, out _));
        }

        [Fact]
        public async System.Threading.Tasks.Task Invalid_password_does_not_open_a_session()
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db"));
            database.Initialize();
            var accounts = new SqlitePcAccountRepository(database);
            var service = new PcAuthenticationService(accounts, new PcSessionContext(), new SqliteDeviceIdentityRepository(database), new PasswordService(), new ApplicationOptions { DataDirectory = root, DatabasePath = Path.Combine(root, "auth.db") });
            await SeedAsync(accounts);

            var result = await service.AuthenticateAsync("admin.app.test@myptbooth.local", "wrong", false, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.Session);
        }

        [Fact]
        public async System.Threading.Tasks.Task Production_account_cache_cannot_bypass_online_booth_assignment_check()
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db"));
            database.Initialize();
            var accounts = new SqlitePcAccountRepository(database);
            var passwords = new PasswordService();
            await accounts.UpsertAsync(new PcAccountRecord { AccountId = "bta_staff", Email = "manager01", DisplayName = "Manager", OrganizationId = "org_1", Role = PcAppRole.LocalAdmin, PasswordHash = passwords.Hash("ManagerPass123"), IsActive = true, IsDevelopmentAccount = false, LastOnlineAuthenticationAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow }, CancellationToken.None);
            var service = new PcAuthenticationService(accounts, new PcSessionContext(), new SqliteDeviceIdentityRepository(database), passwords, new ApplicationOptions { DataDirectory = root, DatabasePath = Path.Combine(root, "auth.db") });

            var result = await service.AuthenticateAsync("manager01", "ManagerPass123", false, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("Thiết bị chưa được liên kết đầy đủ", result.Error);
        }

        [Fact]
        public async System.Threading.Tasks.Task Five_offline_failures_temporarily_lock_the_account()
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db"));
            database.Initialize();
            var accounts = new SqlitePcAccountRepository(database);
            var service = new PcAuthenticationService(accounts, new PcSessionContext(), new SqliteDeviceIdentityRepository(database), new PasswordService(), new ApplicationOptions { DataDirectory = root, DatabasePath = Path.Combine(root, "auth.db") });
            await SeedAsync(accounts);

            PcAuthenticationResult result = null;
            for (var attempt = 0; attempt < 5; attempt++)
                result = await service.AuthenticateAsync("admin.app.test@myptbooth.local", "wrong", false, CancellationToken.None);
            var correctAfterLock = await service.AuthenticateAsync("admin.app.test@myptbooth.local", "PhotoBoothAdmin#2026", false, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("tạm khóa", result.Error);
            Assert.False(correctAfterLock.Succeeded);
        }

        [Fact]
        public async System.Threading.Tasks.Task Remembered_login_round_trips_and_can_be_cleared()
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db"));
            database.Initialize();
            var accounts = new SqlitePcAccountRepository(database);
            var expires = DateTime.UtcNow.AddDays(7);

            await accounts.SaveRememberedLoginAsync(new PcRememberedLogin
            {
                AccountId = "usr_test",
                OrganizationId = "org_test",
                ProtectedToken = new byte[] { 1, 2, 3, 4 },
                ExpiresAtUtc = expires,
                UpdatedAtUtc = DateTime.UtcNow
            }, CancellationToken.None);

            var saved = await accounts.GetRememberedLoginAsync(CancellationToken.None);
            Assert.Equal("usr_test", saved.AccountId);
            Assert.Equal("org_test", saved.OrganizationId);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, saved.ProtectedToken);
            Assert.Equal(expires.ToString("O"), saved.ExpiresAtUtc.ToString("O"));

            await accounts.ClearRememberedLoginAsync(CancellationToken.None);
            Assert.Null(await accounts.GetRememberedLoginAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async System.Threading.Tasks.Task Legacy_remember_flag_never_persists_and_restart_never_resumes(bool remember)
        {
            Directory.CreateDirectory(root);
            var database = new SqliteDatabase(Path.Combine(root, "auth.db")); database.Initialize();
            var accounts = new SqlitePcAccountRepository(database); await SeedAsync(accounts);
            await accounts.SaveRememberedLoginAsync(new PcRememberedLogin { AccountId="old", OrganizationId="org", ProtectedToken=new byte[]{1,2,3}, ExpiresAtUtc=DateTime.UtcNow.AddDays(7), UpdatedAtUtc=DateTime.UtcNow }, CancellationToken.None);
            var sessions = new PcSessionContext();
            var service = new PcAuthenticationService(accounts, sessions, new SqliteDeviceIdentityRepository(database), new PasswordService(), new ApplicationOptions {DataDirectory=root,DatabasePath=Path.Combine(root,"auth.db")});
            var result = await service.AuthenticateAsync("admin.app.test@myptbooth.local", "PhotoBoothAdmin#2026", remember, CancellationToken.None);
            Assert.True(result.Succeeded); Assert.Null(await accounts.GetRememberedLoginAsync(CancellationToken.None));
            sessions.SignIn(result.Session);
            Assert.False((await service.TryResumeAsync(CancellationToken.None)).Succeeded); Assert.Null(sessions.Current);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(BoothAccessCodeType.Event)]
        [InlineData(BoothAccessCodeType.SelfBooth)]
        [InlineData(BoothAccessCodeType.PhotoBooth)]
        public async System.Threading.Tasks.Task Exit_clears_each_login_even_when_network_token_is_cancelled(BoothAccessCodeType? kind)
        {
            Directory.CreateDirectory(root); var database=new SqliteDatabase(Path.Combine(root,"auth.db"));database.Initialize();
            var accounts=new SqlitePcAccountRepository(database);var sessions=new PcSessionContext();
            sessions.SignIn(new PcSession {AccountId="synthetic",RemoteSessionToken="synthetic",ExpiresAtUtc=DateTime.UtcNow.AddHours(1),EventAuthorization=kind.HasValue?new BoothAccessAuthorization{CodeType=kind.Value}:null});
            var service=new PcAuthenticationService(accounts,sessions,new SqliteDeviceIdentityRepository(database),new PasswordService(),new ApplicationOptions{DataDirectory=root,DatabasePath=Path.Combine(root,"auth.db")});
            await service.ForgetAsync(new CancellationToken(true));Assert.Null(sessions.Current);Assert.Null(await accounts.GetRememberedLoginAsync(CancellationToken.None));
        }

        static async System.Threading.Tasks.Task SeedAsync(SqlitePcAccountRepository accounts)
        {
            var passwords = new PasswordService();
            await accounts.UpsertAsync(new PcAccountRecord { AccountId = "test_pc_admin", Email = "admin.app.test@myptbooth.local", DisplayName = "Admin App Test", Role = PcAppRole.LocalAdmin, PasswordHash = passwords.Hash("PhotoBoothAdmin#2026"), IsActive = true, IsDevelopmentAccount = true, UpdatedAtUtc = DateTime.UtcNow }, CancellationToken.None);
            await accounts.UpsertAsync(new PcAccountRecord { AccountId = "test_pc_operator", Email = "operator.app.test@myptbooth.local", DisplayName = "Operator App Test", Role = PcAppRole.BoothOperator, PasswordHash = passwords.Hash("PhotoBoothOperator#2026"), IsActive = true, IsDevelopmentAccount = true, UpdatedAtUtc = DateTime.UtcNow }, CancellationToken.None);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
