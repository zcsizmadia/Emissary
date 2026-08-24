using System.Net;
using System.Text.Json;
using Emissary.OpenApi;
using Emissary.Tests.Agents;

namespace Emissary.Tests.OpenApi;

/// <summary>
/// A specification already says which operations read and which ones write, which is exactly the
/// information Emissary's taint tracking needs. These tests pin that the safety posture is derived
/// rather than configured, and that the requests come out the way the document describes them.
/// </summary>
public sealed class OpenApiToolsTests
{
    /// <summary>
    /// Path-level parameters, an operation-level query parameter, tags, a referenced request body,
    /// and a schema that references itself.
    /// </summary>
    private const string PetStore = """
    {
      "openapi": "3.0.3",
      "servers": [{ "url": "https://spec.example.com/api" }],
      "paths": {
        "/pets/{petId}": {
          "parameters": [
            { "name": "petId", "in": "path", "required": true,
              "schema": { "type": "integer" }, "description": "Which pet." }
          ],
          "get": {
            "operationId": "getPet",
            "summary": "Fetch a pet",
            "tags": ["pets"],
            "parameters": [{ "name": "verbose", "in": "query", "schema": { "type": "boolean" } }]
          },
          "delete": { "operationId": "deletePet", "tags": ["pets", "admin"] }
        },
        "/pets": {
          "post": {
            "operationId": "createPet",
            "tags": ["pets"],
            "requestBody": {
              "required": true,
              "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
            }
          }
        }
      },
      "components": {
        "schemas": {
          "Pet": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "friend": { "$ref": "#/components/schemas/Pet" }
            }
          }
        }
      }
    }
    """;

    private static ToolDefinition Tool(OpenApiToolSet set, string name) =>
        set.Tools.Single(t => t.Name == name);

    private static JsonElement Input(string json)
    {
        var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Test]
    public async Task Reads_taint_the_run_and_writes_are_privileged()
    {
        // The point of the whole package: having read a response body — content someone else
        // wrote — the agent cannot write back through the same API. Nobody configured that.
        var set = OpenApiTools.FromSpec(PetStore, new StubHttp().Client());

        await Assert.That(set.Tools.Select(t => t.Name))
            .IsEquivalentTo(["getPet", "deletePet", "createPet"]);

        var read = Tool(set, "getPet");
        await Assert.That(read.Untrusted).IsTrue();
        await Assert.That(read.Privileged).IsFalse();

        foreach (string write in new[] { "deletePet", "createPet" })
        {
            await Assert.That(Tool(set, write).Privileged).IsTrue();
            await Assert.That(Tool(set, write).Untrusted).IsFalse();
        }

        // And the posture is defaults, not dogma.
        var relaxed = OpenApiTools.FromSpec(
            PetStore,
            new StubHttp().Client(),
            new OpenApiToolOptions
            {
                ReadsAreUntrusted = false,
                WritesArePrivileged = false,
                WritePolicy = "pets:write",
            });

        await Assert.That(Tool(relaxed, "getPet").Untrusted).IsFalse();
        await Assert.That(Tool(relaxed, "createPet").Privileged).IsFalse();
        await Assert.That(Tool(relaxed, "createPet").RequiredPolicy).IsEqualTo("pets:write");
        await Assert.That(Tool(relaxed, "getPet").RequiredPolicy).IsNull();
    }

    [Test]
    public async Task A_tainted_run_cannot_reach_a_write_tool()
    {
        // The interlock, end to end through the agent rather than asserted on flags.
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(PetStore, http.Client());

        var options = new AgentOptions { Model = "claude-test-1" };
        foreach (var tool in set.Tools)
        {
            options.Tools.Add(tool);
        }

        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "getPet", """{"petId":7}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t2", "deletePet", """{"petId":7}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("I cannot delete after reading."));

        var result = await new ClaudeAgent(options, transport).RunAsync("read then delete pet 7");

        var blocked = result.Conversation.Messages
            .SelectMany(m => m.Content.OfType<ToolResultBlock>())
            .Last();
        await Assert.That(blocked.IsError).IsTrue();
        await Assert.That(blocked.Content).Contains("cannot run after untrusted content");
        await Assert.That(http.Requests).Count().IsEqualTo(1);   // the delete never left the process
    }

    [Test]
    public async Task Parameters_reach_the_url_where_the_document_says()
    {
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(PetStore, http.Client());

        string body = await Tool(set, "getPet")
            .InvokeAsync(Input("""{"petId":"a b/c","verbose":true}"""));

        // The base address keeps its own path segment, the path parameter is substituted and
        // escaped, and the query parameter is appended.
        // AbsoluteUri rather than ToString, which unescapes what was deliberately escaped.
        await Assert.That(http.LastUri.AbsoluteUri)
            .IsEqualTo("https://spec.example.com/api/pets/a%20b%2Fc?verbose=true");
        await Assert.That(http.Requests[0].Method.Method).IsEqualTo("GET");
        await Assert.That(body).IsEqualTo("""{"ok":true}""");
    }

    [Test]
    public async Task An_omitted_optional_parameter_is_omitted()
    {
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(PetStore, http.Client());

        await Tool(set, "getPet").InvokeAsync(Input("""{"petId":7,"verbose":null}"""));

        await Assert.That(http.LastUri.ToString()).IsEqualTo("https://spec.example.com/api/pets/7");
    }

    [Test]
    public async Task A_missing_required_parameter_is_reported_as_bad_tool_input()
    {
        var set = OpenApiTools.FromSpec(PetStore, new StubHttp().Client());

        var thrown = await Assert.ThrowsAsync<ToolArgumentException>(
            async () => await Tool(set, "getPet").InvokeAsync(Input("""{"verbose":true}""")));

        await Assert.That(thrown!.Message).Contains("GET /pets/{petId}");
        await Assert.That(thrown.Message).Contains("required parameter 'petId'");
    }

    [Test]
    public async Task An_array_query_parameter_repeats()
    {
        const string spec = """
        {
          "servers": [{ "url": "http://q.example.com" }],
          "paths": {
            "/search": {
              "get": {
                "operationId": "search",
                "parameters": [
                  { "name": "tag", "in": "query", "schema": { "type": "array" } },
                  { "name": "q", "in": "query", "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(spec, http.Client());

        await Tool(set, "search").InvokeAsync(Input("""{"tag":["a","b c"],"q":"x&y"}"""));

        await Assert.That(http.LastUri.Query).IsEqualTo("?tag=a&tag=b%20c&q=x%26y");
    }

    [Test]
    public async Task A_request_body_is_sent_as_json()
    {
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(PetStore, http.Client());

        await Tool(set, "createPet").InvokeAsync(Input("""{"body":{"name":"Rex"}}"""));

        await Assert.That(http.LastUri.ToString()).IsEqualTo("https://spec.example.com/api/pets");
        await Assert.That(http.Bodies[0]).IsEqualTo("""{"name":"Rex"}""");
        await Assert.That(http.Requests[0].Content!.Headers.ContentType!.MediaType)
            .IsEqualTo("application/json");
    }

    [Test]
    public async Task A_parameter_named_body_does_not_collide_with_the_request_body()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://c.example.com" }],
          "paths": {
            "/things": {
              "put": {
                "operationId": "put_thing",
                "parameters": [{ "name": "body", "in": "query", "schema": { "type": "string" } }],
                "requestBody": {
                  "content": { "application/merge-patch+json": { "schema": { "type": "object" } } }
                }
              }
            }
          }
        }
        """;
        var http = new StubHttp();
        var set = OpenApiTools.FromSpec(spec, http.Client());
        var tool = Tool(set, "put_thing");

        await Assert.That(tool.InputSchemaJson).Contains("request_body");
        await Assert.That(tool.InputSchemaJson).Contains("merge-patch");

        await tool.InvokeAsync(Input("""{"body":"query-one","request_body":{"real":true}}"""));
        await Assert.That(http.LastUri.Query).IsEqualTo("?body=query-one");
        await Assert.That(http.Bodies[0]).IsEqualTo("""{"real":true}""");
    }

    [Test]
    public async Task Non_success_and_empty_responses_are_reported_as_content()
    {
        // A 404 is information, not a crash: the model should read it and adapt.
        var http = new StubHttp { Status = HttpStatusCode.NotFound, Response = "no such pet" };
        var set = OpenApiTools.FromSpec(PetStore, http.Client());

        string missing = await Tool(set, "getPet").InvokeAsync(Input("""{"petId":7}"""));
        await Assert.That(missing).IsEqualTo("HTTP 404 Not Found: no such pet");

        http.Status = HttpStatusCode.NoContent;
        http.Response = "";
        string empty = await Tool(set, "deletePet").InvokeAsync(Input("""{"petId":7}"""));
        await Assert.That(empty).IsEqualTo("HTTP 204 No Content (no body)");
    }

    [Test]
    public async Task Operations_are_selected_by_tag_and_by_operation_id()
    {
        var byTag = new OpenApiToolOptions();
        byTag.Tags.Add("admin");
        await Assert.That(OpenApiTools.FromSpec(PetStore, new StubHttp().Client(), byTag)
            .Tools.Select(t => t.Name)).IsEquivalentTo(["deletePet"]);

        var byId = new OpenApiToolOptions();
        byId.OperationIds.Add("getPet");
        await Assert.That(OpenApiTools.FromSpec(PetStore, new StubHttp().Client(), byId)
            .Tools.Select(t => t.Name)).IsEquivalentTo(["getPet"]);

        // Conjunction: both filters must pass, so this selects nothing.
        var both = new OpenApiToolOptions();
        both.Tags.Add("admin");
        both.OperationIds.Add("getPet");
        await Assert.That(OpenApiTools.FromSpec(PetStore, new StubHttp().Client(), both).Tools).IsEmpty();
    }

    [Test]
    public async Task A_prefix_is_applied_and_the_result_set_reports_itself()
    {
        var options = new OpenApiToolOptions { Prefix = "pets_", MaxResultLength = 99 };
        var set = OpenApiTools.FromSpec(PetStore, new StubHttp().Client(), options);

        await Assert.That(set.Tools.Select(t => t.Name))
            .IsEquivalentTo(["pets_getPet", "pets_deletePet", "pets_createPet"]);
        await Assert.That(set.Tools[0].MaxResultLength).IsEqualTo(99);
        await Assert.That(set.ToText()).StartsWith("3 tools: pets_getPet, pets_deletePet");
        await Assert.That(set.ToText()).DoesNotContain("skipped");
    }

    [Test]
    public async Task Descriptions_fall_back_to_the_operation_itself()
    {
        var set = OpenApiTools.FromSpec(PetStore, new StubHttp().Client());

        await Assert.That(Tool(set, "getPet").Description).IsEqualTo("Fetch a pet (GET /pets/{petId})");
        await Assert.That(Tool(set, "deletePet").Description).IsEqualTo("DELETE /pets/{petId}");

        const string described = """
        {
          "servers": [{ "url": "https://d.example.com" }],
          "paths": { "/x": { "get": { "operationId": "x", "description": "The long form." } } }
        }
        """;
        var fallback = OpenApiTools.FromSpec(described, new StubHttp().Client());
        await Assert.That(fallback.Tools[0].Description).IsEqualTo("The long form. (GET /x)");
    }

    [Test]
    public async Task Header_and_cookie_parameters_are_never_exposed_to_the_model()
    {
        // A model that can set headers can set Authorization, so headers stay the client's business.
        // An operation that cannot work without one is reported instead of generated half-working.
        const string spec = """
        {
          "servers": [{ "url": "https://h.example.com" }],
          "paths": {
            "/optional": {
              "get": {
                "operationId": "optional_header",
                "parameters": [{ "name": "X-Trace", "in": "header", "schema": { "type": "string" } }]
              }
            },
            "/required": {
              "get": {
                "operationId": "required_header",
                "parameters": [
                  { "name": "X-Region", "in": "header", "required": true, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;
        var set = OpenApiTools.FromSpec(spec, new StubHttp().Client());

        await Assert.That(set.Tools.Select(t => t.Name)).IsEquivalentTo(["optional_header"]);
        await Assert.That(set.Tools[0].InputSchemaJson).DoesNotContain("X-Trace");
        await Assert.That(set.Skipped.Single().Operation).IsEqualTo("GET /required");
        await Assert.That(set.Skipped.Single().Reason).Contains("set it on the HttpClient instead");
        await Assert.That(set.ToText()).Contains("1 operation(s) skipped:");
        await Assert.That(set.ToText()).Contains("GET /required — requires header parameter 'X-Region'");
    }

    [Test]
    public async Task A_body_with_no_json_media_type_is_skipped()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://u.example.com" }],
          "paths": {
            "/upload": {
              "post": {
                "operationId": "upload",
                "requestBody": { "content": { "multipart/form-data": { "schema": { "type": "object" } } } }
              }
            },
            "/empty": { "post": { "operationId": "empty_body", "requestBody": { "required": true } } }
          }
        }
        """;
        var set = OpenApiTools.FromSpec(spec, new StubHttp().Client());

        await Assert.That(set.Tools).IsEmpty();
        await Assert.That(set.Skipped.Select(s => s.Operation))
            .IsEquivalentTo(["POST /upload", "POST /empty"]);
        await Assert.That(set.Skipped[0].Reason).IsEqualTo("its request body has no JSON media type.");
    }

    [Test]
    public async Task Too_many_tools_is_a_specification_error()
    {
        var options = new OpenApiToolOptions { MaxTools = 2 };

        var thrown = Assert.Throws<OpenApiSpecException>(
            () => OpenApiTools.FromSpec(PetStore, new StubHttp().Client(), options));

        await Assert.That(thrown!.Message).Contains("produced 3 tools, over the limit of 2");
        await Assert.That(thrown.Message).Contains("Tags or OperationIds");
    }

    [Test]
    public async Task The_address_comes_from_options_then_the_client_then_the_document()
    {
        var http = new StubHttp();

        var fromOptions = OpenApiTools.FromSpec(
            PetStore,
            http.Client("https://client.example.com/"),
            new OpenApiToolOptions { BaseAddress = new Uri("https://explicit.example.com/") });
        await Tool(fromOptions, "getPet").InvokeAsync(Input("""{"petId":1}"""));
        await Assert.That(http.LastUri.Host).IsEqualTo("explicit.example.com");

        var fromClient = OpenApiTools.FromSpec(PetStore, http.Client("https://client.example.com/"));
        await Tool(fromClient, "getPet").InvokeAsync(Input("""{"petId":1}"""));
        await Assert.That(http.LastUri.Host).IsEqualTo("client.example.com");

        var fromSpec = OpenApiTools.FromSpec(PetStore, http.Client());
        await Tool(fromSpec, "getPet").InvokeAsync(Input("""{"petId":1}"""));
        await Assert.That(http.LastUri.Host).IsEqualTo("spec.example.com");
    }

    [Test]
    public async Task An_unaddressable_specification_says_so()
    {
        // Every way a `servers` entry can fail to be an address: not an object at all, unparseable,
        // a scheme nothing can be sent to, and a relative path — which parses as an absolute file
        // URI on Unix and not at all on Windows, so it must be rejected on its scheme either way.
        const string spec = """
        {
          "servers": [
            "not-an-object",
            { "url": "://bad" },
            { "url": "ftp://files.example.com" },
            { "url": "/relative-only" }
          ],
          "paths": { "/x": { "get": { "operationId": "x" } } }
        }
        """;

        var thrown = Assert.Throws<OpenApiSpecException>(
            () => OpenApiTools.FromSpec(spec, new StubHttp().Client()));

        await Assert.That(thrown!.Message).Contains("No address to send requests to");
    }

    [Test]
    public async Task An_unreadable_specification_says_so()
    {
        var client = new StubHttp().Client();

        var notJson = Assert.Throws<OpenApiSpecException>(() => OpenApiTools.FromSpec("{ nope", client));
        await Assert.That(notJson!.Message).Contains("not valid JSON");
        await Assert.That(notJson.InnerException).IsTypeOf<JsonException>();

        var noPaths = Assert.Throws<OpenApiSpecException>(() => OpenApiTools.FromSpec("[]", client));
        await Assert.That(noPaths!.Message).Contains("no 'paths' object");

        await Assert.That(() => OpenApiTools.FromSpec(null!, client)).Throws<ArgumentNullException>();
        await Assert.That(() => OpenApiTools.FromSpec("{}", null!)).Throws<ArgumentNullException>();
        await Assert.That(new OpenApiSpecException().Message).Contains("could not be read");
    }

    [Test]
    public async Task Extensions_and_unsupported_verbs_are_ignored()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://x.example.com" }],
          "paths": {
            "x-vendor-note": "not a path item",
            "/x": {
              "get": { "operationId": "x" },
              "options": { "operationId": "preflight" },
              "summary": "a string, not an operation"
            }
          }
        }
        """;
        var set = OpenApiTools.FromSpec(spec, new StubHttp().Client());

        await Assert.That(set.Tools.Select(t => t.Name)).IsEquivalentTo(["x"]);
        await Assert.That(set.Skipped).IsEmpty();
    }

    [Test]
    public async Task Names_are_sanitized_deduplicated_and_truncated()
    {
        string longId = new('a', 70);
        string spec = $$"""
        {
          "servers": [{ "url": "https://n.example.com" }],
          "paths": {
            "/a/{id}": { "get": {}, "post": {} },
            "/b": { "get": { "operationId": "list pets/now" } },
            "/dup1": { "get": { "operationId": "dup" } },
            "/dup2": { "get": { "operationId": "dup" } },
            "/c": { "get": { "operationId": "{{longId}}1" } },
            "/d": { "get": { "operationId": "{{longId}}2" } }
          }
        }
        """;
        var set = OpenApiTools.FromSpec(spec, new StubHttp().Client());

        var names = set.Tools.Select(t => t.Name).ToList();

        // No operation id: the method and path become one, sanitized.
        await Assert.That(names).Contains("get__a__id_");
        await Assert.That(names).Contains("post__a__id_");
        await Assert.That(names).Contains("list_pets_now");

        // A specification is allowed to repeat an operation id, and a tool set is not.
        await Assert.That(names).Contains("dup");
        await Assert.That(names).Contains("dup_2");

        // Over-long ids are cut to the 64 characters the API allows, which makes these two collide;
        // the second gets a suffix that still fits.
        await Assert.That(names).Contains(new string('a', 64));
        await Assert.That(names).Contains(new string('a', 62) + "_2");
        await Assert.That(names.Max(n => n.Length)).IsEqualTo(64);
    }
}
