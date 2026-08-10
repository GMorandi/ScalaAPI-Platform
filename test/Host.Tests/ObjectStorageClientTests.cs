using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ObjectStorageClientTests
{
    [Fact]
    public async Task ListAsyncSignsS3QueryAndFollowsContinuation()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ObjectStorage:Endpoint"] = "http://storage.test:9000",
                ["ObjectStorage:PublicEndpoint"] = "http://storage.test:9000",
                ["ObjectStorage:Bucket"] = "scalaapi-media",
                ["ObjectStorage:AccessKey"] = "platform",
                ["ObjectStorage:SecretKey"] = "secret-key",
            }).Build();
        var client = new ObjectStorageClient(http, configuration,
            NullLogger<ObjectStorageClient>.Instance);

        var objects = await client.ListAsync("media/");

        Assert.Equal(2, objects.Count);
        Assert.Equal("media/first.png", objects[0].Key);
        Assert.Equal(12, objects[0].Size);
        Assert.Equal("media/second.bin", objects[1].Key);
        Assert.Equal(4, objects[1].Size);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.All(handler.Requests.Skip(1), request =>
        {
            Assert.DoesNotContain("X-Amz-Algorithm", request.RequestUri!.Query);
            Assert.Contains("list-type=2", request.RequestUri.Query);
            Assert.Contains("prefix=media%2F", request.RequestUri.Query);
            Assert.Contains("AWS4-HMAC-SHA256", request.Headers.GetValues("Authorization").Single());
        });
        Assert.Contains("continuation-token=next-token", handler.Requests[2].RequestUri!.Query);
    }

    [Fact]
    public async Task BatchArchiveCopiesBoundedItemsAndWritesManifest()
    {
        var handler = new ArchiveHandler();
        using var http = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ObjectStorage:Endpoint"] = "http://storage.test:9000",
                ["ObjectStorage:PublicEndpoint"] = "http://storage.test:9000",
                ["ObjectStorage:Bucket"] = "scalaapi-media",
                ["ObjectStorage:AccessKey"] = "platform",
                ["ObjectStorage:SecretKey"] = "secret-key",
            }).Build();
        var client = new ObjectStorageClient(http, configuration,
            NullLogger<ObjectStorageClient>.Instance);

        var result = await client.CreateBatchArchiveAsync("""
            {"data":[
              {"custom_id":"mock-1","url":"http://provider.test/output/one"},
              {"custom_id":"mock-1","url":"http://provider.test/output/two"}
            ]}
            """, "med_batch_archive_test");

        Assert.Equal("media/med_batch_archive_test.zip", result.ObjectKey);
        Assert.Equal("application/zip", result.ContentType);
        var archiveBytes = Assert.Single(handler.PutBodies.Skip(1));
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "mock-1.png");
        Assert.Contains(archive.Entries, entry => entry.FullName == "mock-1-2.png");
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "errors.json");
        Assert.Equal(2, handler.ProviderRequests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Method == HttpMethod.Put)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var page = Requests.Count == 2
                ? """
                  <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                    <IsTruncated>true</IsTruncated>
                    <NextContinuationToken>next-token</NextContinuationToken>
                    <Contents><Key>media/first.png</Key><LastModified>2026-08-10T10:00:00.000Z</LastModified><ETag>"etag-1"</ETag><Size>12</Size></Contents>
                  </ListBucketResult>
                  """
                : """
                  <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                    <IsTruncated>false</IsTruncated>
                    <Contents><Key>media/second.bin</Key><LastModified>2026-08-10T10:01:00.000Z</LastModified><ETag>"etag-2"</ETag><Size>4</Size></Contents>
                  </ListBucketResult>
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page),
            });
        }
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        public List<byte[]> PutBodies { get; } = [];
        public int ProviderRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "provider.test")
            {
                ProviderRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("provider-bytes"u8.ToArray())
                    {
                        Headers = { ContentType = new("image/png") },
                    },
                };
            }

            if (request.Method == HttpMethod.Put)
            {
                PutBodies.Add(request.Content is null
                    ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                    "\"archive-etag\"");
                return response;
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        }
    }
}
