namespace MedBox.Domain;

/// <summary>
/// 埋点参数隐私闸门（契约 §二.10：参数"不得含药品名 / 疾病名 / 剂量文本"）。
/// 前端不许发，服务端也不许存 —— 这里做第二道拦，因为"不信任前端"是这个产品的合规前提。
///
/// 策略：只放行**行为枚举**（短、纯 ASCII、key 在白名单内），其余字段**静默丢弃**。
/// 不整批打回：埋点因为一个脏字段报错，前端只会无限重投，脏数据照样进不来、指标还全丢了。
/// </summary>
public static class PrivacyFilter
{
    /// <summary>单批上限：前端 10 条 / 5 秒攒批，50 条是容忍突发后的硬顶（超过只截断不报错）。</summary>
    public const int MaxEvents = 50;

    /// <summary>单个事件最多保留几个参数。</summary>
    public const int MaxParams = 8;

    /// <summary>参数值上限。药名普遍 4~20 字、剂量串 2~8 字，32 足够承载枚举值又装不下句子。</summary>
    public const int MaxValueLen = 32;

    /// <summary>
    /// 参数 key 白名单。共同点：表达"发生了什么、在哪个页面、结果如何"，不表达"什么药、什么病、多少量"。
    /// 不在表里的一律丢弃 —— 默认拒绝，新增埋点字段必须来这里登记。
    /// </summary>
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        // 位置与来源（枚举）
        "from", "to", "page", "action", "scene", "channel", "trigger", "src", "ver",
        // 状态与结果（枚举）
        "role", "mode", "result", "ok", "status", "from_status", "to_status", "reason", "err",
        // 业务计数与耗时（数值）
        "n", "count", "step", "index", "code", "rate", "limit", "batch", "batch_size",
        "patient_n", "med_count", "duration", "delay_ms", "ms", "quota_before", "cold",
        "accepted", "snooze_count", "slot", "kind", "tmpl", "product", "unit",
        // 注意：fid / openid / msg 一律不收 —— fid 是家庭标识，msg 可能整句带着药名回来
    };

    /// <summary>事件名：小写字母 / 数字 / 下划线，最长 32（它直接当 metric_key 用）。</summary>
    public static bool IsSafeMetricKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxValueLen) return false;
        foreach (var c in key)
        {
            var ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (!ok) return false;
        }
        return true;
    }

    public static bool IsSafeKey(string? key) => !string.IsNullOrEmpty(key) && AllowedKeys.Contains(key);

    /// <summary>
    /// 值必须是纯 ASCII 可打印串。这一条挡住的是绝大多数真实泄漏场景：
    /// 药名（"盐酸二甲双胍片"）、剂量（"1片"、"半片"）、疾病（"高血压"）都含非 ASCII 字符。
    /// </summary>
    public static bool IsSafeValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLen) return false;
        foreach (var c in value)
            if (c < 0x20 || c > 0x7E) return false;
        return true;
    }

    /// <summary>过滤一批参数：只留 key 与 value 都安全的项。</summary>
    public static Dictionary<string, string> Sanitize(IEnumerable<KeyValuePair<string, string?>> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in raw)
        {
            if (result.Count >= MaxParams) break;
            if (!IsSafeKey(k) || !IsSafeValue(v)) continue;
            result[k] = v!;
        }
        return result;
    }
}
