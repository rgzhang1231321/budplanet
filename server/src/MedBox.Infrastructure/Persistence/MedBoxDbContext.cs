using System.Data;
using System.Data.Common;
using System.Text;
using MedBox.Application.Abstractions;
using MedBox.Domain;
using MedBox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MedBox.Infrastructure.Persistence;

/// <summary>
/// EF 只做映射，schema 由 db/migrations/V*.sql 构建。
/// 禁止 Database.Migrate() / EnsureCreated() / dotnet ef（CI 出现即失败）。
/// C# 属性 PascalCase → 列名 snake_case，逐个 HasColumnName 写死。
/// </summary>
public sealed class MedBoxDbContext(DbContextOptions<MedBoxDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Medicine> Medicines => Set<Medicine>();
    public DbSet<MedicineCommon> MedicinesCommon => Set<MedicineCommon>();
    public DbSet<MedicineUsageStat> MedicineUsageStats => Set<MedicineUsageStat>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<DoseLog> DoseLogs => Set<DoseLog>();
    public DbSet<AppConfigEntry> AppConfigs => Set<AppConfigEntry>();
    public DbSet<CorrectLog> CorrectLogs => Set<CorrectLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<AdEvent> AdEvents => Set<AdEvent>();
    public DbSet<PushTask> PushTasks => Set<PushTask>();
    public DbSet<WeeklyStat> WeeklyStats => Set<WeeklyStat>();
    public DbSet<StatsDaily> StatsDaily => Set<StatsDaily>();
    public DbSet<DoseView> DoseViews => Set<DoseView>();

    IQueryable<User> IAppDbContext.Users => Users;
    IQueryable<Family> IAppDbContext.Families => Families;
    IQueryable<FamilyMember> IAppDbContext.FamilyMembers => FamilyMembers;
    IQueryable<Patient> IAppDbContext.Patients => Patients;
    IQueryable<Medicine> IAppDbContext.Medicines => Medicines;
    IQueryable<MedicineCommon> IAppDbContext.MedicinesCommon => MedicinesCommon;
    IQueryable<MedicineUsageStat> IAppDbContext.MedicineUsageStats => MedicineUsageStats;
    IQueryable<Plan> IAppDbContext.Plans => Plans;
    IQueryable<DoseLog> IAppDbContext.DoseLogs => DoseLogs;
    IQueryable<AppConfigEntry> IAppDbContext.AppConfigs => AppConfigs;
    IQueryable<CorrectLog> IAppDbContext.CorrectLogs => CorrectLogs;
    IQueryable<AuditLog> IAppDbContext.AuditLogs => AuditLogs;
    IQueryable<Subscription> IAppDbContext.Subscriptions => Subscriptions;
    IQueryable<Entitlement> IAppDbContext.Entitlements => Entitlements;
    IQueryable<AdEvent> IAppDbContext.AdEvents => AdEvents;
    IQueryable<PushTask> IAppDbContext.PushTasks => PushTasks;
    IQueryable<WeeklyStat> IAppDbContext.WeeklyStats => WeeklyStats;
    IQueryable<StatsDaily> IAppDbContext.StatsDaily => StatsDaily;
    IQueryable<DoseView> IAppDbContext.DoseViews => DoseViews;

    void IAppDbContext.Add<T>(T entity) => Set<T>().Add(entity);

    void IAppDbContext.Remove<T>(T entity) => Set<T>().Remove(entity);

    async Task<DoseSnapshot?> IAppDbContext.GetDoseSnapshotAsync(string doseId, string familyId, CancellationToken ct)
        => await DoseLogs.AsNoTracking()
            .Where(d => d.DoseId == doseId && d.FamilyId == familyId)
            .Select(d => new DoseSnapshot(d.Status, d.IsCorrected, d.DueDate, d.Slot))
            .FirstOrDefaultAsync(ct);

    Task<int> IAppDbContext.CompareAndSetDoseAsync(DoseCasUpdate u, CancellationToken ct)
    {
        var from = u.From.ToWire();
        var to = u.To.ToWire();

        // 守卫三层：主键 + 家庭 + "当前状态还是读到的那个状态"，再叠 is_corrected=false
        // （人工改判过就锁死，missed→taken 也只放行系统判的漏服）。
        var target = DoseLogs.Where(d =>
            d.DoseId == u.DoseId && d.FamilyId == u.FamilyId && d.Status == from && !d.IsCorrected);

        if (u.To == DoseStatus.Taken)
        {
            return target.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, to)
                .SetProperty(d => d.TakenAt, (long?)u.NowMs)
                .SetProperty(d => d.TakenBy, u.OperatorOpenid)
                .SetProperty(d => d.IsLate, u.IsLate)
                .SetProperty(d => d.SkipReason, (string?)null), ct);
        }

        return target.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, to)
            .SetProperty(d => d.TakenAt, (long?)null)
            .SetProperty(d => d.TakenBy, (string?)null)
            .SetProperty(d => d.IsLate, false)
            .SetProperty(d => d.SkipReason, u.SkipReason), ct);
    }

    Task<int> IAppDbContext.CompareAndSetDoseCorrectAsync(DoseCorrectUpdate u, CancellationToken ct)
    {
        var from = u.From.ToWire();
        var to = u.To.ToWire();

        // 守卫同打卡：主键 + 家庭 + 当前状态仍是读到的那个 + 未改判过（改判不可逆）
        return DoseLogs
            .Where(d => d.DoseId == u.DoseId && d.FamilyId == u.FamilyId && d.Status == from && !d.IsCorrected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, to)
                .SetProperty(d => d.IsCorrected, true)
                .SetProperty(d => d.CheckedBy, u.OperatorOpenid)
                .SetProperty(d => d.CheckedAt, (long?)u.NowMs), ct);
    }

    async Task<int> IAppDbContext.InsertDoseLogsIgnoreConflictAsync(IReadOnlyList<DoseLog> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        var conn = Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere) await Database.OpenConnectionAsync(ct);

        try
        {
            await using var cmd = conn.CreateCommand();
            var sb = new StringBuilder();
            sb.Append("INSERT INTO dose_logs (dose_id, family_id, patient_id, plan_id, medicine_id, dose_text, ");
            sb.Append("med_name_snapshot, due_date, slot, due_ts, status, is_late, snooze_count, remind_status, ");
            sb.Append("alert_status, week_key, is_corrected, created_at) VALUES ");

            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var b = i * 18;
                sb.Append($"(@p{b}, @p{b + 1}, @p{b + 2}, @p{b + 3}, @p{b + 4}, @p{b + 5}, @p{b + 6}, @p{b + 7}, ");
                sb.Append($"@p{b + 8}, @p{b + 9}, @p{b + 10}, @p{b + 11}, @p{b + 12}, @p{b + 13}, @p{b + 14}, ");
                sb.Append($"@p{b + 15}, @p{b + 16}, @p{b + 17})");

                var r = rows[i];
                Add(cmd, $"@p{b}", r.DoseId);
                Add(cmd, $"@p{b + 1}", r.FamilyId);
                Add(cmd, $"@p{b + 2}", r.PatientId);
                Add(cmd, $"@p{b + 3}", r.PlanId);
                Add(cmd, $"@p{b + 4}", r.MedicineId);
                Add(cmd, $"@p{b + 5}", r.DoseText);
                Add(cmd, $"@p{b + 6}", r.MedNameSnapshot);
                Add(cmd, $"@p{b + 7}", r.DueDate);
                Add(cmd, $"@p{b + 8}", r.Slot);
                Add(cmd, $"@p{b + 9}", r.DueTs);
                Add(cmd, $"@p{b + 10}", r.Status);
                Add(cmd, $"@p{b + 11}", r.IsLate);
                Add(cmd, $"@p{b + 12}", r.SnoozeCount);
                Add(cmd, $"@p{b + 13}", r.RemindStatus);
                Add(cmd, $"@p{b + 14}", r.AlertStatus);
                Add(cmd, $"@p{b + 15}", r.WeekKey);
                Add(cmd, $"@p{b + 16}", r.IsCorrected);
                Add(cmd, $"@p{b + 17}", r.CreatedAt);
            }

            // 已存在的行一律不动（绝不覆盖用户打卡），只补确实缺的
            sb.Append(" ON CONFLICT (dose_id) DO NOTHING");
            cmd.CommandText = sb.ToString();
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (openedHere) await Database.CloseConnectionAsync();
        }
    }

    Task<int> IAppDbContext.InsertAdEventIgnoreConflictAsync(AdEvent row, CancellationToken ct)
        // 并发重复回调不再靠"先查再插"：两个回调同时到，后插的那条撞主键抛异常被映射成 1009，
        // 前端看到"服务异常"，而用户其实已经拿到权益了。
        => Database.ExecuteSqlRawAsync(
            "INSERT INTO ad_events (ad_trace, openid, family_id, product_id, granted, created_at) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}) ON CONFLICT (ad_trace) DO NOTHING",
            new object[] { row.AdTrace, row.Openid, row.FamilyId, row.ProductId, row.Granted, row.CreatedAt }, ct);

    Task<int> IAppDbContext.InsertPushTaskIgnoreConflictAsync(PushTask row, CancellationToken ct)
        // 与剂量行同族：AnyAsync + Add 是读-改-写，两个 tick（或两个实例）同时入队必撞主键 →
        // 1009，而任务其实已经排上了。改成 INSERT … ON CONFLICT DO NOTHING，返回真正新插的行数。
        => Database.ExecuteSqlRawAsync(
            "INSERT INTO push_tasks (task_id, shard, openid, tmpl_key, status, priority, payload, " +
            "retry_count, next_retry_at, created_at) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}, {8}, {9}) ON CONFLICT (task_id) DO NOTHING",
            new object[]
            {
                row.TaskId, row.Shard, row.Openid, row.TmplKey, row.Status, row.Priority,
                row.Payload, row.RetryCount, row.NextRetryAt, row.CreatedAt,
            }, ct);

    async Task<int> IAppDbContext.AddMedicineUsageAsync(
        string nameKey, string name, string? spec, string? defaultDose, int delta, long nowMs, CancellationToken ct)
    {
        if (delta > 0)
            // 加：单条 upsert。展示值跟着最近一次添加走（用户改了规格，榜上也该是新规格）
            return await Database.ExecuteSqlRawAsync(
                "INSERT INTO medicine_usage_stats (name_key, name, spec, default_dose, use_count, last_used_at) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}) " +
                "ON CONFLICT (name_key) DO UPDATE SET " +
                "use_count = medicine_usage_stats.use_count + EXCLUDED.use_count, " +
                "last_used_at = EXCLUDED.last_used_at, " +
                "name = EXCLUDED.name, " +
                "spec = COALESCE(EXCLUDED.spec, medicine_usage_stats.spec), " +
                "default_dose = COALESCE(EXCLUDED.default_dose, medicine_usage_stats.default_dose)",
                new object[] { nameKey, name, spec ?? (object)DBNull.Value, defaultDose ?? (object)DBNull.Value, delta, nowMs },
                ct);

        // 减：只 UPDATE，不 INSERT —— 减到一个不存在的药名不该凭空造一行 0 出来。
        // GREATEST 兜底 0：计数偏低比出现负数安全（负数会让榜单排序彻底乱掉）。
        return await Database.ExecuteSqlRawAsync(
            "UPDATE medicine_usage_stats SET use_count = GREATEST(use_count - 1, 0) WHERE name_key = {0}",
            new object[] { nameKey }, ct);
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        // SQL 里写 @p0，ADO.NET 参数名不带前缀（与 EF 自己生成 raw SQL 的口径一致）
        p.ParameterName = name.TrimStart('@');
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Openid);
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.Unionid).HasColumnName("unionid").HasMaxLength(64);
            e.Property(x => x.NickName).HasColumnName("nick_name").HasMaxLength(128);
            e.Property(x => x.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(512);
            e.Property(x => x.FamilyIds).HasColumnName("family_ids").HasColumnType("text[]");
            e.Property(x => x.ActiveFamilyId).HasColumnName("active_family_id").HasMaxLength(64);
            e.Property(x => x.Setting).HasColumnName("setting").HasColumnType("jsonb");
            e.Property(x => x.Entitlement).HasColumnName("entitlement").HasColumnType("jsonb");
            e.Property(x => x.PrivacyAgreedAt).HasColumnName("privacy_agreed_at");
            e.Property(x => x.DisclaimerAgreedAt).HasColumnName("disclaimer_agreed_at");
            e.Property(x => x.Unreachable).HasColumnName("unreachable");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        });

        b.Entity<Family>(e =>
        {
            e.ToTable("families");
            e.HasKey(x => x.FamilyId);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.OwnerOpenid).HasColumnName("owner_openid").HasMaxLength(64);
            e.Property(x => x.InviteToken).HasColumnName("invite_token").HasMaxLength(64);
            e.Property(x => x.InviteExpireAt).HasColumnName("invite_expire_at");
            e.Property(x => x.Tier).HasColumnName("tier").HasMaxLength(16);
            e.Property(x => x.PatientCount).HasColumnName("patient_count");
            e.Property(x => x.MemberCount).HasColumnName("member_count");
            e.Property(x => x.HistoryForeverAt).HasColumnName("history_forever_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<FamilyMember>(e =>
        {
            e.ToTable("family_members");
            e.HasKey(x => new { x.FamilyId, x.Openid });
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(16);
            e.Property(x => x.Relation).HasColumnName("relation").HasMaxLength(32);
            e.Property(x => x.IsPatient).HasColumnName("is_patient");
            e.Property(x => x.SelfPatientId).HasColumnName("self_patient_id").HasMaxLength(64);
            e.Property(x => x.NotifyEnabled).HasColumnName("notify_enabled");
            e.Property(x => x.JoinedAt).HasColumnName("joined_at");
        });

        b.Entity<Patient>(e =>
        {
            e.ToTable("patients");
            e.HasKey(x => x.PatientId);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.Alias).HasColumnName("alias").HasMaxLength(64);
            e.Property(x => x.AvatarFileId).HasColumnName("avatar_file_id").HasMaxLength(512);
            e.Property(x => x.SelfOpenid).HasColumnName("self_openid").HasMaxLength(64);
            e.Property(x => x.ChronicTags).HasColumnName("chronic_tags").HasColumnType("text[]");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.DisplayMode).HasColumnName("display_mode").HasMaxLength(8);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<Medicine>(e =>
        {
            e.ToTable("medicines");
            e.HasKey(x => x.MedicineId);
            e.Property(x => x.MedicineId).HasColumnName("medicine_id").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.Spec).HasColumnName("spec").HasMaxLength(128);
            e.Property(x => x.DefaultDose).HasColumnName("default_dose").HasMaxLength(32);
            e.Property(x => x.DoseAmount).HasColumnName("dose_amount").HasPrecision(10, 3);
            e.Property(x => x.DoseUnit).HasColumnName("dose_unit").HasMaxLength(16);
            e.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(255);
            e.Property(x => x.PhotoFileId).HasColumnName("photo_file_id").HasMaxLength(512);
            e.Property(x => x.DrugCode).HasColumnName("drug_code").HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<MedicineCommon>(e =>
        {
            e.ToTable("medicines_common");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.Spec).HasColumnName("spec").HasMaxLength(128);
            e.Property(x => x.DefaultDose).HasColumnName("default_dose").HasMaxLength(32);
            e.Property(x => x.DoseAmount).HasColumnName("dose_amount").HasPrecision(10, 3);
            e.Property(x => x.DoseUnit).HasColumnName("dose_unit").HasMaxLength(16);
            e.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<MedicineUsageStat>(e =>
        {
            e.ToTable("medicine_usage_stats");
            e.HasKey(x => x.NameKey);
            e.Property(x => x.NameKey).HasColumnName("name_key").HasMaxLength(128);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.Spec).HasColumnName("spec").HasMaxLength(128);
            e.Property(x => x.DefaultDose).HasColumnName("default_dose").HasMaxLength(32);
            e.Property(x => x.UseCount).HasColumnName("use_count");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        });

        b.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.HasKey(x => x.PlanId);
            e.Property(x => x.PlanId).HasColumnName("plan_id").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.MedicineId).HasColumnName("medicine_id").HasMaxLength(64);
            e.Property(x => x.Slots).HasColumnName("slots").HasColumnType("jsonb");
            e.Property(x => x.TimesMinute).HasColumnName("times_minute").HasColumnType("integer[]");
            e.Property(x => x.CycleType).HasColumnName("cycle_type").HasMaxLength(16);
            e.Property(x => x.CycleDays).HasColumnName("cycle_days");
            e.Property(x => x.CycleEndDate).HasColumnName("cycle_end_date").HasMaxLength(10);
            e.Property(x => x.StartDate).HasColumnName("start_date").HasMaxLength(10);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.PauseUntil).HasColumnName("pause_until").HasMaxLength(10);
            e.Property(x => x.AdvanceMin).HasColumnName("advance_min");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<DoseLog>(e =>
        {
            e.ToTable("dose_logs");
            e.HasKey(x => x.DoseId);
            e.Property(x => x.DoseId).HasColumnName("dose_id").HasMaxLength(128);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.PlanId).HasColumnName("plan_id").HasMaxLength(64);
            e.Property(x => x.MedicineId).HasColumnName("medicine_id").HasMaxLength(64);
            e.Property(x => x.DoseText).HasColumnName("dose_text").HasMaxLength(32);
            e.Property(x => x.MedNameSnapshot).HasColumnName("med_name_snapshot").HasMaxLength(128);
            e.Property(x => x.DueDate).HasColumnName("due_date").HasMaxLength(10);
            e.Property(x => x.Slot).HasColumnName("slot");
            e.Property(x => x.DueTs).HasColumnName("due_ts");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.TakenAt).HasColumnName("taken_at");
            e.Property(x => x.TakenBy).HasColumnName("taken_by").HasMaxLength(64);
            e.Property(x => x.IsLate).HasColumnName("is_late");
            e.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(255);
            e.Property(x => x.SnoozeCount).HasColumnName("snooze_count");
            e.Property(x => x.RemindStatus).HasColumnName("remind_status");
            e.Property(x => x.RemindAt).HasColumnName("remind_at");
            e.Property(x => x.RemindErrcode).HasColumnName("remind_errcode");
            e.Property(x => x.AlertStatus).HasColumnName("alert_status");
            e.Property(x => x.AlertAt).HasColumnName("alert_at");
            e.Property(x => x.WeekKey).HasColumnName("week_key").HasMaxLength(8);
            e.Property(x => x.IsCorrected).HasColumnName("is_corrected");
            e.Property(x => x.CheckedBy).HasColumnName("checked_by").HasMaxLength(64);
            e.Property(x => x.CheckedAt).HasColumnName("checked_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<AppConfigEntry>(e =>
        {
            e.ToTable("app_config");
            e.HasKey(x => x.ConfigKey);
            e.Property(x => x.ConfigKey).HasColumnName("config_key").HasMaxLength(32);
            e.Property(x => x.ConfigValue).HasColumnName("config_value").HasColumnType("jsonb");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
        });

        b.Entity<CorrectLog>(e =>
        {
            e.ToTable("correct_logs");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).HasColumnName("log_id").HasMaxLength(64);
            e.Property(x => x.DoseId).HasColumnName("dose_id").HasMaxLength(128);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.OperatorOpenid).HasColumnName("operator_openid").HasMaxLength(64);
            e.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(16);
            e.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(16);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(255);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).HasColumnName("log_id").HasMaxLength(64);
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(64);
            e.Property(x => x.ResultCode).HasColumnName("result_code");
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<DoseView>(e =>
        {
            e.ToTable("dose_views");
            e.HasKey(x => x.ViewId);
            e.Property(x => x.ViewId).HasColumnName("view_id").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.ViewerOpenid).HasColumnName("viewer_openid").HasMaxLength(64);
            e.Property(x => x.ViewerRole).HasColumnName("viewer_role").HasMaxLength(16);
            e.Property(x => x.ViewerAlias).HasColumnName("viewer_alias").HasMaxLength(64);
            e.Property(x => x.ViewedAt).HasColumnName("viewed_at");
            e.HasIndex(x => new { x.FamilyId, x.ViewedAt });
        });

        b.Entity<PushTask>(e =>
        {
            e.ToTable("push_tasks");
            e.HasKey(x => x.TaskId);
            e.Property(x => x.TaskId).HasColumnName("task_id").HasMaxLength(128);
            e.Property(x => x.Shard).HasColumnName("shard");
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.TmplKey).HasColumnName("tmpl_key").HasMaxLength(16);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.RetryCount).HasColumnName("retry_count");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(x => x.Errcode).HasColumnName("errcode");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
        });

        b.Entity<WeeklyStat>(e =>
        {
            e.ToTable("weekly_stats");
            e.HasKey(x => new { x.FamilyId, x.PatientId, x.WeekKey });
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.PatientId).HasColumnName("patient_id").HasMaxLength(64);
            e.Property(x => x.WeekKey).HasColumnName("week_key").HasMaxLength(8);
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.TakenCnt).HasColumnName("taken_cnt");
            e.Property(x => x.MissedCnt).HasColumnName("missed_cnt");
            e.Property(x => x.SkippedCnt).HasColumnName("skipped_cnt");
            e.Property(x => x.ManualCorrectCount).HasColumnName("manual_correct_count");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<StatsDaily>(e =>
        {
            e.ToTable("stats_daily");
            e.HasKey(x => new { x.StatDate, x.MetricKey });
            e.Property(x => x.StatDate).HasColumnName("stat_date").HasMaxLength(10);
            e.Property(x => x.MetricKey).HasColumnName("metric_key").HasMaxLength(32);
            e.Property(x => x.MetricValue).HasColumnName("metric_value").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<Entitlement>(e =>
        {
            e.ToTable("entitlements");
            e.HasKey(x => x.EntitlementId);
            e.Property(x => x.EntitlementId).HasColumnName("entitlement_id").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.ProductId).HasColumnName("product_id").HasMaxLength(32);
            e.Property(x => x.Credits).HasColumnName("credits");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(16);
            e.Property(x => x.ExpireAt).HasColumnName("expire_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<AdEvent>(e =>
        {
            e.ToTable("ad_events");
            e.HasKey(x => x.AdTrace);
            e.Property(x => x.AdTrace).HasColumnName("ad_trace").HasMaxLength(128);
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64);
            e.Property(x => x.ProductId).HasColumnName("product_id").HasMaxLength(32);
            e.Property(x => x.Granted).HasColumnName("granted");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => new { x.Openid, x.TmplKey });
            e.Property(x => x.Openid).HasColumnName("openid").HasMaxLength(64);
            e.Property(x => x.TmplKey).HasColumnName("tmpl_key").HasMaxLength(16);
            e.Property(x => x.TmplId).HasColumnName("tmpl_id").HasMaxLength(128);
            e.Property(x => x.Quota).HasColumnName("quota");
            e.Property(x => x.MainSwitch).HasColumnName("main_switch");
            e.Property(x => x.GrantedTotal).HasColumnName("granted_total");
            e.Property(x => x.UsedTotal).HasColumnName("used_total");
            e.Property(x => x.LastGrantAt).HasColumnName("last_grant_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.ExhaustWarnedAt).HasColumnName("exhaust_warned_at");
        });
    }
}
