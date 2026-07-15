namespace ComplexTweaks.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class FrameworkUpdateAttribute(string? configFieldName = null) : Attribute {
    public string? ConfigFieldName { get; } = configFieldName;
}

