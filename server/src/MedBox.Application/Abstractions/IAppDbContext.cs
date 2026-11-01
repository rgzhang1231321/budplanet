using MedBox.Domain.Entities;

namespace MedBox.Application.Abstractions;

/// <summary>
/// 应用层唯一的数据访问边界。暴露 IQueryable 而非 DbSet，避免 Application 依赖 EF。
/// 所有写操作走 Add + SaveChangesAsync（一次保存 = 一个事务）。
/// </summary>
public interface IAppDbContext
{
    IQueryable<User> Users { get; }
    IQueryable<Family> Families { get; }
    IQueryable<FamilyMember> FamilyMembers { get; }
    IQueryable<Patient> Patients { get; }
    IQueryable<Medicine> Medicines { get; }
    IQueryable<MedicineCommon> MedicinesCommon { get; }
    IQueryable<Plan> Plans { get; }
    IQueryable<DoseLog> DoseLogs { get; }
    IQueryable<AppConfigEntry> AppConfigs { get; }
    IQueryable<CorrectLog> CorrectLogs { get; }
    IQueryable<AuditLog> AuditLogs { get; }
    IQueryable<Subscription> Subscriptions { get; }
    IQueryable<Entitlement> Entitlements { get; }
    IQueryable<AdEvent> AdEvents { get; }
    IQueryable<PushTask> PushTasks { get; }
    IQueryable<WeeklyStat> WeeklyStats { get; }
    IQueryable<StatsDaily> StatsDaily { get; }
    IQueryable<DoseView> DoseViews { get; }

    void Add<T>(T entity) where T : class;

    void Remove<T>(T entity) where T : class;

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>dose_logs 一行的状态快照（不跟踪实体），CAS 判定前读一次。</summary>
    Task<DoseSnapshot?> GetDoseSnapshotAsync(string doseId, string familyId, CancellationToken ct = default);

    /// <summary>
    /// 状态 CAS：`UPDATE dose_logs SET … WHERE dose_id=@id AND status=@from AND is_corrected=false`，
    /// 返回受影响行数（1 = 写入成功，0 = 已被并发改过）。
    /// </summary>
    Task<int> CompareAndSetDoseAsync(DoseCasUpdate update, CancellationToken ct = default);

    /// <summary>
    /// 人工改判 CAS：`UPDATE dose_logs SET status=@to, is_corrected=true, checked_by=…, checked_at=…
    /// WHERE dose_id=@id AND family_id=@fid AND status=@from AND is_corrected=false`，返回受影响行数。
    /// </summary>
    Task<int> CompareAndSetDoseCorrectAsync(DoseCorrectUpdate update, CancellationToken ct = default);

    /// <summary>
    /// 剂量行幂等插入：`INSERT … ON CONFLICT (dose_id) DO NOTHING`，返回真正新增的行数。
    /// 不用"先查再插"——两个请求同时补齐（首页 + 打卡页）会撞主键，抛异常被映射成 1009。
    /// </summary>
    Task<int> InsertDoseLogsIgnoreConflictAsync(IReadOnlyList<DoseLog> rows, CancellationToken ct = default);

    /// <summary>
    /// 广告事件幂等插入：`INSERT … ON CONFLICT (ad_trace) DO NOTHING`，返回真正新增的行数。
    /// 0 行 = 同一个 ad_trace 已经被核销过（激励视频重复回调），调用方按幂等返回而不是报错。
    /// </summary>
    Task<int> InsertAdEventIgnoreConflictAsync(AdEvent row, CancellationToken ct = default);
}
