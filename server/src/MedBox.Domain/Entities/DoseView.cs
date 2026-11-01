namespace MedBox.Domain.Entities;

/// <summary>
/// 已读回执：谁在什么时候看过这个家庭的药单。
/// 只承载"有人在意我"这一条信息 —— 前端只渲染一行灰字，禁红点/弹窗/声音（契约 rows[] 的 recentView）。
/// view_id 用 openid_日期 幂等：一天一条、更新为当天最后一次，避免每次下拉刷新都插一行。
/// </summary>
public class DoseView
{
    public string ViewId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string? PatientId { get; set; }
    public string ViewerOpenid { get; set; } = "";
    /// <summary>admin | member | elder —— 取回执时要排掉 elder（同龄人看不看不构成"被关心"的证据）。</summary>
    public string? ViewerRole { get; set; }
    public string? ViewerAlias { get; set; }
    public long ViewedAt { get; set; }
}
