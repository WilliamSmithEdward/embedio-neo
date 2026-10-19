using System;
using System.Text.Json.Nodes;

namespace EmbedIO
{
    partial class HttpResultMetadata
    {
        internal static int FixedProblemStatus(Type type) =>
            type == typeof(BadRequestProblemResult) || type == typeof(ValidationProblemResult)
                ? 400
                : type == typeof(NotFoundProblemResult) ? 404 : type == typeof(ConflictProblemResult) ? 409 : 0;

        internal static JsonObject ProblemSchema(Type type)
        {
            var status = FixedProblemStatus(type);
            var statusSchema = new JsonObject { ["type"] = "integer" };

            if (status != 0) statusSchema["const"] = status;

            else
            {
                statusSchema["minimum"] = 400;
                statusSchema["maximum"] = 599;
            }

            var properties = new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string" },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["status"] = statusSchema,
                ["detail"] = new JsonObject { ["type"] = "string" },
                ["instance"] = new JsonObject { ["type"] = "string" }
            };
            var requiredMembers = new JsonArray("status");

            if (type == typeof(ValidationProblemResult))
            {
                properties["errors"] = new JsonObject
                {
                    ["type"] = "object",
                    ["minProperties"] = 1,
                    ["maxProperties"] = ValidationProblemData.MaxErrors,
                    ["propertyNames"] = new JsonObject { ["maxLength"] = ValidationProblemData.MaxPathLength },
                    ["additionalProperties"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = ValidationProblemData.MaxErrors,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = ValidationProblemData.MaxMessageLength
                        }
                    }
                };
                requiredMembers.Add((JsonNode?)JsonValue.Create("errors"));
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = requiredMembers,
                ["additionalProperties"] = true
            };
        }
    }
}
