using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Comgenie.AI.Entities
{
    public class ToolCallInfo
    {
        public string Type { get; set; } = "function";

        public ToolCallFunctionInfo? Function { get; set; }

        [JsonIgnore]
        internal MethodInfo? MethodInfo { get; set; }
        [JsonIgnore]
        internal object? MethodInstance { get; set; }
    }
    public class ToolCallFunctionInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ToolCallParameterInfo Parameters { get; set; }
    }
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(ToolCallObjectParameterInfo), typeDiscriminator: "object")]
    [JsonDerivedType(typeof(ToolCallStringFieldParameterInfo), typeDiscriminator: "string")]
    [JsonDerivedType(typeof(ToolCallIntegerFieldParameterInfo), typeDiscriminator: "integer")]
    [JsonDerivedType(typeof(ToolCallBooleanFieldParameterInfo), typeDiscriminator: "boolean")]
    [JsonDerivedType(typeof(ToolCallEnumFieldParameterInfo), typeDiscriminator: "enum")]
    public abstract class ToolCallParameterInfo
    {
        [JsonIgnore] // Hack to make it work with the TypeDiscriminatorProperty name, otherwise it will be duplicated for some reason
        public string type { get; set; } = "string"; // string, integer, object
    }
    public class ToolCallObjectParameterInfo : ToolCallParameterInfo
    {
        public ToolCallObjectParameterInfo()
        {
            type = "object";
        }
        public Dictionary<string, ToolCallParameterInfo> Properties { get; set; } = new();
        public List<string> Required { get; set; } = new();
    }
    public abstract class ToolCallFieldParameterInfo : ToolCallParameterInfo
    {
        public string? Description { get; set; }
    }
    public class ToolCallStringFieldParameterInfo : ToolCallFieldParameterInfo
    {
    }

    public class ToolCallIntegerFieldParameterInfo : ToolCallFieldParameterInfo
    {
    }
    public class ToolCallBooleanFieldParameterInfo : ToolCallFieldParameterInfo
    {
    }
    public class ToolCallEnumFieldParameterInfo : ToolCallFieldParameterInfo
    {
        public string[] Enum { get; set; }
    }
}
