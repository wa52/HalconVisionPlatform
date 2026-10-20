using HalconVision.Domain;

namespace HalconVision.Application;

public sealed record MatchCandidatePolicy(double MinimumScore, int ExpectedCount, int CandidateLimit,
    double PositionTolerance, double AngleToleranceDegrees, double MinimumScoreGap,
    double AngleStart, double AngleExtent, Rectangle2D? AcceptanceRegion = null);

/// <summary>Pure, deterministic acceptance gate. A rejection never contains usable poses.</summary>
public static class MatchCandidateEvaluator
{
    public static QuickMatchImageResult Evaluate(int imageIndex, IReadOnlyList<QuickMatchPose> candidates,
        MatchCandidatePolicy policy, int width, int height)
    {
        Validate(policy);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var raw = candidates.ToArray();
        QuickMatchImageResult Reject(string code, string message, string reason) => new(imageIndex, [], raw, new(code, message), reason);
        if (raw.Any(p => !double.IsFinite(p.Row) || !double.IsFinite(p.Column) || !double.IsFinite(p.AngleDegrees) ||
            !double.IsFinite(p.Score) || p.Score < 0 || p.Score > 1))
            return Reject(QuickMatchStatus.Rejected, "候选包含无效坐标或评分", "INVALID_CANDIDATE") with { RawCandidates = [] };
        if (raw.Length == 0) return Reject(QuickMatchStatus.NotFound, "未找到模板候选", "NO_CANDIDATE");
        if (raw.Length >= policy.CandidateLimit)
            return Reject(QuickMatchStatus.Ambiguous, "候选数达到搜索上限，无法确认目标唯一性；请缩小范围或提高候选上限", "CANDIDATE_LIMIT_REACHED");
        bool InRange(QuickMatchPose p) => p.Row >= 0 && p.Row <= height - 1 && p.Column >= 0 && p.Column <= width - 1 &&
            (policy.AcceptanceRegion is not { } r || p.Row >= r.RowBegin && p.Row <= r.RowEnd && p.Column >= r.ColumnBegin && p.Column <= r.ColumnEnd) &&
            (policy.AngleExtent >= 360 || PositiveAngle(p.AngleDegrees - policy.AngleStart) <= policy.AngleExtent + 1e-6);
        var viable = raw.Where(InRange).OrderByDescending(p => p.Score).ThenBy(p => p.Row).ThenBy(p => p.Column).ThenBy(p => p.AngleDegrees).ToArray();
        var distinct = new List<QuickMatchPose>();
        foreach (var p in viable)
            if (!distinct.Any(q => Distance(p, q) <= policy.PositionTolerance && AngleDistance(p.AngleDegrees, q.AngleDegrees) <= policy.AngleToleranceDegrees))
                distinct.Add(p);
        var qualified = distinct.Where(p => p.Score >= policy.MinimumScore).ToArray();
        if (qualified.Length < policy.ExpectedCount)
            return raw.Any(p => p.Score >= policy.MinimumScore && !InRange(p))
                ? Reject(QuickMatchStatus.OutOfRange, "候选位姿超出允许位置或角度，未达到期望目标数", "POSE_OUT_OF_RANGE")
                : Reject(QuickMatchStatus.NotFound, $"合格目标 {qualified.Length} 个，期望 {policy.ExpectedCount} 个", "INSUFFICIENT_TARGETS");
        if (qualified.Length > policy.ExpectedCount)
            return Reject(QuickMatchStatus.Ambiguous, $"合格目标 {qualified.Length} 个超过期望 {policy.ExpectedCount} 个，不能可靠定位", "EXTRA_TARGET_OR_ORIENTATION");
        for (var i = 0; i < qualified.Length; i++)
            for (var j = i + 1; j < qualified.Length; j++)
                if (Distance(qualified[i], qualified[j]) <= policy.PositionTolerance)
                    return Reject(QuickMatchStatus.Ambiguous, "同一位置存在不同方向候选，不能视为多个独立目标", "ORIENTATION_AMBIGUITY");
        var competing = distinct.Skip(qualified.Length).FirstOrDefault();
        if (competing is not null && qualified[^1].Score - competing.Score <= policy.MinimumScoreGap)
            return Reject(QuickMatchStatus.Ambiguous, "额外候选与接受评分过于接近，请检查模板或缩小搜索范围", "SCORE_GAP_TOO_SMALL");
        return new(imageIndex, qualified, raw, new(QuickMatchStatus.Found, $"通过可靠性检查，接受 {qualified.Length} 个目标"));
    }

    private static void Validate(MatchCandidatePolicy p)
    {
        if (!double.IsFinite(p.MinimumScore) || p.MinimumScore <= 0 || p.MinimumScore > 1 || p.ExpectedCount < 1 ||
            p.CandidateLimit <= p.ExpectedCount || !double.IsFinite(p.PositionTolerance) || p.PositionTolerance < 0 ||
            !double.IsFinite(p.AngleToleranceDegrees) || p.AngleToleranceDegrees < 0 || p.AngleToleranceDegrees > 180 ||
            !double.IsFinite(p.MinimumScoreGap) || p.MinimumScoreGap < 0 || p.MinimumScoreGap > 1 ||
            !double.IsFinite(p.AngleStart) || p.AngleStart < -180 || p.AngleStart > 180 ||
            !double.IsFinite(p.AngleExtent) || p.AngleExtent < 0 || p.AngleExtent > 360 || p.AngleStart + p.AngleExtent > 180.000001)
            throw new InvalidDataException("快捷匹配可靠性参数无效。");
        if (p.AcceptanceRegion is { } r && (!double.IsFinite(r.RowBegin) || !double.IsFinite(r.ColumnBegin) ||
            !double.IsFinite(r.RowEnd) || !double.IsFinite(r.ColumnEnd) || r.RowBegin > r.RowEnd || r.ColumnBegin > r.ColumnEnd))
            throw new InvalidDataException("快捷匹配允许位置范围无效。");
    }
    private static double Distance(QuickMatchPose a, QuickMatchPose b) => Math.Sqrt(Math.Pow(a.Row - b.Row, 2) + Math.Pow(a.Column - b.Column, 2));
    private static double PositiveAngle(double angle) => (angle % 360 + 360) % 360;
    public static double AngleDistance(double a, double b) => Math.Abs(PositiveAngle(a - b + 180) - 180);
}
