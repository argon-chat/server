namespace ArgonSharedLogicTest;

using System.Reflection;
using System.Text.Json;
using Argon.Features.BotApi;

/// <summary>
/// The OpenAPI document is what integrators generate their clients from, and it is generated from
/// the endpoints the server actually maps — so these assertions are about the API surface itself,
/// not about a description of it that someone maintained by hand.
/// <para>
/// Generation needs no database and no cluster: it maps the interfaces into a throwaway host purely
/// to read their metadata back.
/// </para>
/// </summary>
[TestFixture]
public class BotOpenApiTests
{
    private JsonDocument document = null!;
    private JsonElement  paths;

    [OneTimeSetUp]
    public void GenerateDocument()
    {
        // Interfaces are discovered across loaded assemblies; touch one so Argon.Api is loaded.
        _ = typeof(Argon.Api.BotApi.Interfaces.MessagesV1);

        document = JsonDocument.Parse(BotOpenApi.GenerateOfflineAsync().GetAwaiter().GetResult());
        paths    = document.RootElement.GetProperty("paths");
    }

    [OneTimeTearDown]
    public void Dispose() => document.Dispose();

    private IEnumerable<(string Path, string Method, JsonElement Operation)> Operations()
    {
        foreach (var path in paths.EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
            yield return (path.Name, operation.Name, operation.Value);
    }

    [Test]
    public void EveryInterfaceExposesOperations()
    {
        var declared = typeof(IBotInterface).Assembly.GetTypes()
           .Concat(typeof(Argon.Api.BotApi.Interfaces.MessagesV1).Assembly.GetTypes())
           .Where(t => typeof(IBotInterface).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
           .Select(t => t.GetCustomAttribute<BotInterfaceAttribute>()?.Name)
           .OfType<string>()
           .Distinct();

        var described = Operations()
           .SelectMany(o => o.Operation.GetProperty("tags").EnumerateArray().Select(t => t.GetString()))
           .OfType<string>()
           .ToHashSet();

        Assert.That(declared.Except(described), Is.Empty,
            "an interface with no operations in the document is one nobody can generate a client for");
    }

    [Test]
    public void OperationIdsAreUniqueAndNameTheirInterface()
    {
        var ids = Operations()
           .Select(o => o.Operation.TryGetProperty("operationId", out var id) ? id.GetString() : null)
           .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Has.None.Null.Or.Empty, "a generated client names its methods after these");
            Assert.That(ids, Is.Unique);
            Assert.That(ids, Is.All.Matches<string>(id => id.Contains("_v")),
                "the interface version belongs in the operation id — two versions of one route coexist");
        });
    }

    [Test]
    public void PathsAreWrittenAsClientsCallThem()
    {
        // The gateway publishes the API at the root of its own host and adds /api/bot on the way in.
        // A document carrying the internal prefix would send every generated client to a 404.
        Assert.That(paths.EnumerateObject().Select(p => p.Name),
            Is.All.Matches<string>(p => !p.StartsWith("/api/bot", StringComparison.Ordinal)));
    }

    [Test]
    public void ArraysDeclareTheirItemSchema()
    {
        var offenders = new List<string>();
        Walk(document.RootElement, "", offenders);

        Assert.That(offenders, Is.Empty,
            "an array without an item schema is invalid OpenAPI 3.0 and breaks client generators");

        static void Walk(JsonElement node, string path, List<string> offenders)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    if (node.TryGetProperty("type", out var type) && type.ValueKind is JsonValueKind.String
                     && type.GetString() == "array" && !node.TryGetProperty("items", out _))
                        offenders.Add(path);

                    foreach (var property in node.EnumerateObject())
                        Walk(property.Value, $"{path}/{property.Name}", offenders);
                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in node.EnumerateArray())
                        Walk(item, $"{path}/{index++}", offenders);
                    break;
            }
        }
    }

    [Test]
    public void EveryOperationIsAuthenticatedAndRateLimited()
    {
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("components").GetProperty("securitySchemes")
               .EnumerateObject().Select(s => s.Name), Does.Contain("BotToken"));

            foreach (var (path, method, operation) in Operations())
            {
                var statuses = operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToList();

                Assert.That(statuses, Does.Contain("401"), $"{method.ToUpperInvariant()} {path}");
                Assert.That(statuses, Does.Contain("429"), $"{method.ToUpperInvariant()} {path}");
            }
        });
    }

    [Test]
    public void EveryOperationIsSummarised()
    {
        // The summary is the one-line description a reference page and a generated client both
        // show. A route that maps without one documents itself as nothing.
        foreach (var (path, method, operation) in Operations())
            Assert.That(operation.TryGetProperty("summary", out var summary) && summary.GetString()?.Length > 0,
                $"{method.ToUpperInvariant()} {path} has no summary");
    }

    [Test]
    public void PermissionsAndErrorsTravelAsStructuredExtensions()
    {
        // The documentation site reads these rather than parsing the prose in `description`, so a
        // rename that silently drops them would empty its tables.
        var subscribe = paths.GetProperty("/IVoiceEgress/v20260401/SubscribeTrack").GetProperty("post");

        Assert.Multiple(() =>
        {
            Assert.That(subscribe.GetProperty("x-argon-permission").GetString(), Is.EqualTo("Connect"));
            Assert.That(subscribe.GetProperty("x-argon-privileged").GetBoolean(), Is.True);
            Assert.That(subscribe.GetProperty("x-argon-errors").EnumerateArray()
               .Select(e => e.GetProperty("code").GetString()),
                Does.Contain("not_verified").And.Contains("channel_not_found"));
        });
    }

    [Test]
    public void AFormRouteDescribesAMultipartBodyWithItsFiles()
    {
        var addItem = paths.GetProperty("/IExpressions/v1/AddItem").GetProperty("post");
        var content = addItem.GetProperty("requestBody").GetProperty("content");

        Assert.That(content.EnumerateObject().Select(c => c.Name), Is.EqualTo(new[] { "multipart/form-data" }));

        var request = Resolve(content.GetProperty("multipart/form-data").GetProperty("schema"));
        var file    = Resolve(request.GetProperty("properties").GetProperty("file"));
        var errors  = addItem.GetProperty("x-argon-errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(request.GetProperty("required").EnumerateArray().Select(r => r.GetString()),
                Is.SupersetOf(new[] { "spaceId", "packId", "name", "file" }));
            Assert.That((file.GetProperty("type").GetString(), file.GetProperty("format").GetString()), Is.EqualTo(("string", "binary")));
            Assert.That(file.GetProperty("description").GetString(), Does.Contain("attach://"));
            Assert.That(request.GetProperty("properties").TryGetProperty("thumb", out _), Is.False, "bots send no thumbnail");
            Assert.That(request.GetProperty("additionalProperties").GetProperty("format").GetString(), Is.EqualTo("binary"),
                "the parts attach:// references name are part of the body too");
            Assert.That(errors, Is.SupersetOf(new[]
            {
                "invalid_request", "too_large", "invalid_format", "content_rejected", "space_rate_limited", "render_unavailable"
            }));
            Assert.That(addItem.GetProperty("responses").GetProperty("429").GetProperty("description").GetString(),
                Does.Contain("`space_rate_limited`").And.Contain("`rate_limited`"), "a route's own 429 is not overwritten by the limiter's");
        });
    }

    [Test]
    public void SendTakesJsonOrAMultipartFormWithFiles()
    {
        var send    = paths.GetProperty("/IMessages/v1/Send").GetProperty("post");
        var content = send.GetProperty("requestBody").GetProperty("content");
        var request = Resolve(content.GetProperty("application/json").GetProperty("schema"));
        var files   = request.GetProperty("properties").GetProperty("attachments");
        var errors  = send.GetProperty("x-argon-errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        var upload  = Resolve(paths.GetProperty("/IFiles/v1/Upload").GetProperty("post").GetProperty("requestBody").GetProperty("content")
           .GetProperty("multipart/form-data").GetProperty("schema"));
        var purpose = Resolve(upload.GetProperty("properties").GetProperty("purpose"));

        Assert.Multiple(() =>
        {
            Assert.That(content.EnumerateObject().Select(c => c.Name), Is.EquivalentTo(new[] { "application/json", "multipart/form-data" }));
            Assert.That(content.GetProperty("multipart/form-data").GetProperty("schema").GetProperty("$ref").GetString(),
                Does.EndWith("/SendMessageRequest"), "one request, two encodings");
            Assert.That(files.GetProperty("type").GetString(), Is.EqualTo("array"));
            Assert.That(Resolve(files.GetProperty("items")).GetProperty("description").GetString(), Does.Contain("fileId"));
            Assert.That(errors, Is.SupersetOf(new[] { "invalid_request", "too_large", "not_found", "insufficient_permissions", "invalid_format" }));
            Assert.That(purpose.GetProperty("enum").EnumerateArray().Select(e => e.GetString()),
                Is.EqualTo(new[] { "sticker", "emoji", "attachment" }));
        });
    }

    [Test]
    public void CustomReactionsDocumentTheirPrivilegedMode()
    {
        var add = paths.GetProperty("/IReactions/v1/AddCustom").GetProperty("post");

        Assert.Multiple(() =>
        {
            Assert.That(add.GetProperty("summary").GetString(), Does.Contain("verified"));
            Assert.That(add.GetProperty("x-argon-permission").GetString(), Is.EqualTo("AddReactions"));
            Assert.That(add.GetProperty("responses").GetProperty("403").GetProperty("description").GetString(),
                Does.Contain("`insufficient_permissions`"));
            Assert.That(add.GetProperty("responses").GetProperty("404").GetProperty("description").GetString(),
                Does.Contain("`not_found`").And.Contain("`message_not_found`"));
            Assert.That(paths.TryGetProperty("/IReactions/v1/RemoveCustom", out _), Is.True);
        });
    }

    [Test]
    public void ExpressionEnumsAreStrings()
    {
        var kind = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("BotExpressionKind");

        Assert.Multiple(() =>
        {
            Assert.That(kind.GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(kind.GetProperty("enum").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "sticker", "emoji" }));
        });
    }

    [Test]
    public void BotsAreGivenNoRouteThatDeletesOrReordersExpressions()
    {
        var operations = Operations().Where(o => o.Path.StartsWith("/IExpressions/", StringComparison.Ordinal)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(operations, Is.Not.Empty);
            Assert.That(operations.Select(o => o.Path), Has.None.Contains("Delete").And.None.Contains("Reorder"));
            Assert.That(operations.Select(o => o.Method), Has.None.EqualTo("delete"));
            Assert.That(operations.Where(o => o.Operation.TryGetProperty("x-argon-privileged", out _)).Select(o => o.Path), Is.Empty,
                "no expression route is privileged");
            Assert.That(operations.SelectMany(o => o.Operation.TryGetProperty("x-argon-errors", out var e) ? e.EnumerateArray() : [])
                   .Select(e => e.GetProperty("code").GetString()), Has.None.EqualTo("not_verified"),
                "no expression route is for verified bots only");
        });
    }

    [Test]
    public void EveryExpressionErrorReachesABotAsADeclaredError()
    {
        // An unmapped value would come back as a 500; AddItem can produce every one of them.
        var declared = paths.GetProperty("/IExpressions/v1/AddItem").GetProperty("post").GetProperty("x-argon-errors")
           .EnumerateArray().Select(e => (e.GetProperty("status").GetInt32(), e.GetProperty("code").GetString())).ToList();

        foreach (var error in Enum.GetValues<ArgonContracts.ExpressionError>().Where(e => e is not ArgonContracts.ExpressionError.NONE))
        {
            var mapped = Argon.Api.BotApi.Interfaces.ExpressionBotErrors.For(error);
            Assert.That(declared, Does.Contain((mapped.Status, mapped.Code)), $"{error} maps to an error AddItem does not declare");
        }
    }

    private JsonElement Resolve(JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
            return schema;

        var name = reference.GetString()!.Split('/')[^1];
        return document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    [Test]
    public void RoutesBuiltWithTheTypedBuilderCarryTheirDocumentation()
    {
        // MessagesV1 is the one migrated to the typed route builder: its summary, its permission and
        // its response schema all reach the document from the call that maps the route.
        var send = paths.GetProperty("/IMessages/v1/Send").GetProperty("post");

        Assert.Multiple(() =>
        {
            Assert.That(send.GetProperty("summary").GetString(), Is.Not.Empty);
            Assert.That(send.GetProperty("description").GetString(), Does.Contain("SendMessages"));
            Assert.That(send.GetProperty("requestBody").GetProperty("content")
               .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString(),
                Does.EndWith("/SendMessageRequest"));
            Assert.That(send.GetProperty("responses").GetProperty("200").GetProperty("content")
               .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString(),
                Does.EndWith("/SendMessageResponse"));
        });
    }
}
