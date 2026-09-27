using CoreSync.PostgreSQL;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoreSync.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class PostgreSQLProvisioningSessionTests
    {
        private const string IsolatedConnectionStringVariable = "CORE-SYNC_POSTGRESQL_ISOLATED_CONNECTION_STRING";
        private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Pooling=false";

        [TestMethod]
        public async Task CallerOwnedProvisioningRequiresAnOpenConnection()
        {
            var provider = new PostgreSQLSyncProvider(
                new PostgreSQLSyncConfigurationBuilder(UnreachableConnectionString).Table("unused").Build());

            await Should.ThrowAsync<ArgumentNullException>(() =>
                provider.ApplyProvisionAsync((NpgsqlConnection)null!, CancellationToken.None));

            using var closed = new NpgsqlConnection(UnreachableConnectionString);
            await Should.ThrowAsync<InvalidOperationException>(() =>
                provider.ApplyProvisionAsync(closed, CancellationToken.None));
            closed.State.ShouldBe(ConnectionState.Closed);
        }

        [TestMethod]
        public async Task CallerOwnedProvisioningUsesOnlyTheSuppliedBackend()
        {
            var scope = await TestSchema.CreateAsync();
            await scope.InstallAuditAsync();
            await using var owner = await scope.OpenAsync("owner");
            var backendPid = owner.ProcessID;
            var provider = scope.CreateProvider(UnreachableConnectionString);

            await provider.ApplyProvisionAsync(owner, CancellationToken.None);

            owner.State.ShouldBe(ConnectionState.Open);
            owner.ProcessID.ShouldBe(backendPid);
            var audit = await scope.ReadAuditAsync();
            audit.ShouldNotBeEmpty();
            audit.ShouldContain(row => row.Tag == "CREATE TABLE");
            audit.ShouldContain(row => row.Tag == "CREATE INDEX");
            audit.ShouldContain(row => row.Tag == "CREATE FUNCTION");
            audit.ShouldContain(row => row.Tag == "CREATE TRIGGER");
            audit.All(row => row.BackendPid == backendPid && row.Actor == "owner").ShouldBeTrue();

            await using var observer = await scope.OpenAsync("observer");
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeTrue();
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);
        }

        [TestMethod]
        public async Task CallerOwnedTransactionIsNeverCompletedAndRollbackCanBeRetried()
        {
            var scope = await TestSchema.CreateAsync();
            await using var owner = await scope.OpenAsync("owner");
            await using var observer = await scope.OpenAsync("observer");
            var provider = scope.CreateProvider(UnreachableConnectionString);

            await using (var transaction = await owner.BeginTransactionAsync())
            {
                await provider.ApplyProvisionAsync(owner, transaction, CancellationToken.None);

                transaction.Connection.ShouldBeSameAs(owner);
                await using var query = owner.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = "SELECT COUNT(*) FROM __core_sync_local_id";
                Convert.ToInt64(await query.ExecuteScalarAsync()).ShouldBe(1);
                (await scope.TrackingTableExistsAsync(observer)).ShouldBeFalse();

                await transaction.RollbackAsync();
            }

            owner.State.ShouldBe(ConnectionState.Open);
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeFalse();
            await provider.ApplyProvisionAsync(owner, CancellationToken.None);
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeTrue();
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);

            await using (var transaction = await owner.BeginTransactionAsync())
            {
                await provider.ApplyProvisionAsync(owner, transaction, CancellationToken.None);
                transaction.Connection.ShouldBeSameAs(owner);
                await transaction.CommitAsync();
            }
            owner.State.ShouldBe(ConnectionState.Open);
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);
        }

        [TestMethod]
        public async Task ImplicitCallerTransactionRollbackDoesNotPoisonTheDefaultInitializer()
        {
            var scope = await TestSchema.CreateAsync();
            await using var owner = await scope.OpenAsync("owner");
            await using var observer = await scope.OpenAsync("observer");
            var provider = scope.CreateProvider();

            await using (var transaction = await owner.BeginTransactionAsync())
            {
                await provider.ApplyProvisionAsync(owner, CancellationToken.None);
                await transaction.RollbackAsync();
            }

            (await scope.TrackingTableExistsAsync(observer)).ShouldBeFalse();
            ISyncProvider legacyProvider = provider;
            await legacyProvider.ApplyProvisionAsync(CancellationToken.None);
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeTrue();
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);
        }

        [TestMethod]
        public async Task WrongTransactionAndPreCancellationDoNotWriteOrCloseConnection()
        {
            var scope = await TestSchema.CreateAsync();
            await using var owner = await scope.OpenAsync("owner");
            await using var other = await scope.OpenAsync("other");
            await using var observer = await scope.OpenAsync("observer");
            await using var transaction = await other.BeginTransactionAsync();
            var provider = scope.CreateProvider(UnreachableConnectionString);

            await Should.ThrowAsync<ArgumentException>(() =>
                provider.ApplyProvisionAsync(owner, transaction, CancellationToken.None));
            transaction.Connection.ShouldBeSameAs(other);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() =>
                provider.ApplyProvisionAsync(owner, cancellation.Token));

            owner.State.ShouldBe(ConnectionState.Open);
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeFalse();
            await transaction.RollbackAsync();
        }

        [TestMethod]
        public async Task CancellationDuringTriggerCreationStopsLaterWritesAndCanBeRetried()
        {
            var scope = await TestSchema.CreateAsync();
            var gateKey = NewLockKey();
            await scope.InstallAuditAsync(gateKey, "CREATE TRIGGER");
            await using var gate = await scope.OpenAsync("gate");
            await using var monitor = await scope.OpenAsync("monitor");
            await using var owner = await scope.OpenAsync("holder");
            await using var observer = await scope.OpenAsync("observer");
            await ExecuteAsync(gate, "SELECT pg_advisory_lock($1)", gateKey);
            var provider = scope.CreateProvider(UnreachableConnectionString);
            using var cancellation = new CancellationTokenSource();
            var provisioning = provider.ApplyProvisionAsync(owner, cancellation.Token);

            try
            {
                (await WaitForAdvisoryWaitAsync(monitor, "holder")).ShouldBe(owner.ProcessID);
                cancellation.Cancel();
                var error = await FailureWithinAsync(provisioning);
                (error is OperationCanceledException || error is NpgsqlException).ShouldBeTrue();
            }
            finally
            {
                (await ScalarAsync<bool>(gate, "SELECT pg_advisory_unlock($1)", gateKey)).ShouldBeTrue();
            }

            (await scope.TriggerCountAsync(observer)).ShouldBe(0);
            (await scope.ReadAuditAsync()).ShouldNotContain(row => row.Tag == "CREATE TRIGGER");

            await using var retryOwner = await scope.OpenAsync("retry");
            await provider.ApplyProvisionAsync(retryOwner, CancellationToken.None);
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);
        }

        [TestMethod]
        public async Task LockHolderLossStopsOldWritesBeforeContenderProvisions()
        {
            var scope = await TestSchema.CreateAsync();
            var gateKey = NewLockKey();
            var provisionKey = gateKey + 1;
            await scope.InstallAuditAsync(gateKey, "CREATE INDEX");
            await using var gate = await scope.OpenAsync("gate");
            await using var monitor = await scope.OpenAsync("monitor");
            await using var holder = await scope.OpenAsync("holder");
            await using var contender = await scope.OpenAsync("contender");
            await ExecuteAsync(gate, "SELECT pg_advisory_lock($1)", gateKey);
            await ExecuteAsync(holder, "SELECT pg_advisory_lock($1)", provisionKey);
            var holderPid = holder.ProcessID;
            var contenderPid = contender.ProcessID;
            var first = scope.CreateProviderForActor("holder").ApplyProvisionAsync(holder, CancellationToken.None);

            try
            {
                (await WaitForAdvisoryWaitAsync(monitor, "holder")).ShouldBe(holderPid);
                var beforeLoss = await scope.ReadAuditAsync();
                beforeLoss.ShouldContain(row => row.Actor == "holder" && row.Tag == "CREATE TABLE"
                    && row.BackendPid == holderPid);
                beforeLoss.Where(row => row.Actor == "holder").All(row => row.BackendPid == holderPid).ShouldBeTrue();

                (await ScalarAsync<bool>(contender, "SELECT pg_try_advisory_lock($1)", provisionKey)).ShouldBeFalse();
                using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var contenderLock = ExecuteAsync(contender, "SELECT pg_advisory_lock($1)", lockTimeout.Token, provisionKey);
                (await WaitForAdvisoryWaitAsync(monitor, "contender")).ShouldBe(contenderPid);

                (await ScalarAsync<bool>(monitor, "SELECT pg_terminate_backend($1)", holderPid)).ShouldBeTrue();
                await contenderLock.WaitAsync(TimeSpan.FromSeconds(10));
                await scope.CreateProvider().ApplyProvisionAsync(contender, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                var loss = await FailureWithinAsync(first);
                (loss is NpgsqlException || loss is InvalidOperationException).ShouldBeTrue();
                var afterTakeover = await scope.ReadAuditAsync();
                afterTakeover.ShouldContain(row => row.Actor == "contender" && row.BackendPid == contenderPid);
                afterTakeover.Where(row => row.Actor == "holder")
                    .Select(row => row.Sequence)
                    .ShouldBe(beforeLoss.Where(row => row.Actor == "holder").Select(row => row.Sequence));
                afterTakeover.Where(row => row.Actor == "contender")
                    .All(row => row.Sequence > beforeLoss.Max(before => before.Sequence)).ShouldBeTrue();
            }
            finally
            {
                (await ScalarAsync<bool>(gate, "SELECT pg_advisory_unlock($1)", gateKey)).ShouldBeTrue();
            }

            var finalAudit = await scope.ReadAuditAsync();
            var firstContenderWrite = finalAudit.First(row => row.Actor == "contender").Sequence;
            finalAudit.ShouldNotContain(row => row.Actor == "holder" && row.Sequence >= firstContenderWrite);
            (await scope.TriggerCountAsync(contender)).ShouldBe(3);
        }

        [TestMethod]
        public async Task DefaultInterfaceProvisioningStillUsesItsConfiguredConnection()
        {
            var scope = await TestSchema.CreateAsync();
            ISyncProvider provider = scope.CreateProvider();

            await provider.ApplyProvisionAsync(CancellationToken.None);

            await using var observer = await scope.OpenAsync("observer");
            (await scope.TrackingTableExistsAsync(observer)).ShouldBeTrue();
            (await scope.TriggerCountAsync(observer)).ShouldBe(3);
        }

        private static long NewLockKey() => (BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x3FFF_FFFF_FFFF_FFFFL) + 1;

        private static async Task<int> WaitForAdvisoryWaitAsync(NpgsqlConnection monitor, string actor)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                var pid = await ScalarAsync<int>(monitor,
                    "SELECT COALESCE((SELECT l.pid FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid " +
                    "WHERE a.datname = current_database() AND a.application_name = $1 " +
                    "AND l.locktype = 'advisory' AND NOT l.granted LIMIT 1), 0)", actor);
                if (pid != 0)
                    return pid;

                await Task.Delay(50);
            }

            Assert.Fail($"Timed out waiting for {actor} to block on the advisory gate.");
            throw new InvalidOperationException();
        }

        private static async Task<Exception> FailureWithinAsync(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Assert.Fail("Provisioning did not abort promptly.");
                throw;
            }
            catch (Exception error)
            {
                return error;
            }

            Assert.Fail("Provisioning unexpectedly succeeded.");
            throw new InvalidOperationException();
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params object[] parameters)
        {
            await ExecuteAsync(connection, sql, CancellationToken.None, parameters);
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.Add(new NpgsqlParameter { Value = parameter });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params object[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.Add(new NpgsqlParameter { Value = parameter });
            var result = await command.ExecuteScalarAsync();
            if (result is null || result == DBNull.Value)
                throw new InvalidOperationException("The PostgreSQL scalar query returned no value.");
            return (T)result;
        }

        private sealed record AuditRow(long Sequence, int BackendPid, string Actor, string Tag);

        private sealed class TestSchema
        {
            private readonly string _connectionString;
            private readonly string _auditName;

            private TestSchema(string connectionString, string schema, string tableName, string auditName)
            {
                _connectionString = connectionString;
                Schema = schema;
                TableName = tableName;
                _auditName = auditName;
            }

            public string Schema { get; }
            public string TableName { get; }

            public static async Task<TestSchema> CreateAsync()
            {
                var connectionString = Environment.GetEnvironmentVariable(IsolatedConnectionStringVariable);
                if (string.IsNullOrWhiteSpace(connectionString))
                    Assert.Inconclusive($"Set {IsolatedConnectionStringVariable} to a disposable PostgreSQL database.");

                var suffix = Guid.NewGuid().ToString("N");
                var schema = $"cs_{suffix}";
                var tableName = $"item_{suffix.Substring(0, 16)}";
                var auditName = $"audit_{suffix.Substring(0, 16)}";
                var builder = new NpgsqlConnectionStringBuilder(connectionString!)
                {
                    Pooling = false,
                    SearchPath = schema,
                };

                await using var setup = new NpgsqlConnection(connectionString);
                await setup.OpenAsync();
                await ExecuteAsync(setup, $@"CREATE SCHEMA ""{schema}""");
                await ExecuteAsync(setup, $@"CREATE TABLE ""{schema}"".""{tableName}"" (id integer PRIMARY KEY, payload text)");
                return new TestSchema(builder.ConnectionString, schema, tableName, auditName);
            }

            public PostgreSQLSyncProvider CreateProvider(string? connectionString = null)
                => new PostgreSQLSyncProvider(
                    new PostgreSQLSyncConfigurationBuilder(connectionString ?? _connectionString)
                        .Table(TableName).Build());

            public PostgreSQLSyncProvider CreateProviderForActor(string actor)
            {
                var builder = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = actor };
                return CreateProvider(builder.ConnectionString);
            }

            public async Task<NpgsqlConnection> OpenAsync(string actor)
            {
                var builder = new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = actor };
                var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                return connection;
            }

            public async Task InstallAuditAsync(long? gateKey = null, string gateTag = "CREATE INDEX")
            {
                await using var setup = await OpenAsync("setup");
                await ExecuteAsync(setup, $@"CREATE TABLE public.""{_auditName}"" (
                    sequence bigserial PRIMARY KEY, backend_pid integer NOT NULL,
                    actor text NOT NULL, tag text NOT NULL)");
                var gate = gateKey.HasValue
                    ? $@"IF current_setting('application_name') = 'holder' AND TG_TAG = '{gateTag}' THEN
                            PERFORM pg_advisory_xact_lock({gateKey.Value});
                        END IF;"
                    : string.Empty;
                await ExecuteAsync(setup, $@"
CREATE FUNCTION public.""{_auditName}_function""() RETURNS event_trigger LANGUAGE plpgsql AS $audit$
BEGIN
    IF current_schema() = '{Schema}' THEN
        INSERT INTO public.""{_auditName}"" (backend_pid, actor, tag)
            SELECT pg_backend_pid(), current_setting('application_name'), command_tag
            FROM pg_event_trigger_ddl_commands();
        {gate}
    END IF;
END;
$audit$");
                await ExecuteAsync(setup, $@"CREATE EVENT TRIGGER ""{_auditName}_event""
                    ON ddl_command_end EXECUTE FUNCTION public.""{_auditName}_function""()");
            }

            public async Task<List<AuditRow>> ReadAuditAsync()
            {
                await using var observer = await OpenAsync("audit_reader");
                await using var command = observer.CreateCommand();
                command.CommandText = $@"SELECT sequence, backend_pid, actor, tag
                    FROM public.""{_auditName}"" ORDER BY sequence";
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<AuditRow>();
                while (await reader.ReadAsync())
                    rows.Add(new AuditRow(reader.GetInt64(0), reader.GetInt32(1),
                        reader.GetString(2), reader.GetString(3)));
                return rows;
            }

            public async Task<bool> TrackingTableExistsAsync(NpgsqlConnection connection)
                => await ScalarAsync<bool>(connection, "SELECT to_regclass($1) IS NOT NULL", $"{Schema}.__core_sync_ct");

            public async Task<long> TriggerCountAsync(NpgsqlConnection connection)
                => await ScalarAsync<long>(connection,
                    "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = to_regclass($1) AND NOT tgisinternal",
                    $"{Schema}.{TableName}");
        }
    }
}
