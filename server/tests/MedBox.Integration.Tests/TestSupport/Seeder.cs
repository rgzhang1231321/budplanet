using Npgsql;

namespace MedBox.Integration.Tests.TestSupport;

/// <summary>
/// 直连测试库灌种子数据。
///
/// 为什么走 SQL 而不走 API：要验的是"某个用例在**已有数据**上的行为"
/// （限额、越权、部分成功），用 API 造这些前置状态会引入一串无关端点，
/// 一旦那些端点自己出错，失败的用例会指向错误的方向。
/// 直接写库的前置状态是确定的，失败的只有被测的那一条链路。
/// </summary>
public sealed class Seeder
{
    private readonly PostgresContainerFixture _fx;

    public Seeder(PostgresContainerFixture fx) => _fx = fx;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task<string> UserAsync(string openid)
    {
        await _fx.ExecuteAsync(
            """
            INSERT INTO users (openid, family_ids, setting, entitlement, created_at, last_login_at)
            VALUES (@openid, '{}', '{}'::jsonb, '{}'::jsonb, @now, @now)
            ON CONFLICT (openid) DO NOTHING
            """,
            _fx.P("openid", openid), _fx.P("now", NowMs()));
        return openid;
    }

    /// <summary>
    /// 建家庭。<paramref name="inviteToken"/> 只是给人看的标签，真正入库的令牌自动生成唯一值 ——
    /// V001 的 uq_families_invite_token 是 invite_token 上的 partial unique index，
    /// 用固定 token 会让不同用例互相撞号。
    /// 格式与 FamilyCreateHandler.NewToken() 一致："IVT" + 8 位大写十六进制。
    /// </summary>
    public async Task<string> FamilyAsync(string fid, string ownerOpenid, string inviteToken = "")
    {
        var token = "IVT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await _fx.ExecuteAsync(
            """
            INSERT INTO families (family_id, name, owner_openid, invite_token, tier, member_count, created_at)
            VALUES (@fid, @name, @owner, @ivt, 'free', 1, @now)
            ON CONFLICT (family_id) DO NOTHING
            """,
            _fx.P("fid", fid), _fx.P("name", "测试家庭"), _fx.P("owner", ownerOpenid),
            _fx.P("ivt", token), _fx.P("now", NowMs()));
        return fid;
    }

    public async Task MemberAsync(string fid, string openid, string role,
        string? selfPatientId = null, bool isPatient = false)
    {
        await _fx.ExecuteAsync(
            """
            INSERT INTO family_members (family_id, openid, role, is_patient, self_patient_id, joined_at)
            VALUES (@fid, @openid, @role, @isPatient, @self, @now)
            ON CONFLICT (family_id, openid) DO UPDATE
                SET role = EXCLUDED.role, self_patient_id = EXCLUDED.self_patient_id
            """,
            _fx.P("fid", fid), _fx.P("openid", openid), _fx.P("role", role),
            _fx.P("isPatient", isPatient), _fx.P("self", (object?)selfPatientId ?? DBNull.Value),
            _fx.P("now", NowMs()));
    }

    public async Task<string> PatientAsync(string pid, string fid, string? selfOpenid = null, string name = "我妈")
    {
        await _fx.ExecuteAsync(
            """
            INSERT INTO patients (patient_id, family_id, name, alias, chronic_tags, status, display_mode, created_at)
            VALUES (@pid, @fid, @name, @name, '{}', 'active', 'normal', @now)
            ON CONFLICT (patient_id) DO UPDATE SET self_openid = EXCLUDED.self_openid
            """,
            _fx.P("pid", pid), _fx.P("fid", fid), _fx.P("name", name), _fx.P("now", NowMs()));

        if (selfOpenid is not null)
        {
            await _fx.ExecuteAsync(
                "UPDATE patients SET self_openid = @openid WHERE patient_id = @pid",
                _fx.P("openid", selfOpenid), _fx.P("pid", pid));
        }

        return pid;
    }

    public async Task<string> MedicineAsync(string mid, string fid, string pid, string name = "苯磺酸氨氯地平片")
    {
        await _fx.ExecuteAsync(
            """
            INSERT INTO medicines (medicine_id, family_id, patient_id, name, spec, default_dose,
                                   dose_amount, dose_unit, status, created_by, created_at)
            VALUES (@mid, @fid, @pid, @name, '5mg×28片', '1片', 1, '片', 'active', 'seed', @now)
            ON CONFLICT (medicine_id) DO NOTHING
            """,
            _fx.P("mid", mid), _fx.P("fid", fid), _fx.P("pid", pid),
            _fx.P("name", name), _fx.P("now", NowMs()));
        return mid;
    }

    public async Task<string> PlanAsync(string planId, string fid, string pid, string mid,
        string startDate, int[] timesMinute)
    {
        var slots = "[" + string.Join(",", timesMinute.Select(m =>
            $"{{\"t\":\"{m / 60:D2}:{m % 60:D2}\",\"d\":\"1片\",\"slot_key\":\"s{m:D4}\"}}")) + "]";

        await _fx.ExecuteAsync(
            """
            INSERT INTO plans (plan_id, family_id, patient_id, medicine_id, slots, times_minute,
                               cycle_type, start_date, status, advance_min, created_by, updated_at)
            VALUES (@planId, @fid, @pid, @mid, @slots::jsonb, @times, 'long_term', @startDate,
                    'active', 0, 'seed', @now)
            ON CONFLICT (plan_id) DO NOTHING
            """,
            _fx.P("planId", planId), _fx.P("fid", fid), _fx.P("pid", pid), _fx.P("mid", mid),
            _fx.P("slots", slots), _fx.P("times", timesMinute), _fx.P("startDate", startDate),
            _fx.P("now", NowMs()));
        return planId;
    }

    /// <summary>手动造一行 dose_logs（打卡用例的前置状态）。</summary>
    public async Task DoseLogAsync(string doseId, string fid, string pid, string planId, string mid,
        string dateCn, int slot, string status = "pending", string? weekKey = null)
    {
        var dueTs = MedBox.Domain.TimeCn.SlotUtcMs(dateCn, slot);
        weekKey ??= MedBox.Domain.TimeCn.WeekKey(dateCn);

        await _fx.ExecuteAsync(
            """
            INSERT INTO dose_logs (dose_id, family_id, patient_id, plan_id, medicine_id, dose_text,
                                   med_name_snapshot, due_date, slot, due_ts, status, week_key, created_at)
            VALUES (@doseId, @fid, @pid, @planId, @mid, '1片', '苯磺酸氨氯地平片',
                    @date, @slot, @dueTs, @status, @weekKey, @now)
            ON CONFLICT (dose_id) DO UPDATE SET status = EXCLUDED.status
            """,
            _fx.P("doseId", doseId), _fx.P("fid", fid), _fx.P("pid", pid), _fx.P("planId", planId),
            _fx.P("mid", mid), _fx.P("date", dateCn), _fx.P("slot", slot), _fx.P("dueTs", dueTs),
            _fx.P("status", status), _fx.P("weekKey", weekKey), _fx.P("now", NowMs()));
    }

    public async Task<string> ConfigValueAsync()
    {
        await using var conn = await _fx.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT config_value::text FROM app_config WHERE config_key = 'runtime'", conn);
        var result = await cmd.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("app_config 里没有 'runtime' 这一行 —— V003 种子没跑成功？");
        return (string)result;
    }

    public Task SetConfigValueAsync(string json)
        => _fx.ExecuteAsync(
            "UPDATE app_config SET config_value = @json::jsonb, updated_at = @now WHERE config_key = 'runtime'",
            _fx.P("json", json), _fx.P("now", NowMs()));

    /// <summary>东八区自然日（与生产口径一致，不要用 DateTime.Today）。</summary>
    public static string TodayCn() => MedBox.Domain.TimeCn.DateCn(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
