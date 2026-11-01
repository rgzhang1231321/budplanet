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
}
