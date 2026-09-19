using Comgenie.Util;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler
    {
        // Protocol versions of the Model Context Protocol we accept (newest first). The client requested
        // version is echoed back when supported, otherwise we advertise the newest one we support.
        private static readonly string[] McpSupportedProtocolVersions = new string[] { "2025-06-18", "2025-03-26", "2024-10-07" };

        private static readonly JsonSerializerOptions McpJsonOptions = new JsonSerializerOptions()
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        /// <summary>
        /// Expose all public methods of the given mcpApplication class as MCP tools, and register a single
        /// endpoint (streamable HTTP transport) where an MCP v1 client can talk JSON-RPC to the server.
        /// Use the [McpTool] and [McpParam] attributes to customize the name/description of tools and parameters.
        /// </summary>
        /// <param name="domain">Domain to register the MCP endpoint on</param>
        /// <param name="path">Path of the MCP endpoint, e.g. "/mcp"</param>
        /// <param name="mcpApplication">Object of which the public methods will be exposed as MCP tools</param>
        /// <param name="setCorsToAllowAll">Set to true to add permissive CORS headers to all responses and automatically answer OPTIONS preflight requests. Useful when the MCP endpoint is accessed from a browser based client.</param>
        public void AddMcpRoute(string domain, string path, object mcpApplication, bool setCorsToAllowAll = false)
        {
            // Expose all public methods of the given mcpApplication class as MCP tools, and register a single
            // endpoint (streamable HTTP transport) where an MCP v1 client can talk JSON-RPC to the server.
            // Use the [McpTool] and [McpParam] attributes (see below) to customize the name/description of tools and parameters.
            var tools = new Dictionary<string, McpToolDescriptor>();
            var publicMethods = mcpApplication.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var ignoreMethods = typeof(object).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(a => a.Name).ToArray();

            foreach (var method in publicMethods)
            {
                if (ignoreMethods.Contains(method.Name) || method.IsSpecialName)
                    continue; // Skip object methods plus property/event accessors and operators

                var toolAttribute = method.GetCustomAttribute<McpToolAttribute>();
                if (toolAttribute != null && !toolAttribute.Enabled)
                    continue;

                var toolName = toolAttribute?.Name;
                if (string.IsNullOrWhiteSpace(toolName))
                    toolName = SanitizeMcpToolName(method.Name);
                if (toolName.Length == 0 || tools.ContainsKey(toolName))
                    continue; // Duplicate tool name (e.g. method overload), first one wins

                var methodParameters = method.GetParameters();
                tools.Add(toolName, new McpToolDescriptor()
                {
                    Name = toolName,
                    Title = toolAttribute?.Title,
                    Description = toolAttribute?.Description,
                    Method = method,
                    Parameters = methodParameters,
                    InputSchema = BuildMcpInputSchema(methodParameters)
                });
            }

            var serverName = mcpApplication.GetType().Name;
            var serverVersion = mcpApplication.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0";

            var route = new Route()
            {
                HandleExecuteRequestAsync = async (client, data, cancellationToken) =>
                {
                    if (setCorsToAllowAll && data.Method == "OPTIONS")
                    {
                        // CORS preflight request, respond with the allowed methods and headers
                        var preflight = new HttpResponse() { StatusCode = 204, ContentType = "" };
                        ApplyMcpCorsHeaders(preflight);
                        return preflight;
                    }

                    var response = await HandleMcpRequestAsync(tools, mcpApplication, serverName, serverVersion, data, cancellationToken);
                    if (setCorsToAllowAll && response != null)
                        ApplyMcpCorsHeaders(response);
                    return response;
                }
            };
            AddRoute(domain, path, route);
        }

        private static void ApplyMcpCorsHeaders(HttpResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Authorization, MCP-Protocol-Version, Last-Event-ID, Mcp-Session-Id";
            response.Headers["Access-Control-Expose-Headers"] = "Content-Type, MCP-Protocol-Version, Last-Event-ID, Mcp-Session-Id";
            response.Headers["Access-Control-Max-Age"] = "86400";
        }

        private async Task<HttpResponse?> HandleMcpRequestAsync(Dictionary<string, McpToolDescriptor> tools, object application, string serverName, string serverVersion, HttpClientData data, CancellationToken cancellationToken)
        {
            // Stateless streamable HTTP transport: every JSON-RPC message is posted to this endpoint and
            // answered directly with a JSON response. No SSE stream or sessions are offered.
            if (data.Method != "POST")
                return new HttpResponse(405, BuildMcpError(null, -32000, "This MCP endpoint only accepts POST requests"));

            if (data.DataStream == null || data.DataLength <= 0)
                return new HttpResponse(400, BuildMcpError(null, -32700, "Parse error: empty request"));

            JsonElement root;
            try
            {
                data.DataStream.Position = 0;
                root = await JsonSerializer.DeserializeAsync<JsonElement>(data.DataStream, McpJsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return new HttpResponse(400, BuildMcpError(null, -32700, "Parse error: invalid JSON"));
            }

            // Support both single messages and JSON-RPC batches
            var isBatch = root.ValueKind == JsonValueKind.Array;
            var incoming = new List<JsonElement>();
            if (isBatch)
                incoming.AddRange(root.EnumerateArray());
            else
                incoming.Add(root);

            var responses = new List<object?>();
            foreach (var item in incoming)
            {
                var response = await ProcessMcpMessageAsync(tools, application, serverName, serverVersion, item, data, cancellationToken);
                if (response != null)
                    responses.Add(response);
            }

            if (responses.Count == 0)
            {
                // Only notifications received. Per the MCP spec we must acknowledge with 202 Accepted and no body.
                // Note: .Data is set to an empty array so the handler sends an explicit Content-Length: 0 header,
                // without it some clients (e.g. llama.cpp) keep waiting for a response body on the open connection.
                return new HttpResponse() { StatusCode = 202, ContentType = "text/plain", Data = Array.Empty<byte>() };
            }
            if (!isBatch)
                return new HttpResponse() { ResponseObject = responses[0] };
            return new HttpResponse() { ResponseObject = responses };
        }

        private async Task<object?> ProcessMcpMessageAsync(Dictionary<string, McpToolDescriptor> tools, object application, string serverName, string serverVersion, JsonElement item, HttpClientData data, CancellationToken cancellationToken)
        {
            // Returns null for notifications (which get no JSON-RPC response back)
            if (item.ValueKind != JsonValueKind.Object)
                return BuildMcpError(null, -32600, "Invalid Request");

            string? method = null;
            if (item.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
                method = methodElement.GetString();
            JsonElement? id = null;
            if (item.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
                id = idElement;

            if (method == null)
                return BuildMcpError(id, -32600, "Invalid Request: no method specified");
            if (id == null)
                return null; // JSON-RPC notification (no id), never responded to

            item.TryGetProperty("params", out var parameters);

            switch (method)
            {
                case "initialize":
                {
                    string? requestedVersion = null;
                    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var versionElement) && versionElement.ValueKind == JsonValueKind.String)
                        requestedVersion = versionElement.GetString();
                    var negotiatedVersion = requestedVersion != null && McpSupportedProtocolVersions.Contains(requestedVersion)
                        ? requestedVersion
                        : McpSupportedProtocolVersions[0];
                    return BuildMcpResult(id, new Dictionary<string, object?>()
                    {
                        ["protocolVersion"] = negotiatedVersion,
                        ["capabilities"] = new Dictionary<string, object?>()
                        {
                            ["tools"] = new Dictionary<string, object?>() { ["listChanged"] = false }
                        },
                        ["serverInfo"] = new Dictionary<string, object?>()
                        {
                            ["name"] = serverName,
                            ["version"] = serverVersion
                        }
                    });
                }

                case "ping":
                    return BuildMcpResult(id, new Dictionary<string, object?>());

                case "tools/list":
                {
                    var toolList = tools.Values.Select(tool =>
                    {
                        var toolInfo = new Dictionary<string, object?>()
                        {
                            ["name"] = tool.Name
                        };
                        if (!string.IsNullOrEmpty(tool.Title))
                            toolInfo["title"] = tool.Title;
                        if (!string.IsNullOrEmpty(tool.Description))
                            toolInfo["description"] = tool.Description;
                        toolInfo["inputSchema"] = tool.InputSchema;
                        return (object)toolInfo;
                    }).ToList();
                    return BuildMcpResult(id, new Dictionary<string, object?>() { ["tools"] = toolList });
                }

                case "tools/call":
                {
                    string? toolName = null;
                    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                        toolName = nameElement.GetString();
                    if (string.IsNullOrEmpty(toolName))
                        return BuildMcpError(id, -32602, "Invalid params: no tool name specified");
                    if (!tools.TryGetValue(toolName, out var tool))
                        return BuildMcpError(id, -32602, "Unknown tool: " + toolName);

                    JsonElement? arguments = null;
                    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object)
                        arguments = argumentsElement;

                    var toolResult = await ExecuteMcpToolAsync(tool, application, arguments, data, cancellationToken);
                    return BuildMcpResult(id, toolResult);
                }

                default:
                    return BuildMcpError(id, -32601, "Method not found: " + method);
            }
        }

        private async Task<Dictionary<string, object?>> ExecuteMcpToolAsync(McpToolDescriptor tool, object application, JsonElement? arguments, HttpClientData data, CancellationToken cancellationToken)
        {
            // Tool execution failures are returned as a tool result with isError set (per the MCP spec),
            // only protocol level problems are returned as JSON-RPC errors.
            try
            {
                var paramValues = new List<object?>();
                var missingParameters = new List<string>();
                foreach (var param in tool.Parameters)
                {
                    if (param.ParameterType == typeof(HttpClientData))
                        paramValues.Add(data);
                    else if (param.ParameterType == typeof(CancellationToken))
                        paramValues.Add(cancellationToken);
                    else
                    {
                        JsonElement argumentValue = default;
                        var hasValue = false;
                        if (arguments.HasValue && param.Name != null && arguments.Value.TryGetProperty(param.Name, out argumentValue) && argumentValue.ValueKind != JsonValueKind.Null)
                            hasValue = true;

                        if (hasValue)
                        {
                            try
                            {
                                paramValues.Add(ConvertMcpArgument(argumentValue, param.ParameterType));
                            }
                            catch
                            {
                                return BuildMcpToolFailure("Invalid value for parameter '" + param.Name + "'");
                            }
                        }
                        else if (param.HasDefaultValue)
                            paramValues.Add(param.DefaultValue);
                        else if (param.ParameterType.IsValueType && Nullable.GetUnderlyingType(param.ParameterType) == null && param.Name != null)
                            missingParameters.Add(param.Name);
                        else
                            paramValues.Add(null);
                    }
                }
                if (missingParameters.Count > 0)
                    return BuildMcpToolFailure("Missing required parameter(s): " + string.Join(", ", missingParameters));

                var result = tool.Method.Invoke(application, paramValues.ToArray());
                if (result is Task task)
                {
                    await task;
                    var resultProperty = task.GetType().GetProperty("Result"); // TODO: See if we can skip this reflection step for better performance
                    result = resultProperty?.GetValue(task);
                }

                string text;
                if (result == null)
                    text = "Done";
                else if (result is string str)
                    text = str;
                else
                    text = JsonSerializer.Serialize(result, McpJsonOptions);

                return new Dictionary<string, object?>()
                {
                    ["content"] = new List<object?>()
                    {
                        new Dictionary<string, object?>() { ["type"] = "text", ["text"] = text }
                    },
                    ["isError"] = false
                };
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetException && targetException.InnerException != null)
                    e = targetException.InnerException;
                return BuildMcpToolFailure(e.Message);
            }
        }

        private static object? ConvertMcpArgument(JsonElement value, Type targetType)
        {
            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlyingType == typeof(JsonElement))
                return value;

            // Be lenient towards clients which send everything as (or as numeric) strings
            if (value.ValueKind == JsonValueKind.String && underlyingType != typeof(string))
            {
                var text = value.GetString()!;
                if (underlyingType == typeof(bool)) return bool.Parse(text);
                if (underlyingType == typeof(int)) return int.Parse(text, CultureInfo.InvariantCulture);
                if (underlyingType == typeof(long)) return long.Parse(text, CultureInfo.InvariantCulture);
                if (underlyingType == typeof(double)) return double.Parse(text, CultureInfo.InvariantCulture);
                if (underlyingType == typeof(float)) return float.Parse(text, CultureInfo.InvariantCulture);
                if (underlyingType == typeof(decimal)) return decimal.Parse(text, CultureInfo.InvariantCulture);
                if (underlyingType == typeof(Guid)) return Guid.Parse(text);
                if (underlyingType == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture);
            }
            else if (value.ValueKind != JsonValueKind.String && underlyingType == typeof(string))
                return value.GetRawText();

            return JsonSerializer.Deserialize(value.GetRawText(), targetType, McpJsonOptions);
        }

        private static Dictionary<string, object?> BuildMcpInputSchema(ParameterInfo[] methodParameters)
        {
            var properties = new Dictionary<string, object?>();
            var required = new List<string>();
            foreach (var param in methodParameters)
            {
                if (param.Name == null || param.ParameterType == typeof(HttpClientData) || param.ParameterType == typeof(CancellationToken))
                    continue; // Injected parameters are not part of the tool schema

                var propertySchema = GetMcpTypeSchema(param.ParameterType);
                var description = param.GetCustomAttribute<McpParamAttribute>()?.Description;
                if (!string.IsNullOrEmpty(description))
                    propertySchema["description"] = description;

                properties.Add(param.Name, propertySchema);
                if (!param.HasDefaultValue)
                    required.Add(param.Name);
            }

            var schema = new Dictionary<string, object?>()
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0)
                schema["required"] = required;
            return schema;
        }

        private static Dictionary<string, object?> GetMcpTypeSchema(Type type)
        {
            var schema = new Dictionary<string, object?>();
            var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

            if (underlyingType == typeof(string) || underlyingType == typeof(char) || underlyingType == typeof(Guid) || underlyingType == typeof(Uri) || underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset) || underlyingType == typeof(TimeSpan))
                schema["type"] = "string";
            else if (underlyingType == typeof(bool))
                schema["type"] = "boolean";
            else if (underlyingType == typeof(byte) || underlyingType == typeof(sbyte) || underlyingType == typeof(short) || underlyingType == typeof(ushort) || underlyingType == typeof(int) || underlyingType == typeof(uint) || underlyingType == typeof(long) || underlyingType == typeof(ulong))
                schema["type"] = "integer";
            else if (underlyingType == typeof(float) || underlyingType == typeof(double) || underlyingType == typeof(decimal))
                schema["type"] = "number";
            else if (underlyingType.IsEnum)
            {
                schema["type"] = "string";
                schema["enum"] = Enum.GetNames(underlyingType).ToList();
            }
            else if (underlyingType == typeof(JsonElement) || underlyingType == typeof(object))
            {
                // Free form
            }
            else if (underlyingType.IsArray)
            {
                schema["type"] = "array";
                schema["items"] = GetMcpTypeSchema(underlyingType.GetElementType()!);
            }
            else if (underlyingType.IsGenericType && underlyingType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                schema["type"] = "object";
                schema["additionalProperties"] = GetMcpTypeSchema(underlyingType.GetGenericArguments()[1]);
            }
            else if (underlyingType.GetInterfaces().Any(a => a.IsGenericType && a.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            {
                schema["type"] = "array";
                var enumerableInterface = underlyingType.GetInterfaces().First(a => a.IsGenericType && a.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                schema["items"] = GetMcpTypeSchema(enumerableInterface.GetGenericArguments()[0]);
            }
            else
                schema["type"] = "object"; // Complex type, deserialized from a JSON object

            return schema;
        }

        private static Dictionary<string, object?> BuildMcpToolFailure(string message)
        {
            return new Dictionary<string, object?>()
            {
                ["content"] = new List<object?>()
                {
                    new Dictionary<string, object?>() { ["type"] = "text", ["text"] = message }
                },
                ["isError"] = true
            };
        }

        private static Dictionary<string, object?> BuildMcpResult(JsonElement? id, object? result)
        {
            return new Dictionary<string, object?>()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
        }

        private static Dictionary<string, object?> BuildMcpError(JsonElement? id, int code, string message)
        {
            return new Dictionary<string, object?>()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?>()
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        private static string SanitizeMcpToolName(string name)
        {
            // Tool names should only contain a-z, A-Z, 0-9, '_', '-' and '.' and be at most 64 characters long
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            var result = sb.ToString();
            if (result.Length > 64)
                result = result.Substring(0, 64);
            return result;
        }

        private class McpToolDescriptor
        {
            public string Name { get; set; } = "";
            public string? Title { get; set; }
            public string? Description { get; set; }
            public MethodInfo Method { get; set; } = null!;
            public ParameterInfo[] Parameters { get; set; } = Array.Empty<ParameterInfo>();
            public Dictionary<string, object?> InputSchema { get; set; } = new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Optionally applied to a method of an object registered with HttpHandler.AddMcpRoute to customize how the
    /// method is exposed as an MCP tool. Without this attribute the method is still exposed, using its method name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public class McpToolAttribute : Attribute
    {
        /// <summary>
        /// Override the tool name shown to the MCP client. Must be unique within the application. Defaults to the method name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Optional human readable title for the tool.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>
        /// Description of the tool as shown to the MCP client (LLM). Explain here what the tool does and when to use it.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Set to false to prevent a public method from being exposed as a tool.
        /// </summary>
        public bool Enabled { get; set; } = true;

        public McpToolAttribute()
        {
        }

        public McpToolAttribute(string? description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// Optionally applied to a parameter of a method exposed as an MCP tool to provide a description for it.
    /// Parameters without a default value are marked as required in the tool schema.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
    public class McpParamAttribute : Attribute
    {
        /// <summary>
        /// Description of the parameter as shown to the MCP client (LLM).
        /// </summary>
        public string? Description { get; set; }

        public McpParamAttribute()
        {
        }

        public McpParamAttribute(string? description)
        {
            Description = description;
        }
    }
}
