using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace UnityCli.Support;

/// <summary>
/// <c>assert</c> 명령과 워크플로 단계 검증/대기에서 공유하는 단일 평가기.
/// 경로 해석은 <see cref="JsonPathResolver"/> 를 재사용하고, 비교 연산만 추가로 담당한다.
/// </summary>
public static class AssertEvaluator
{
    /// <summary>assert 비교 연산자 종류.</summary>
    public enum AssertOp
    {
        /// <summary>경로 스칼라가 기대값과 문자열로 정확히 일치(Ordinal).</summary>
        Equals,

        /// <summary>경로 스칼라가 기대값 부분 문자열을 포함(Ordinal).</summary>
        Contains,

        /// <summary>경로가 존재(스칼라 여부 무관, 기대값 무시).</summary>
        Exists,

        /// <summary>경로 스칼라(숫자) &gt; 기대값(숫자).</summary>
        Gt,

        /// <summary>경로 스칼라(숫자) &lt; 기대값(숫자).</summary>
        Lt,

        /// <summary>경로 스칼라가 기대값 정규식과 매칭.</summary>
        Matches,
    }

    /// <summary>assert 평가 결과. <paramref name="Passed"/> 성공 여부, <paramref name="Actual"/> 실제 스칼라(없으면 null), <paramref name="Detail"/> 사람이 읽는 부가 설명.</summary>
    /// <param name="Passed">검증 통과 여부.</param>
    /// <param name="Actual">경로에서 해석한 실제 스칼라 값(해석 실패 시 null).</param>
    /// <param name="Detail">비숫자/잘못된 정규식 등 추가 진단 문자열(없으면 빈 문자열).</param>
    public readonly record struct AssertResult(bool Passed, string? Actual, string Detail);

    /// <summary>연산자 토큰 문자열(equals|contains|exists|gt|lt|matches, 대소문자 무시)을 <see cref="AssertOp"/> 으로 파싱한다.</summary>
    /// <param name="token">연산자 토큰.</param>
    /// <param name="op">파싱된 연산자.</param>
    /// <returns>인식 가능한 연산자면 true, 아니면 false.</returns>
    public static bool TryParseOp(string token, out AssertOp op)
    {
        switch (token?.ToLowerInvariant())
        {
            case "equals":
                op = AssertOp.Equals;
                return true;
            case "contains":
                op = AssertOp.Contains;
                return true;
            case "exists":
                op = AssertOp.Exists;
                return true;
            case "gt":
                op = AssertOp.Gt;
                return true;
            case "lt":
                op = AssertOp.Lt;
                return true;
            case "matches":
                op = AssertOp.Matches;
                return true;
            default:
                op = AssertOp.Exists;
                return false;
        }
    }

    /// <summary>지정한 경로의 값을 연산자와 기대값으로 평가한다. 경로 해석은 <see cref="JsonPathResolver"/> 를 재사용한다.</summary>
    /// <param name="root">평가 대상 루트 노드.</param>
    /// <param name="path">점/인덱스 경로(예: <c>result.name</c>, <c>data.gameViewWidth</c>).</param>
    /// <param name="op">비교 연산자.</param>
    /// <param name="expected">기대값(exists 는 무시).</param>
    /// <returns>평가 결과.</returns>
    public static AssertResult Evaluate(JsonNode? root, string path, AssertOp op, string? expected)
    {
        var node = JsonPathResolver.Resolve(root, path);
        var actual = JsonPathResolver.ResolveToScalar(root, path);

        switch (op)
        {
            case AssertOp.Exists:
                return new AssertResult(node is not null, actual, string.Empty);
            case AssertOp.Equals:
                return new AssertResult(actual is not null && string.Equals(actual, expected, StringComparison.Ordinal), actual, string.Empty);
            case AssertOp.Contains:
                return new AssertResult(actual is not null && expected is not null && actual.Contains(expected, StringComparison.Ordinal), actual, string.Empty);
            case AssertOp.Gt:
            case AssertOp.Lt:
                if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber)
                    || !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber))
                {
                    return new AssertResult(false, actual, "non-numeric");
                }

                var numericPassed = op == AssertOp.Gt ? actualNumber > expectedNumber : actualNumber < expectedNumber;
                return new AssertResult(numericPassed, actual, string.Empty);
            case AssertOp.Matches:
                if (actual is null)
                {
                    return new AssertResult(false, actual, string.Empty);
                }

                try
                {
                    return new AssertResult(Regex.IsMatch(actual, expected ?? string.Empty), actual, string.Empty);
                }
                catch (ArgumentException)
                {
                    return new AssertResult(false, actual, "invalid regex");
                }
            default:
                return new AssertResult(false, actual, string.Empty);
        }
    }
}
