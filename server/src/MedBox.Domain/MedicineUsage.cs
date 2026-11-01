namespace MedBox.Domain;

/// <summary>
/// 药名归一与「常用药」榜单口径（P22 chips + 药名联想共用）。
///
/// 榜单是**全平台**的：同一个药名被所有家庭添加过多少次（`medicine_usage_stats`，V008）。
/// 计数必须落库：跨家庭聚合拿不到别人的数据，也不能每开一次表单就全表扫一遍。
///
/// 口径（主理人定）：**每次添加 +1，每次删除（归档）−1**，下限 0。
/// 净次数而不是累计次数 —— 用户删掉一个药就是不用了，再拿它去推荐别人没有道理。
/// </summary>
public static class MedicineUsage
{
    /// <summary>常用药 chips 条数：P22 一行 3 个、最多两行。</summary>
    public const int CommonTop = 5;

    /// <summary>输入联想最多给几条 —— 再多老人翻不动，还挡住键盘。</summary>
    public const int SuggestLimit = 8;

    /// <summary>
    /// **自定义药名**上榜门槛：全平台净次数至少到这个数才进 chips（平台预设名不受限制，输入联想也不受限）。
    ///
    /// 不是性能考虑，是**隐私与噪音**：没有门槛时，低流量期第一个用户手输的任意一种药
    /// （可能是很私密的药）会立刻出现在所有家庭的"常用药"里，还能被反复轮询看出"刚刚有人加了这个药"。
    /// 3 次意味着不是一个人偶然加的那一次。
    ///
    /// 预设名为什么豁免：`medicines_common` 本来就是公开只读数据（`/api/medicine/common` 匿名可取），
    /// 它的药名出现不泄露任何新信息，而且要让"大家真在用的预设药"能立刻顶到前面去 ——
    /// 否则上线头几个月 chips 永远是预设原序，看起来像功能没生效。
    /// </summary>
    public const int MinCountToRank = 3;

    /// <summary>
    /// 归一键：去首尾空白（含全角空格）+ 小写。中文没有大小写，小写只为兼容手输的英文/数字
    /// （"VitD" 与 "vitd" 不该各占一行）。
    /// 必须与 V008 里 `lower(btrim(name, E' \t\r\n' || chr(12288)))` 保持一致，
    /// 否则回填出来的历史行与新建行会各算一行。
    /// </summary>
    public static string Normalize(string? name)
    {
        var s = name ?? string.Empty;
        var start = 0;
        var end = s.Length;
        while (start < end && char.IsWhiteSpace(s[start])) start++;
        while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
        return s[start..end].ToLowerInvariant();
    }

    /// <summary>药名是否命中关键词。<paramref name="normalizedKeyword"/> 必须是 <see cref="Normalize"/> 过的。</summary>
    public static bool Matches(string? name, string normalizedKeyword)
        => normalizedKeyword.Length == 0 || Normalize(name).Contains(normalizedKeyword, StringComparison.Ordinal);

    /// <summary>
    /// 按组顺序合并候选并去重（本家庭历史 → 全平台榜 → 平台预设），最多 <paramref name="limit"/> 条。
    ///
    /// 用"先到先得"而不是重新排序：三组的价值本来就是分层的 ——
    /// 自己家的药必须压过全平台热门（否则"我妈在吃的那一个"会被更热门的药挤出列表），
    /// 预设只在前面都不够时补位。重排会把这三层压平。
    /// </summary>
    public static List<T> Merge<T>(IEnumerable<IEnumerable<T>> groups, Func<T, string> nameOf, int limit)
    {
        var result = new List<T>();
        if (limit <= 0) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                if (result.Count >= limit) return result;
                var key = Normalize(nameOf(item));
                if (key.Length == 0) continue; // 空药名不占位，否则它会吃掉一个名额
                if (!seen.Add(key)) continue;
                result.Add(item);
            }
        }
        return result;
    }
}
