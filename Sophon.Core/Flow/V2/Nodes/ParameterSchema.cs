#nullable enable
namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 节点参数元数据声明，用于驱动属性面板（Wave3-E Nodify 编辑器）渲染与校验。
    /// </summary>
    public class ParameterSchema
    {
        /// <summary>参数键名（程序内通过此键访问）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>UI 显示名称（属性面板标签）。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 数据类型标识：
        /// "string", "int", "double", "bool", "axis", "point", "json", "enum" 等。
        /// </summary>
        public string Type { get; set; } = "string";

        /// <summary>默认值。</summary>
        public object? DefaultValue { get; set; }

        /// <summary>参数描述与使用提示。</summary>
        public string? Description { get; set; }

        /// <summary>是否为必填项。</summary>
        public bool IsRequired { get; set; }

        /// <summary>若是枚举类型或下拉选择，可选值列表。</summary>
        public string[]? Options { get; set; }

        public ParameterSchema() { }

        public ParameterSchema(string name, string displayName, string type = "string", object? defaultValue = null, string? description = null, bool isRequired = false, string[]? options = null)
        {
            Name = name;
            DisplayName = displayName;
            Type = type;
            DefaultValue = defaultValue;
            Description = description;
            IsRequired = isRequired;
            Options = options;
        }
    }
}
