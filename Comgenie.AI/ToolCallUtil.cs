using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Comgenie.AI.Entities;

namespace Comgenie.AI
{
    // Note: This class is mostly AI generated code.
    public static class ToolCallUtil
    {
        /// <summary>
        /// Discover methods annotated with ToolCallAttributes in the given assembly and return a list of ToolCallInfo.
        /// </summary>
        public static List<ToolCallInfo> DiscoverToolCalls(Assembly assembly)
        {
            var result = new List<ToolCallInfo>();

            var allTypes = assembly.GetTypes();
            foreach (var type in allTypes)
            {
                // Consider public and non-public, instance and static methods
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var methodAttr = method.GetCustomAttribute<ToolCallAttribute>(inherit: true);
                    if (methodAttr == null)
                        continue;

                    var toolCall = BuildToolCallInfoFromMethod(method, null);
                    result.Add(toolCall);
                }
            }

            return result;
        }


        /// <summary>
        /// Generate indented JSON for discovered ToolCallInfo objects.
        /// </summary>
        public static string GenerateToolCallsJson(Assembly assembly)
        {
            var infos = DiscoverToolCalls(assembly);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            return JsonSerializer.Serialize(infos, options);
        }

        public static ToolCallInfo BuildToolCallInfoFromMethod(MethodInfo method, object? methodInstance, Delegate? methodDelegate=null)
        {
            var methodAttr = method.GetCustomAttribute<ToolCallAttribute>();

            var toolCall = new ToolCallInfo
            {
                Type = "function",
                Function = new ToolCallFunctionInfo
                {
                    Name = method.Name,
                    Description = methodAttr?.Description ?? string.Empty,
                    Parameters = new ToolCallObjectParameterInfo()
                },
                MethodInfo = method,
                MethodInstance = methodInstance,
                MethodDelegate = methodDelegate
            };
            
            
            var root = (ToolCallObjectParameterInfo)toolCall.Function.Parameters;

            foreach (var param in method.GetParameters())
            {
                var paramAttr = param.GetCustomAttribute<ToolCallAttribute>(inherit: true);
                var mapped = MapTypeToParameterInfo(param.ParameterType, paramAttr);

                // If mapped is null fallback to string field
                if (mapped == null)
                {
                    mapped = new ToolCallStringFieldParameterInfo { type = "string", Description = paramAttr?.Description ?? string.Empty };
                }
                mapped.name = param.Name;

                root.Properties[param.Name] = mapped;

                // Consider parameter required if it has no default value
                // (simple heuristic: parameters with default values are optional)
                if (paramAttr != null)
                {
                    if (paramAttr.Required)
                    {
                        root.Required.Add(param.Name);
                        continue;
                    }
                }
                else if (!param.HasDefaultValue)
                {
                    root.Required.Add(param.Name);
                }
            }

            return toolCall;
        }

        private static ToolCallParameterInfo? MapTypeToParameterInfo(Type type, ToolCallAttribute? attr)
        {
            // Unwrap Nullable<T>
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                type = underlying;

            // Strings
            if (type == typeof(string) || type == typeof(System.Text.StringBuilder))
            {
                return new ToolCallStringFieldParameterInfo
                {
                    type = "string",
                    Description = attr?.Description
                };
            }

            // Integers
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort))
            {
                return new ToolCallIntegerFieldParameterInfo
                {
                    type = "integer",
                    Description = attr?.Description
                };
            }

            // Floating point -> represent as string (no float discriminator)
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                return new ToolCallStringFieldParameterInfo
                {
                    type = "string",
                    Description = attr?.Description
                };
            }

            // Boolean -> represent as string with enum ["true","false"]
            if (type == typeof(bool))
            {
                return new ToolCallBooleanFieldParameterInfo
                {
                    type = "boolean",
                    Description = attr?.Description
                };
            }

            // Enum -> map to string with enum values
            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                return new ToolCallEnumFieldParameterInfo
                {
                    type = "enum",
                    Description = attr?.Description,
                    Enum = names
                };
            }

            // Arrays and generic enumerable types
            if (type.IsArray)
            {
                var elemType = type.GetElementType() ?? typeof(object);
                var itemsInfo = MapTypeToParameterInfo(elemType, null);

                // Represent arrays as an object with "items" property that describes the item type
                var arrObj = new ToolCallObjectParameterInfo();
                arrObj.Properties["items"] = itemsInfo ?? new ToolCallStringFieldParameterInfo { type = "string" };
                return arrObj;
            }
            if (ImplementsGenericInterface(type, typeof(IEnumerable<>)))
            {
                var elemType = GetEnumerableElementType(type) ?? typeof(object);
                var itemsInfo = MapTypeToParameterInfo(elemType, null);
                var arrObj = new ToolCallObjectParameterInfo();
                arrObj.Properties["items"] = itemsInfo ?? new ToolCallStringFieldParameterInfo { type = "string" };
                return arrObj;
            }

            // Complex types (classes/structs)
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                var obj = new ToolCallObjectParameterInfo();

                // reflect public readable properties
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var p in props)
                {
                    // skip indexers
                    if (p.GetIndexParameters().Length > 0)
                        continue;

                    var propAttr = p.GetCustomAttribute<ToolCallAttribute>(inherit: true);
                    var mapped = MapTypeToParameterInfo(p.PropertyType, propAttr);
                    if (mapped == null)
                        mapped = new ToolCallStringFieldParameterInfo { type = "string", Description = propAttr?.Description };
                    mapped.name = p.Name;
                    obj.Properties[p.Name] = mapped;

                    // Simple required heuristic: non-nullable value types are required
                    if (propAttr != null)
                    {
                        if (propAttr.Required)
                            obj.Required.Add(p.Name);
                    }
                    else if (p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) == null)
                    {
                        obj.Required.Add(p.Name);
                    }
                }

                return obj;
            }

            // Fallback
            return new ToolCallStringFieldParameterInfo
            {
                type = "string",
                Description = attr?.Description
            };
        }

        private static bool ImplementsGenericInterface(Type type, Type genericInterface)
        {
            return type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericInterface);
        }

        private static Type? GetEnumerableElementType(Type type)
        {
            if (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(IEnumerable<>).MakeGenericType(type.GetGenericArguments()[0]).IsAssignableFrom(type))
                return type.GetGenericArguments()[0];

            var iface = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return iface?.GetGenericArguments()[0];
        }

        public static string ExecuteFunction(ToolCallInfo tool, string argumentsJson)
        {
            var method = tool.MethodInfo;
            if (method == null)
                throw new ArgumentNullException(nameof(tool), "MethodInfo is null for the provided tool.");

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            // Parse arguments JSON into a document; treat empty input as empty object
            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Use empty object to rely on defaults
                root = JsonDocument.Parse("{}").RootElement;
            }

            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];

                // If JSON contains the named property, deserialize it
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(p.Name, out var propElement))
                {
                    try
                    {
                        var paramType = p.ParameterType;

                        // If the parameter is 'params' array, the parameter type will be an array; just deserialize into the declared type.
                        args[i] = JsonSerializer.Deserialize(propElement.GetRawText(), paramType, options);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Failed to deserialize argument '{p.Name}' to type '{p.ParameterType}'.", ex);
                    }
                }
                else
                {
                    // Not present in JSON -> use default value or CLR default
                    if (p.HasDefaultValue)
                    {
                        var dv = p.DefaultValue;
                        args[i] = dv == System.DBNull.Value ? null : dv;
                    }
                    else
                    {
                        args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                    }
                }
            }

            // If instance method, create instance
            object? instance = tool.MethodInstance;
            if (instance == null && !method.IsStatic)
            {
                var declType = method.DeclaringType ?? throw new InvalidOperationException("Method has no declaring type.");
                try
                {
                    instance = Activator.CreateInstance(declType, nonPublic: true);
                }
                catch
                {
                    instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(declType);
                }
            }

            var invocationResult = method.Invoke(instance, args);

            // Void return -> empty string
            if (method.ReturnType == typeof(void))
            {
                return string.Empty;
            }

            // Async handling: Task or Task<T>
            // TODO: If a 'cancellationToken'  parameter is set, we want to pass ours through
            if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(method.ReturnType))
            {
                if (invocationResult == null)
                    return string.Empty;

                var task = (System.Threading.Tasks.Task)invocationResult;
                // Wait synchronously
                task.GetAwaiter().GetResult();

                // If Task<T>, extract Result property
                if (method.ReturnType.IsGenericType)
                {
                    var resultProperty = invocationResult.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                    var resultValue = resultProperty?.GetValue(invocationResult);
                    return JsonSerializer.Serialize(resultValue, options);
                }

                // Non-generic Task -> nothing to serialize
                return string.Empty;
            }

            // Synchronous non-void return -> serialize and return
            return JsonSerializer.Serialize(invocationResult, options);
        }
    }
}