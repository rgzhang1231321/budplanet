using FluentAssertions;
using MedBox.Application.Services;

namespace MedBox.Domain.Tests;

/// <summary>
/// plans.slots 解析的失败必须可被调用方看见（带 plan_id 记日志），不能像原来那样静默返回空表 ——
/// 空表会让当天药单变空、剂量回落成药品默认值，而日志里一条线索都没有。
/// </summary>
public class DoseSlotParseTests
{
    [Fact]
    public void broken_json_is_reported_as_an_error_instead_of_silently_empty()
    {
        var ok = DoseMaterializer.TryParseSlots("{not json", out var slots, out var error);

        ok.Should().BeFalse();
        slots.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void valid_but_wrong_shaped_json_is_also_reported()
    {
        // "{}" 在 jsonb 里是合法 JSON 对象，但 EnumerateArray 会抛 InvalidOperationException ——
        // 原来那种只 catch JsonException 的写法会让它冒到中间件变成 1009
        var ok = DoseMaterializer.TryParseSlots("{}", out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void empty_or_missing_slots_is_legal_and_not_an_error()
    {
        DoseMaterializer.TryParseSlots(null, out var a, out var errA).Should().BeTrue();
        a.Should().BeEmpty();
        errA.Should().BeNull();

        DoseMaterializer.TryParseSlots("[]", out var b, out var errB).Should().BeTrue();
        b.Should().BeEmpty();
        errB.Should().BeNull();
    }

    [Fact]
    public void well_formed_slots_are_parsed_in_order()
    {
        var ok = DoseMaterializer.TryParseSlots("[{\"t\":\"08:00\",\"d\":\"半片\"},{\"t\":\"20:00\"}]", out var slots, out _);

        ok.Should().BeTrue();
        slots.Select(s => s.Minute).Should().ContainInOrder(480, 1200);
        slots[0].D.Should().Be("半片");
    }
}
