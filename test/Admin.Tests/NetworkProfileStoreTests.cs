using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class NetworkProfileStoreTests
{
    [Fact]
    public async Task ProxySecretsAreEncryptedAndTlsProfilesAreValidatedAndAudited()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var actorId = 9_800_000L + Random.Shared.Next(1, 100_000);
        var proxyName = $"test-proxy-{Guid.NewGuid():N}";
        var tlsName = $"test-tls-{Guid.NewGuid():N}";
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = masterKey,
            })
            .Build();
        var protector = new SecretProtector(configuration);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new NetworkProfileStore(dataSource, protector);
        long? proxyId = null;
        long? tlsId = null;
        try
        {
            var createdProxy = await store.CreateProxyAsync(actorId,
                new ProxyProfileInput(proxyName, "http", "127.0.0.1", 8080,
                    "proxy-user", "super-secret"), "127.0.0.1");
            Assert.Equal(NetworkProfileStatus.Created, createdProxy.Status);
            var proxyKey = createdProxy.Id.GetValueOrDefault();
            Assert.NotEqual(0L, proxyKey);
            proxyId = proxyKey;

            await using (var raw = dataSource.CreateCommand(
                "SELECT password FROM proxies WHERE id = $1"))
            {
                raw.Parameters.AddWithValue(proxyKey);
                var encrypted = (string?)await raw.ExecuteScalarAsync();
                Assert.NotNull(encrypted);
                Assert.NotEqual("super-secret", encrypted);
                Assert.Equal("super-secret", protector.Unprotect(encrypted!));
            }

            var listed = await store.ListProxiesAsync(1, 50);
            var view = Assert.Single(listed, item => item.Id == proxyKey);
            Assert.True(view.HasPassword);
            Assert.Equal("127.0.0.1", view.Host);

            var retained = await store.UpdateProxyAsync(actorId, proxyKey,
                new ProxyProfileInput(proxyName, "http", "127.0.0.1", 8081,
                    "proxy-user", null), "127.0.0.1");
            Assert.Equal(NetworkProfileStatus.Updated, retained.Status);
            var cleared = await store.UpdateProxyAsync(actorId, proxyKey,
                new ProxyProfileInput(proxyName, "http", "127.0.0.1", 8081,
                    "proxy-user", ""), "127.0.0.1");
            Assert.Equal(NetworkProfileStatus.Updated, cleared.Status);
            var afterClear = Assert.Single(await store.ListProxiesAsync(1, 50),
                item => item.Id == proxyKey);
            Assert.False(afterClear.HasPassword);
            var retainedEmpty = await store.UpdateProxyAsync(actorId, proxyKey,
                new ProxyProfileInput(proxyName, "http", "127.0.0.1", 8081,
                    "proxy-user", null), "127.0.0.1");
            Assert.Equal(NetworkProfileStatus.Updated, retainedEmpty.Status);

            var invalidProxy = await store.CreateProxyAsync(actorId,
                new ProxyProfileInput("bad", "http", "not a host", 0, null, null), null);
            Assert.Equal(NetworkProfileStatus.Invalid, invalidProxy.Status);

            var createdTls = await store.CreateTlsAsync(actorId,
                new TlsFingerprintProfileInput(tlsName,
                    "0123456789abcdef0123456789abcdef", "t13d1516h2_8da95d", "4865,4866"),
                "127.0.0.1");
            Assert.Equal(NetworkProfileStatus.Created, createdTls.Status);
            var tlsKey = createdTls.Id.GetValueOrDefault();
            Assert.NotEqual(0L, tlsKey);
            tlsId = tlsKey;
            var tls = Assert.Single(await store.ListTlsAsync(), item => item.Id == tlsKey);
            Assert.Equal("0123456789abcdef0123456789abcdef", tls.Ja3Hash);
            Assert.Equal("t13d1516h2_8da95d", tls.Ja4Hash);

            var invalidTls = await store.CreateTlsAsync(actorId,
                new TlsFingerprintProfileInput("bad-tls", "xyz", null, null), null);
            Assert.Equal(NetworkProfileStatus.Invalid, invalidTls.Status);

            await using var audit = dataSource.CreateCommand("""
                SELECT count(*) FROM audit_logs
                WHERE user_id = $1 AND action IN (
                    'proxy.created', 'proxy.updated', 'tls_profile.created')
                """);
            audit.Parameters.AddWithValue(actorId);
            Assert.Equal(5L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_logs WHERE user_id = $1",
                "DELETE FROM proxies WHERE id = $1",
                "DELETE FROM tls_fingerprint_profiles WHERE id = $1",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(statement.Contains("proxies", StringComparison.Ordinal)
                    ? (object?)proxyId ?? DBNull.Value
                    : statement.Contains("tls_fingerprint", StringComparison.Ordinal)
                        ? (object?)tlsId ?? DBNull.Value : actorId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
