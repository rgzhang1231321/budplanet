namespace MedBox.Application.Abstractions;

public interface IJwtIssuer
{
    /// <summary>签发 access token；sub = openid，Claims 里不放药品/疾病信息。</summary>
    string Issue(string openid);

    string IssueRefresh(string openid);
}
