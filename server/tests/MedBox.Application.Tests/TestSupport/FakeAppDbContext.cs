using MedBox.Application.Abstractions;
using MedBox.Domain.Entities;
using NSubstitute;

namespace MedBox.Application.Tests.TestSupport;

/// <summary>
/// 用 NSubstitute 拼一个 <see cref="IAppDbContext"/> 替身。三件事必须处理好：
///
/// ① 每个 IQueryable 属性都挂一个**异步可查询**的内存集合（见 <see cref="AsyncQueryableExtensions"/>）——
///    否则 EF 的 <c>ToListAsync</c> 会在运行时抛 "not of type EntityQueryable"。
/// ② <c>Add</c>/<c>Remove</c> 必须真的落到同一个 List 上。<see cref="UseCases.FamilyJoinHandler"/>
///    在 <c>db.Add(member)</c> 之后还要再调一次 <c>FamilyInfoHandler</c>（内部会重新查成员），
///    替身如果把 Add 吞了，新成员场景就会以 NotMemberException 收场 —— 那是替身的 bug，不是用例的。
/// ③ <c>SaveChangesAsync</c> 默认返回 1。用例要验"一行都没存"时自己覆写。
///
/// 之所以不自己手写一个实现 IAppDbContext 的 Fake 类：接口有 20 个 IQueryable 属性 + 8 个方法，
/// 手写一遍既啰骚又容易漏配；NSubstitute 只声明本用例真正会摸到的属性，意图更清楚。
/// </summary>
internal static class FakeAppDbContext
{
    public static IAppDbContext Create(
        IEnumerable<User>? users = null,
        IEnumerable<Family>? families = null,
        IEnumerable<FamilyMember>? members = null,
        IEnumerable<Patient>? patients = null,
        IEnumerable<Medicine>? medicines = null,
        IEnumerable<MedicineCommon>? medicinesCommon = null,
        IEnumerable<MedicineUsageStat>? usageStats = null,
        IEnumerable<Plan>? plans = null,
        IEnumerable<DoseLog>? doseLogs = null,
        IEnumerable<AppConfigEntry>? appConfigs = null,
        IEnumerable<CorrectLog>? correctLogs = null,
        IEnumerable<AuditLog>? auditLogs = null,
        IEnumerable<Subscription>? subscriptions = null,
        IEnumerable<Entitlement>? entitlements = null,
        IEnumerable<AdEvent>? adEvents = null,
        IEnumerable<PushTask>? pushTasks = null,
        IEnumerable<WeeklyStat>? weeklyStats = null,
        IEnumerable<StatsDaily>? statsDaily = null,
        IEnumerable<DoseView>? doseViews = null)
    {
        var db = Substitute.For<IAppDbContext>();

        var userList = List(users);
        var familyList = List(families);
        var memberList = List(members);
        var patientList = List(patients);
        var medicineList = List(medicines);
        var commonList = List(medicinesCommon);
        var usageList = List(usageStats);
        var planList = List(plans);
        var doseList = List(doseLogs);
        var configList = List(appConfigs);
        var correctList = List(correctLogs);
        var auditList = List(auditLogs);
        var subscriptionList = List(subscriptions);
        var entitlementList = List(entitlements);
        var adEventList = List(adEvents);
        var pushTaskList = List(pushTasks);
        var weeklyStatList = List(weeklyStats);
        var statsDailyList = List(statsDaily);
        var doseViewList = List(doseViews);

        db.Users.Returns(userList.AsAsyncQueryable());
        db.Families.Returns(familyList.AsAsyncQueryable());
        db.FamilyMembers.Returns(memberList.AsAsyncQueryable());
        db.Patients.Returns(patientList.AsAsyncQueryable());
        db.Medicines.Returns(medicineList.AsAsyncQueryable());
        db.MedicinesCommon.Returns(commonList.AsAsyncQueryable());
        db.MedicineUsageStats.Returns(usageList.AsAsyncQueryable());
        db.Plans.Returns(planList.AsAsyncQueryable());
        db.DoseLogs.Returns(doseList.AsAsyncQueryable());
        db.AppConfigs.Returns(configList.AsAsyncQueryable());
        db.CorrectLogs.Returns(correctList.AsAsyncQueryable());
        db.AuditLogs.Returns(auditList.AsAsyncQueryable());
        db.Subscriptions.Returns(subscriptionList.AsAsyncQueryable());
        db.Entitlements.Returns(entitlementList.AsAsyncQueryable());
        db.AdEvents.Returns(adEventList.AsAsyncQueryable());
        db.PushTasks.Returns(pushTaskList.AsAsyncQueryable());
        db.WeeklyStats.Returns(weeklyStatList.AsAsyncQueryable());
        db.StatsDaily.Returns(statsDailyList.AsAsyncQueryable());
        db.DoseViews.Returns(doseViewList.AsAsyncQueryable());

        Route(db, userList);
        Route(db, familyList);
        Route(db, memberList);
        Route(db, patientList);
        Route(db, medicineList);
        Route(db, commonList);
        Route(db, usageList);
        Route(db, planList);
        Route(db, doseList);
        Route(db, configList);
        Route(db, correctList);
        Route(db, auditList);
        Route(db, subscriptionList);
        Route(db, entitlementList);
        Route(db, adEventList);
        Route(db, pushTaskList);
        Route(db, weeklyStatList);
        Route(db, statsDailyList);
        Route(db, doseViewList);

        db.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        return db;
    }

    private static List<T> List<T>(IEnumerable<T>? source) => source is null ? [] : [.. source];

    private static void Route<T>(IAppDbContext db, List<T> list) where T : class
    {
        db.When(x => x.Add(Arg.Any<T>())).Do(ci => list.Add(ci.Arg<T>()));
        db.When(x => x.Remove(Arg.Any<T>())).Do(ci => list.Remove(ci.Arg<T>()));
    }

    /// <summary>
    /// 把 dose_logs 的幂等插入换成"按 dose_id 去重"的内存实现，模拟
    /// <c>INSERT … ON CONFLICT (dose_id) DO NOTHING</c>：重复插入返回 0 行且**不抛异常**。
    /// </summary>
    public static IAppDbContext WithIdempotentDoseInsert(this IAppDbContext db)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        db.InsertDoseLogsIgnoreConflictAsync(Arg.Any<IReadOnlyList<DoseLog>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = ci.Arg<IReadOnlyList<DoseLog>>();
                // 必须加锁：真库靠 ON CONFLICT (dose_id) DO NOTHING 保证原子性，
                // 而 HashSet 不是线程安全的。不加锁时两个并发调用可能给同一个 dose_id
                // 各记一次"已插入"，断言出来的就是替身自己的竞态，不是用例想验的行为。
                lock (seen)
                {
                    var inserted = 0;
                    foreach (var row in rows)
                        if (seen.Add(row.DoseId)) inserted++;
                    return Task.FromResult(inserted);
                }
            });
        return db;
    }
}
