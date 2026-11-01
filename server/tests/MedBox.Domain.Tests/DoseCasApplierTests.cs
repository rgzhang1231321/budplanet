using FluentAssertions;
using MedBox.Application.Abstractions;
using MedBox.Application.Services;
using MedBox.Domain;
using NSubstitute;

namespace MedBox.Domain.Tests;

/// <summary>
/// 打卡写入的 CAS 编排规则。核心断言是"并发两个 submit 只有一个真的写进去，另一个报幂等空操作"，
/// 以及"打不进去时绝不返回 changed=true"（那就是假成功：用户以为记上了，库里还是 missed）。
/// </summary>
public class DoseCasApplierTests
{
    private const string DoseId = "p1_2026-11-01_480";
    private const string FamilyId = "f1";
    private const string DueDate = "2026-11-01";
    private const int Slot = 480;
    private const long DueTs = 1_761_955_200_000L;

    [Fact]
    public async Task concurrent_submits_only_one_actually_writes()
    {
        // 两个请求都在任何人写入之前读到了 pending（StaleReads=2 模拟这个时刻）
        var store = new FakeDoseStore(DoseStatusWire.Pending) { StaleReads = 2 };
        var cas = BuildApplier(store);

        var first = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);
        var second = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);

        first.Changed.Should().BeTrue();            // 抢到的那个真的写了
        second.Changed.Should().BeFalse();          // 没抢到的按真实状态报幂等空操作
        second.IsRejected.Should().BeFalse();       // 但不许报错（用户点的是对的事）
        second.Status.Should().Be(DoseStatusWire.Taken);
        store.Status.Should().Be(DoseStatusWire.Taken);
        store.CasCalls.Should().BeGreaterThan(1);   // 后者确实试过写，只是没写进去
    }

    [Fact]
    public async Task lost_race_to_missed_judgement_still_lands_as_taken()
    {
        // 漏服判定抢先一步：第一次 CAS 必然 0 行；重读后看到 missed(未改判) 再打一次
        var store = new FakeDoseStore(DoseStatusWire.Pending) { StaleReads = 1, StealOnFirstCas = DoseStatusWire.Missed };
        var cas = BuildApplier(store);

        var outcome = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);

        outcome.Changed.Should().BeTrue();
        outcome.Status.Should().Be(DoseStatusWire.Taken);
        store.Status.Should().Be(DoseStatusWire.Taken); // 用户点的「已服用」没被漏服判定覆盖
    }

    [Fact]
    public async Task a_row_that_keeps_changing_ends_as_rejected_not_success()
    {
        var store = new FakeDoseStore(DoseStatusWire.Pending) { AlwaysLoseCas = true };
        var cas = BuildApplier(store);

        var outcome = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);

        outcome.IsRejected.Should().BeTrue();     // 打不进去就说打不进去
        outcome.Changed.Should().BeFalse();
        store.CasCalls.Should().Be(DoseCasPolicy.MaxAttempts); // 重试有上界，不会把库打爆
    }

    [Fact]
    public async Task skipped_is_rejected_without_touching_the_row()
    {
        var store = new FakeDoseStore(DoseStatusWire.Skipped);
        var cas = BuildApplier(store);

        var outcome = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);

        outcome.IsRejected.Should().BeTrue();
        outcome.RejectCode.Should().Be(1005);
        store.CasCalls.Should().Be(0); // 规则层就不允许，连 UPDATE 都不发
        store.Status.Should().Be(DoseStatusWire.Skipped);
    }

    [Fact]
    public async Task missing_row_is_rejected_as_not_existing()
    {
        var store = new FakeDoseStore(initialStatus: null);
        var cas = BuildApplier(store);

        var outcome = await cas.ApplyAsync(DoseId, FamilyId, DoseStatus.Taken, "openid_elder", null);

        outcome.IsRejected.Should().BeTrue();
        outcome.Exists.Should().BeFalse();
        outcome.RejectCode.Should().Be(1005);
    }

    private static DoseCasApplier BuildApplier(FakeDoseStore store)
    {
        var clock = Substitute.For<IClock>();
        clock.NowMs.Returns(DueTs + 60_000);

        var db = Substitute.For<IAppDbContext>();
        db.GetDoseSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(store.Read()));
        db.CompareAndSetDoseAsync(Arg.Any<DoseCasUpdate>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(store.CompareAndSet(ci.Arg<DoseCasUpdate>())));

        return new DoseCasApplier(db, clock, lateToleranceMin: 15);
    }

    /// <summary>内存里的 dose_logs 单行，只实现"状态 CAS"这一件事。</summary>
    private sealed class FakeDoseStore
    {
        private readonly string? _initialStatus;
        private string? _status;

        public FakeDoseStore(string? initialStatus)
        {
            _initialStatus = initialStatus;
            _status = initialStatus;
        }

        /// <summary>前 N 次读取返回初始状态，模拟多个请求在写入前都读到同一份旧快照。</summary>
        public int StaleReads { get; set; }

        /// <summary>第一次 CAS 时先把状态改成别的（模拟漏服判定抢先），让这次 CAS 落空。</summary>
        public string? StealOnFirstCas { get; set; }

        /// <summary>每次 CAS 都落空且状态不变，用来验证重试有上界。</summary>
        public bool AlwaysLoseCas { get; set; }

        public int CasCalls { get; private set; }

        public string? Status => _status;

        public DoseSnapshot? Read() =>
            _status is null
                ? null
                : new DoseSnapshot(
                    StaleReads-- > 0 ? _initialStatus! : _status,
                    IsCorrected: false,
                    DueDate,
                    Slot);

        public int CompareAndSet(DoseCasUpdate update)
        {
            CasCalls++;
            if (AlwaysLoseCas) return 0;

            if (StealOnFirstCas is { } thief && CasCalls == 1)
            {
                _status = thief;
                StealOnFirstCas = null;
                return 0;
            }

            if (_status != update.From.ToWire()) return 0;
            _status = update.To.ToWire();
            return 1;
        }
    }
}
