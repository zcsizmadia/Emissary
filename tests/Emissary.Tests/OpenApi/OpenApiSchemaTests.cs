using System.Text.Json.Nodes;
using Emissary.OpenApi;

namespace Emissary.Tests.OpenApi;

/// <summary>
/// Claude's tool schemas are self-contained; a specification's are not. These pin the translation
/// between them, including what happens to the shapes JSON Schema can express and one inlined
/// document cannot.
/// </summary>
public sealed class OpenApiSchemaTests
{
    private static ToolDefinition Single(string spec, OpenApiToolOptions? options = null) =>
        OpenApiTools.FromSpec(spec, new StubHttp().Client(), options).Tools.Single();

    /// <summary>Lets the expected schema be written readably and compared exactly.</summary>
    private static string Compact(string json) => JsonNode.Parse(json)!.ToJsonString();

    [Test]
    public async Task A_parameters_description_is_carried_onto_its_schema()
    {
        // A specification documents the parameter; the model reads the schema. Move it across, and
        // do not overwrite a description the schema already has.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/pets/{petId}": {
              "get": {
                "operationId": "getPet",
                "parameters": [
                  { "name": "petId", "in": "path", "required": true, "description": "Which pet.",
                    "schema": { "type": "integer" } },
                  { "name": "verbose", "in": "query", "description": "ignored",
                    "schema": { "type": "boolean", "description": "kept" } },
                  { "name": "raw", "in": "query" }
                ]
              }
            }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "petId": { "description": "Which pet.", "type": "integer" },
            "verbose": { "type": "boolean", "description": "kept" },
            "raw": { "type": "string" }
          },
          "required": ["petId"],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task References_are_expanded_and_a_cycle_degrades_to_an_open_object()
    {
        // `friend` refers to Pet, which contains `friend`. Nothing self-contained can describe that,
        // so it becomes an object that says what it is.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/pets": {
              "post": {
                "operationId": "createPet",
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
                  "friend": { "$ref": "#/components/schemas/Pet" },
                  "litter": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } }
                }
              }
            }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "body": {
              "description": "Request body (application/json).",
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "friend": {
                  "type": "object",
                  "description": "Recursive reference to #/components/schemas/Pet; shape repeats."
                },
                "litter": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "description": "Recursive reference to #/components/schemas/Pet; shape repeats."
                  }
                }
              }
            }
          },
          "required": ["body"],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task References_inside_keyword_arrays_are_expanded_too()
    {
        // `oneOf` and friends hold schemas in an array, and a specification will reference from
        // inside one. Arrays that are plain data — a `required` list — come across unchanged.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/pets": {
              "post": {
                "operationId": "createPet",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "required": ["kind"],
                        "oneOf": [
                          { "$ref": "#/components/schemas/Cat" },
                          { "type": "null" }
                        ]
                      }
                    }
                  }
                }
              }
            }
          },
          "components": { "schemas": { "Cat": { "type": "object", "title": "Cat" } } }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "body": {
              "description": "Request body (application/json).",
              "required": ["kind"],
              "oneOf": [{ "type": "object", "title": "Cat" }, { "type": "null" }]
            }
          },
          "required": [],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task A_referenced_parameter_and_request_body_resolve()
    {
        // Real specifications keep parameters and bodies in components too, not just schemas.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/pets/{petId}": {
              "parameters": [{ "$ref": "#/components/parameters/PetId" }],
              "put": {
                "operationId": "replacePet",
                "requestBody": { "$ref": "#/components/requestBodies/PetBody" }
              }
            }
          },
          "components": {
            "parameters": {
              "PetId": { "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } }
            },
            "requestBodies": {
              "PetBody": { "content": { "application/json": { "schema": { "type": "object" } } } }
            }
          }
        }
        """;

        // The referenced body is not marked required, so it stays optional.
        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "petId": { "type": "integer" },
            "body": { "description": "Request body (application/json).", "type": "object" }
          },
          "required": ["petId"],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task An_operation_parameter_overrides_the_path_level_one_of_the_same_name()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": {
              "parameters": [{ "name": "q", "in": "query", "schema": { "type": "string" } }],
              "get": {
                "operationId": "x",
                "parameters": [
                  { "name": "q", "in": "query", "required": true, "schema": { "type": "integer" } }
                ]
              }
            }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": { "q": { "type": "integer" } },
          "required": ["q"],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task Examples_are_data_and_are_copied_untouched()
    {
        // A "$ref" key inside an example is an example. Expanding it would be a bug, and resolving
        // it would throw.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": {
              "get": {
                "operationId": "x",
                "parameters": [
                  { "name": "q", "in": "query",
                    "schema": { "type": "string", "example": { "$ref": "nonsense" },
                                "enum": ["a", "b"], "default": "a" } }
                ]
              }
            }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "q": {
              "type": "string",
              "example": { "$ref": "nonsense" },
              "enum": ["a", "b"],
              "default": "a"
            }
          },
          "required": [],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task A_pointer_with_escaped_segments_resolves()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": {
              "get": {
                "operationId": "x",
                "parameters": [
                  { "name": "q", "in": "query", "schema": { "$ref": "#/components/schemas/a~1b~0c" } }
                ]
              }
            }
          },
          "components": { "schemas": { "a/b~c": { "type": "string", "format": "date" } } }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).Contains(Compact("""
        { "type": "string", "format": "date" }
        """));
    }

    [Test]
    public async Task A_reference_that_does_not_resolve_is_a_specification_error()
    {
        const string missing = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": { "get": { "operationId": "x", "parameters": [
              { "name": "q", "in": "query", "schema": { "$ref": "#/components/schemas/Gone" } }
            ] } }
          }
        }
        """;

        var thrown = Assert.Throws<OpenApiSpecException>(() => Single(missing));
        await Assert.That(thrown!.Message)
            .IsEqualTo("Reference '#/components/schemas/Gone' does not resolve.");

        // Neither does a pointer that walks into a scalar.
        const string throughScalar = """
        {
          "openapi": "3.0.3",
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": { "get": { "operationId": "x", "parameters": [
              { "name": "q", "in": "query", "schema": { "$ref": "#/openapi/major" } }
            ] } }
          }
        }
        """;
        await Assert.That(Assert.Throws<OpenApiSpecException>(() => Single(throughScalar))!.Message)
            .Contains("does not resolve");
    }

    [Test]
    public async Task A_remote_reference_is_refused_rather_than_fetched()
    {
        // Reading a specification should not make network calls.
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": { "get": { "operationId": "x", "parameters": [
              { "name": "q", "in": "query",
                "schema": { "$ref": "https://other.example.com/schemas.json#/Pet" } }
            ] } }
          }
        }
        """;

        var thrown = Assert.Throws<OpenApiSpecException>(() => Single(spec));

        await Assert.That(thrown!.Message).Contains("is not a local reference");
        await Assert.That(thrown.Message).Contains("Bundle the specification");
    }

    [Test]
    public async Task A_parameter_with_no_name_or_location_is_ignored()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": { "get": { "operationId": "x", "parameters": [
              { "in": "query", "schema": { "type": "string" } },
              { "name": "nowhere", "schema": { "type": "string" } },
              { "name": "q", "in": "query", "schema": { "type": "string" } }
            ] } }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": { "q": { "type": "string" } },
          "required": [],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task A_body_that_declares_no_schema_accepts_an_object()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": {
            "/x": {
              "post": {
                "operationId": "x",
                "requestBody": { "required": true, "content": { "application/json": {} } }
              }
            }
          }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        {
          "type": "object",
          "properties": {
            "body": { "description": "Request body (application/json).", "type": "object" }
          },
          "required": ["body"],
          "additionalProperties": false
        }
        """));
    }

    [Test]
    public async Task An_operation_with_no_parameters_still_produces_a_schema()
    {
        const string spec = """
        {
          "servers": [{ "url": "https://s.example.com" }],
          "paths": { "/health": { "get": { "operationId": "health" } } }
        }
        """;

        await Assert.That(Single(spec).InputSchemaJson).IsEqualTo(Compact("""
        { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
        """));
    }
}
