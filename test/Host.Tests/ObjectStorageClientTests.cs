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
}
