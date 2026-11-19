using AcadCopilot.Core.Agents;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

/// <summary>Usage / rate-limit messages of each CLI end the turn as <see cref="AgentFailureKind.RateLimited"/> (AI switching).</summary>
public sealed class ProviderLimitTests
{
    [Theory]
    // Claude Code
    [InlineData("You've hit your limit · resets 3pm (Asia/Calcutta)")]
    [InlineData("Claude AI usage limit reached|1736280000")]
    [InlineData("5-hour limit reached ∙ resets 7pm")]
    [InlineData("Weekly limit reached · resets Mon 9am")]
    [InlineData("You're out of extra usage · resets 4pm")]
    [InlineData("Credit balance is too low")]
    // Codex CLI
    [InlineData("You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), or try again in 2 hours 13 minutes.")]
    [InlineData("usage_limit_reached")]
    [InlineData("exceeded retry limit, last status: 429 Too Many Requests")]
    [InlineData("Rate limit reached for gpt-5 in organization org-x on tokens per min (TPM)")]
    // Gemini CLI
    [InlineData("[API Error: You have exhausted your capacity on this model. Your quota will reset after 2h31m.]")]
    [InlineData("Quota exceeded for quota metric 'Gemini 2.5 Pro Requests' and limit 'Gemini 2.5 Pro Requests per day per user per tier'")]
    [InlineData("[API Error: {\"error\":{\"code\":429,\"message\":\"Resource has been exhausted (e.g. check quota).\",\"status\":\"RESOURCE_EXHAUSTED\"}}]")]
    [InlineData("Usage limit reached for all Pro models.")]
    public void Limit_messages_of_every_cli_are_rate_limits(string text) =>
        Assert.Equal(AgentFailureKind.RateLimited, CliText.Classify(text));

    [Theory]
    [InlineData("Created 429 walls")]
    [InlineData("stream disconnected before completion")]
    [InlineData("API Error: 529 overloaded_error")]
    [InlineData("The limit of 30 steps per run_commands call was exceeded by the request")]
    public void Ordinary_errors_are_not_limits(string text) =>
        Assert.NotEqual(AgentFailureKind.RateLimited, CliText.Classify(text));

    [Theory]
    [InlineData("You've hit your usage limit. Try again in 2 hours.", true)]
    [InlineData("You have exhausted your capacity on this model.", true)]
    [InlineData("Quota exceeded for quota metric 'Requests per day'", true)]
    [InlineData("exceeded retry limit, last status: 429 Too Many Requests", false)]
    [InlineData("Reconnecting... 1/5 (429 Too Many Requests)", false)]
    [InlineData("stream disconnected", false)]
    public void Hard_limits_are_told_apart_from_short_bursts(string text, bool hard) =>
        Assert.Equal(hard, CliText.IsHardLimit(text));

    [Fact]
    public void Claude_limit_results_are_rate_limits()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), """
            {"type":"system","subtype":"init","session_id":"s","model":"claude-opus","mcp_servers":[{"name":"acad","status":"connected"}]}
            {"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1736280000","session_id":"s"}
            """);
        var failed = Assert.IsType<TurnFailed>(events[^1]);
        Assert.Equal(AgentFailureKind.RateLimited, failed.Kind);
        Assert.Contains("Claude", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_ends_the_turn_at_the_first_usage_limit_error_but_retries_429_bursts()
    {
        var limit = ParseAll(new CodexStreamParser("acad", null), """
            {"type":"thread.started","thread_id":"t"}
            {"type":"error","message":"You've hit your usage limit. Upgrade to Pro or try again in 2 hours 13 minutes."}
            """);
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(limit[^1]).Kind);

        var burst = ParseAll(new CodexStreamParser("acad", null), """{"type":"error","message":"Reconnecting... 2/5 (429 Too Many Requests)"}""");
        Assert.DoesNotContain(burst, e => e is TurnFailed);
    }

    [Fact]
    public void Gemini_quota_errors_end_the_turn_as_rate_limits()
    {
        var events = ParseAll(new GeminiStreamParser("acad"), """
            {"type":"init","session_id":"g-1","model":"gemini-2.5-pro"}
            {"type":"error","severity":"error","message":"[API Error: You have exhausted your capacity on this model. Your quota will reset after 2h31m.]"}
            """);
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(events[^1]).Kind);

        var warning = ParseAll(new GeminiStreamParser("acad"), """{"type":"error","severity":"warning","message":"Quota exceeded for gemini-2.5-pro, switching to gemini-2.5-flash"}""");
        Assert.DoesNotContain(warning, e => e is TurnFailed);

        var result = ParseAll(new GeminiStreamParser("acad"), """{"type":"result","status":"error","error":{"type":"Error","message":"Quota exceeded for quota metric 'Gemini 2.5 Pro Requests' per day"}}""");
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(result[^1]).Kind);
    }
}
