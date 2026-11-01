using System.Text.Json;
using FluentAssertions;
using MedBox.Application.Services;

namespace MedBox.Domain.Tests;

/// <summary>
/// 模板字段映射的护栏。这里的键名写错不会编译失败、也不会在本地报错，
/// 只会在真机发送时整批 47003（模板参数不合法）——所以必须锁死。
/// 字段序号以微信后台「我的模板」实际分配的为准，换模板时同步改本文件与 PushTaskBuilder。
/// </summary>
public class TemplateDataTests
{
    private static Dictionary<string, string> Map(string tmplKey, string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return TemplateData.For(tmplKey, doc.RootElement);
    }

    [Fact]
    public void checkin_maps_to_short_thing7_and_time6()
    {
        var d = Map("CHECKIN", """{"alias":"爸爸","due_time":"08:00","dose_text":"1片"}""");
        d.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["short_thing7"] = "1片",
            ["time6"] = "08:00",
        });
    }

    [Fact]
    public void checkin_truncates_dose_to_five_chars()
    {
        // short_thing 限 5 字符，超了微信直接拒发
        var d = Map("CHECKIN", """{"alias":"爸爸","due_time":"08:00","dose_text":"一二三四五六"}""");
        d["short_thing7"].Should().Be("一二三四五");
    }

    [Fact]
    public void missed_maps_to_thing18_time3_thing14()
    {
        var d = Map("MISSED", """{"alias":"爸爸","due_time":"12:30"}""");
        d.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["thing18"] = "爸爸",
            ["time3"] = "12:30",
            ["thing14"] = "还没记录用药", // 固定串：推送时可能只是还没打卡，"漏服"是判定结论
        });
    }

    [Fact]
    public void missed_truncates_alias_to_ten_chars()
    {
        var d = Map("MISSED", """{"alias":"孩子他姥爷的二舅家人啊","due_time":"12:30"}""");
        d["thing18"].Should().Be("孩子他姥爷的二舅家人");
    }

    [Fact]
    public void weekly_maps_to_time5_and_thing6()
    {
        var d = Map("WEEKLY", """{"stat_time":"10.19-10.25","summary":"完成率75% 漏服1次"}""");
        d.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["time5"] = "10.19-10.25",
            ["thing6"] = "完成率75% 漏服1次",
        });
    }

    [Fact]
    public void weekly_truncates_summary_to_twenty_chars()
    {
        var d = Map("WEEKLY", """{"stat_time":"10.19-10.25","summary":"完成率100% 漏服0次 但备注故意写得非常长"}""");
        d["thing6"].Should().HaveLength(20);
    }

    [Fact]
    public void unknown_key_yields_empty_payload()
        => Map("NOPE", "{}").Should().BeEmpty();

    [Fact]
    public void missing_payload_fields_degrade_to_empty_string()
    {
        var d = Map("WEEKLY", "{}");
        d["time5"].Should().Be("");
        d["thing6"].Should().Be("");
    }
}
