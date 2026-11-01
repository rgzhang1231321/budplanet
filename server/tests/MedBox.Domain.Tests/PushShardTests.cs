using FluentAssertions;
using MedBox.Domain;

namespace MedBox.Domain.Tests;

/// <summary>
/// push_tasks.shard 的分配规则。断的是"同一个人永远落在同一个分片"：
/// string.GetHashCode() 带进程级随机种子，重启后同一 openid 会换分片，
/// 分片消费一旦按 shard 过滤，换分片就等于任务的归属变了。所以哈希必须是纯函数。
/// </summary>
public class PushShardTests
{
    private static readonly string[] Sample =
        Enumerable.Range(0, 400).Select(i => $"oX7h{ i:D4}qLmZ").ToArray();

    [Fact]
    public void same_openid_always_maps_to_the_same_shard()
    {
        foreach (var openid in Sample)
            PushPolicy.Shard(openid).Should().Be(PushPolicy.Shard(openid));
    }

    [Fact]
    public void shard_always_lands_in_range_even_for_extreme_looking_input()
    {
        foreach (var openid in Sample.Append("").Append("\u0000\uffff").Append(new string('x', 512)))
        {
            var shard = PushPolicy.Shard(openid);
            shard.Should().BeGreaterThanOrEqualTo((short)0);
            shard.Should().BeLessThan((short)PushPolicy.ShardCount);
        }
    }

    [Fact]
    public void all_shards_are_used_and_none_is_starved()
    {
        var buckets = Sample.GroupBy(PushPolicy.Shard).ToDictionary(g => g.Key, g => g.Count());

        buckets.Should().HaveCount(PushPolicy.ShardCount);
        // 随机分布的粗检：每个分片至少拿到 1/10 的样本（400 个样本下几乎不可能破）
        buckets.Values.Should().OnlyContain(n => n > Sample.Length / 10);
    }

    [Fact]
    public void shard_matches_the_fnv1a_reference_so_a_restart_cannot_move_it()
    {
        // 在测试里独立实现一遍 FNV-1a：只要两边的算法一致，进程重启/换机器算出来都是同一个分片。
        foreach (var openid in Sample)
            PushPolicy.Shard(openid).Should().Be((short)(ReferenceFnv1a32(openid) % PushPolicy.ShardCount));
    }

    private static uint ReferenceFnv1a32(string s)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }
        return hash;
    }
}
