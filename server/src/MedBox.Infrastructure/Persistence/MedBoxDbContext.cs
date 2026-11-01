using MedBox.Application.Abstractions;
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
