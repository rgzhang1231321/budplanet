using MedBox.Domain;
using Xunit;

namespace MedBox.Domain.Tests;

/// <summary>
/// 隐私闸门（契约 §二.10：埋点参数不得含药品名 / 疾病名 / 剂量文本）。
/// 这些用例覆盖的是"真实会发生的泄漏"：药名带中文、剂量带单位、key 本身就叫 medicine_name。
/// </summary>
public class PrivacyFilterTests
{
    [Theory]
    [InlineData("checkin_success")]
    [InlineData("ad_show")]
    [InlineData("error_js")]
    [InlineData("evt_01")]
    public void 合法的埋点事件名通过(string name)
        => Assert.True(PrivacyFilter.IsSafeMetricKey(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("CheckinSuccess")]      // 大写：会污染 metric_key 的检索
    [InlineData("checkin.success")]     // 点号：列宽 32 且要能当 key 用
    [InlineData("checkin-success")]
    [InlineData("打卡成功")]             // 中文事件名本身就是内容泄漏
    public void 非法的埋点事件名被拒(string? name)
        => Assert.False(PrivacyFilter.IsSafeMetricKey(name));

    [Theory]
    [InlineData("from", "invite")]
    [InlineData("channel", "wechat")]
    [InlineData("n", "3")]
    [InlineData("ok", "1")]
    public void 白名单内的枚举参数保留(string key, string value)
        => Assert.Equal(value, PrivacyFilter.Sanitize([new(key, value)])[key]);

    [Theory]
    [InlineData("medicine_name", "阿司匹林")]   // key 不在白名单
    [InlineData("dose_text", "1片")]           // key 不在白名单
    [InlineData("fid", "F1234")]               // 家庭标识：不进聚合表
    [InlineData("msg", "error")]               // 报错文本可能整句带药名回来
    public void 白名单外的key一律丢弃(string key, string value)
        => Assert.Empty(PrivacyFilter.Sanitize([new(key, (string?)value)]));

    [Theory]
    [InlineData("盐酸二甲双胍片")]
    [InlineData("1片")]
    [InlineData("半片")]
    [InlineData("高血压")]
    [InlineData("每天早饭后")]
    public void 值里带中文即判为健康信息并丢弃(string value)
    {
        // key 是白名单内的（from），但值本身是药名/剂量 —— 第二道闸必须挡住
        Assert.False(PrivacyFilter.IsSafeValue(value));
        Assert.Empty(PrivacyFilter.Sanitize([new("from", value)]));
    }

    [Fact]
    public void 纯ASCII的剂量串值检查放行_但不能靠它挡_所以要靠key白名单()
    {
        // 诚实记录这条边界：0.5mg / 1 tablet 是合法 ASCII，IsSafeValue 放行。
        // 真正挡住它的是 key —— dose / dose_text / medicine_name 都不在白名单里。
        Assert.True(PrivacyFilter.IsSafeValue("0.5mg"));
        Assert.Empty(PrivacyFilter.Sanitize([new("dose_text", "0.5mg")]));
    }

    [Fact]
    public void 超长值被丢弃_不截断_因为截断后仍是半句内容()
    {
        Assert.False(PrivacyFilter.IsSafeValue(new string('a', PrivacyFilter.MaxValueLen + 1)));
        Assert.True(PrivacyFilter.IsSafeValue(new string('a', PrivacyFilter.MaxValueLen)));
    }

    [Fact]
    public void 单个事件最多保留_8_个参数()
    {
        var keys = new[] { "from", "to", "page", "action", "scene", "channel", "trigger", "src", "ver", "role" };
        var raw = keys.Select(k => new KeyValuePair<string, string?>(k, "x")).ToList();

        Assert.Equal(PrivacyFilter.MaxParams, PrivacyFilter.Sanitize(raw).Count);
    }

    [Fact]
    public void 非法值被丢弃_合法值保留_同批混合时不互相影响()
    {
        var raw = new List<KeyValuePair<string, string?>>
        {
            new("from", "invite"),
            new("tmpl", "CHECKIN"),
            new("reason", "忘记提前吃了"),   // 中文 → 丢
            new("code", "43101"),
        };

        var kept = PrivacyFilter.Sanitize(raw);

        Assert.Equal(3, kept.Count);
        Assert.Equal("invite", kept["from"]);
        Assert.Equal("CHECKIN", kept["tmpl"]);
        Assert.Equal("43101", kept["code"]);
        Assert.False(kept.ContainsKey("reason"));
    }
}
