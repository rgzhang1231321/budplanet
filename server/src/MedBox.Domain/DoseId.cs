namespace MedBox.Domain;

/// <summary>
/// 确定性剂量主键 = ${pid}_${date}_${slot}，slot = 当日分钟数 0–1439。
/// 三处必须是同一个值：rows[].slot、checkin.submit.items[].slot、dose_id 的第三段。
/// </summary>
public static class DoseId
{
    public static string Build(string patientId, string dueDate, int slot)
    {
        if (string.IsNullOrWhiteSpace(patientId)) throw new ValidationException("patient_id 不能为空");
        if (slot is < 0 or > 1439) throw new ValidationException("slot 必须在 0–1439 之间");
        return $"{patientId}_{dueDate}_{slot}";
    }
}
