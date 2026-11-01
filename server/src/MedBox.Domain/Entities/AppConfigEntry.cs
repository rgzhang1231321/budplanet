namespace MedBox.Domain.Entities;

/// <summary>app_config：广告开关、限额、模板 ID 等远端下发参数（不发版即生效）。</summary>
public class AppConfigEntry
{
    public string ConfigKey { get; set; } = "";
    /// <summary>JSONB 标量或对象，读出后按 camelCase 组装成 config 块。</summary>
    public string ConfigValue { get; set; } = "null";
    public long UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
