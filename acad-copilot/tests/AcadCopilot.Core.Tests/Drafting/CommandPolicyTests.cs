using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class CommandPolicyTests
{
    [Theory]
    [InlineData("_.-layer", "LAYER")]
    [InlineData("'ZOOM", "ZOOM")]
    [InlineData("  line ", "LINE")]
    [InlineData("+OPTIONS", "OPTIONS")]
    [InlineData("._-INSERT", "INSERT")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeName_strips_prefixes_and_upper_cases(string? command, string expected)
    {
        Assert.Equal(expected, CommandPolicy.NormalizeName(command));
    }

    [Theory]
    [InlineData("LINE")]
    [InlineData("pline")]
    [InlineData("_.OFFSET")]
    [InlineData("-LAYER")]
    [InlineData("-HATCH")]
    [InlineData("TEXT")]
    [InlineData("DIMLINEAR")]
    [InlineData("WALLADD")]
    [InlineData("3DORBIT")]
    [InlineData("'ZOOM")]
    [InlineData("SETVAR")]
    [InlineData("ERASE")]
    [InlineData("CHPROP")]
    [InlineData("-INSERT")]
    public void Drafting_commands_are_allowed(string command)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.IsAllowed, verdict.Describe());
    }

    [Theory]
    [InlineData("SHELL")]
    [InlineData("SH")]
    [InlineData("_.SHELL")]
    [InlineData("-SHELL")]
    [InlineData("DEL")]
    [InlineData("START")]
    [InlineData("NOTEPAD")]
    [InlineData("APPLOAD")]
    [InlineData("AP")]
    [InlineData("NETLOAD")]
    [InlineData("ARX")]
    [InlineData("VBAIDE")]
    [InlineData("VBARUN")]
    [InlineData("-VBARUN")]
    [InlineData("SCRIPT")]
    [InlineData("SCR")]
    [InlineData("ACTIVEXFOO")]
    [InlineData("QUIT")]
    [InlineData("EXIT")]
    [InlineData("CLOSE")]
    [InlineData("CLOSEALL")]
    [InlineData("OPEN")]
    [InlineData("NEW")]
    [InlineData("QNEW")]
    [InlineData("RECOVER")]
    [InlineData("VLISP")]
    [InlineData("WEBLOAD")]
    [InlineData("CUILOAD")]
    [InlineData("SECURELOAD")]
    [InlineData("UNDO")]
    [InlineData("U")]
    [InlineData("MULTIPLE")]
    public void Dangerous_commands_are_blocked(string command)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.IsBlocked, command);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData("(command \"LINE\")")]
    [InlineData("!a")]
    [InlineData("  (setq a 1)")]
    public void AutoLisp_commands_are_blocked(string command)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.IsBlocked);
        Assert.Contains("AutoLISP", verdict.Reason);
    }

    [Theory]
    [InlineData("SAVE")]
    [InlineData("QSAVE")]
    [InlineData("SAVEAS")]
    [InlineData("-WBLOCK")]
    [InlineData("EXPORT")]
    [InlineData("EXPORTPDF")]
    [InlineData("EXP")]
    [InlineData("-PLOT")]
    [InlineData("-PUBLISH")]
    [InlineData("-PURGE")]
    [InlineData("-PU")]
    [InlineData("-LAYDEL")]
    [InlineData("AUDIT")]
    [InlineData("DXFOUT")]
    public void File_writing_and_destructive_commands_need_approval(string command)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.NeedsApproval, command + ": " + verdict.Describe());
    }

    [Theory]
    [InlineData("LAYER", "-LAYER")]
    [InlineData("LA", "-LAYER")]
    [InlineData("_.LA", "_.-LAYER")]
    [InlineData("'LAYER", "'-LAYER")]
    [InlineData("STYLE", "-STYLE")]
    [InlineData("D", "-DIMSTYLE")]
    [InlineData("UNITS", "-UNITS")]
    [InlineData("LINETYPE", "-LINETYPE")]
    [InlineData("I", "-INSERT")]
    [InlineData("HATCH", "-HATCH")]
    [InlineData("BH", "-HATCH")]
    [InlineData("H", "-HATCH")]
    [InlineData("MTEXT", "-MTEXT")]
    [InlineData("T", "-MTEXT")]
    [InlineData("PLOT", "-PLOT")]
    [InlineData("PRINT", "-PLOT")]
    [InlineData("PURGE", "-PURGE")]
    [InlineData("BLOCK", "-BLOCK")]
    public void Dialog_commands_have_a_command_line_version(string command, string expected)
    {
        Assert.Equal(expected, CommandPolicy.CommandLineVersion(command));
    }

    [Theory]
    [InlineData("-LAYER")]
    [InlineData("-LA")]
    [InlineData("LINE")]
    [InlineData("TEXT")]
    [InlineData("OPTIONS")]
    [InlineData("(foo)")]
    public void Commands_without_a_dialog_version_are_not_rewritten(string command)
    {
        Assert.Null(CommandPolicy.CommandLineVersion(command));
    }

    [Theory]
    [InlineData("LAYER")]
    [InlineData("STYLE")]
    [InlineData("HATCH")]
    public void Dialog_commands_typed_without_dash_are_blocked_with_a_hint(string command)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.IsBlocked);
        Assert.Contains("-" + command, verdict.Reason);
    }

    [Theory]
    [InlineData("OPTIONS", "SETVAR")]
    [InlineData("OP", "SETVAR")]
    [InlineData("PROPERTIES", "CHPROP")]
    [InlineData("PR", "CHPROP")]
    [InlineData("DDEDIT", "CHANGE")]
    [InlineData("ED", "CHANGE")]
    [InlineData("BEDIT", "-BLOCK")]
    [InlineData("XATTACH", "-XREF")]
    public void Dialog_only_commands_are_blocked_with_an_alternative(string command, string alternative)
    {
        var verdict = CommandPolicy.ClassifyCommand(command);
        Assert.True(verdict.IsBlocked);
        Assert.Contains(alternative, verdict.Reason);
    }

    [Theory]
    [InlineData("LINE STRING")]
    [InlineData("")]
    [InlineData("_.")]
    [InlineData("LINE;")]
    public void Malformed_command_names_are_blocked(string command)
    {
        Assert.True(CommandPolicy.ClassifyCommand(command).IsBlocked);
    }

    [Theory]
    [InlineData("(startapp \"calc\")")]
    [InlineData("!myvar")]
    [InlineData("  (command)")]
    public void AutoLisp_inputs_are_blocked(string input)
    {
        var verdict = CommandPolicy.ClassifyInput(input);
        Assert.True(verdict.IsBlocked);
        Assert.Contains("AutoLISP", verdict.Reason);
    }

    [Theory]
    [InlineData("'SHELL")]
    [InlineData("'_.APPLOAD")]
    [InlineData("'OPTIONS")]
    public void Blocked_transparent_commands_in_inputs_are_blocked(string input)
    {
        var verdict = CommandPolicy.ClassifyInput(input);
        Assert.True(verdict.IsBlocked);
        Assert.StartsWith("'", verdict.Subject);
    }

    [Theory]
    [InlineData("'ZOOM")]
    [InlineData("'-PAN")]
    [InlineData("0,0")]
    [InlineData("@30'<0")]
    [InlineData("")]
    [InlineData("BED ROOM (12'x14')")]
    [InlineData("OPEN TO SKY")]
    [InlineData("SHELL HOUSE")]
    [InlineData("MASTER'S BED")]
    public void Ordinary_inputs_are_allowed(string input)
    {
        Assert.True(CommandPolicy.ClassifyInput(input).IsAllowed, input);
    }

    [Theory]
    [InlineData("SHELL")]
    [InlineData("_.APPLOAD")]
    [InlineData("open")]
    [InlineData("QUIT")]
    [InlineData("START")]
    [InlineData("VBARUN")]
    public void Lone_forbidden_command_names_are_blocked_as_inputs(string input)
    {
        var verdict = CommandPolicy.ClassifyInput(input);
        Assert.True(verdict.IsBlocked, input);
        Assert.Contains("could start", verdict.Reason);
    }

    [Theory]
    [InlineData("U")]
    [InlineData("E")]
    [InlineData("C")]
    [InlineData("SAVE")]
    [InlineData("-1")]
    [InlineData("ALL")]
    [InlineData("LINE")]
    public void Option_keywords_and_harmless_words_stay_allowed(string input)
    {
        Assert.True(CommandPolicy.ClassifyInput(input).IsAllowed, input);
    }

    [Fact]
    public void Lone_external_pgp_command_is_blocked_as_input()
    {
        var aliases = CommandAliases.FromPgp("MYCALC, START CALC, 1,,\n");
        Assert.True(CommandPolicy.ClassifyInput("MYCALC", aliases).IsBlocked);
        Assert.True(CommandPolicy.ClassifyInput("MYCALC").IsAllowed);
    }

    [Theory]
    [InlineData("SECURELOAD")]
    [InlineData("trustedpaths")]
    [InlineData("_TRUSTEDDOMAINS")]
    public void Security_system_variables_are_blocked_as_inputs(string input)
    {
        Assert.True(CommandPolicy.ClassifyInput(input).IsBlocked);
    }

    [Fact]
    public void Setvar_of_a_security_variable_is_blocked()
    {
        var findings = CommandPolicy.CheckStep("SETVAR", ["SECURELOAD", "0"]);
        Assert.Single(findings);
        Assert.True(findings[0].IsBlocked);
        Assert.True(CommandPolicy.CheckStep("SETVAR", ["OSMODE", "0"]).Count == 0);
    }

    [Theory]
    [InlineData("ERASE", "ALL")]
    [InlineData("E", "all")]
    [InlineData("_.ERASE", "_ALL")]
    [InlineData("-ERASE", " ALL ")]
    [InlineData("CUTCLIP", "ALL")]
    public void Erase_all_needs_approval(string command, string input)
    {
        var verdict = CommandPolicy.ClassifyStep(command, [input, ""]);
        Assert.True(verdict.NeedsApproval, verdict.Describe());
        Assert.Contains("ALL", verdict.Subject);
    }

    [Fact]
    public void Erase_with_a_window_is_allowed()
    {
        Assert.True(CommandPolicy.ClassifyStep("ERASE", ["W", "0,0", "10,10", ""]).IsAllowed);
        Assert.True(CommandPolicy.ClassifyStep("MOVE", ["ALL", "", "0,0", "@10,0"]).IsAllowed);
    }

    [Fact]
    public void CheckStep_puts_blocked_findings_first()
    {
        var findings = CommandPolicy.CheckStep("QSAVE", ["(load \"x\")"]);
        Assert.Equal(2, findings.Count);
        Assert.True(findings[0].IsBlocked);
        Assert.True(findings[1].NeedsApproval);
        Assert.True(CommandPolicy.ClassifyStep("QSAVE", ["(load \"x\")"]).IsBlocked);
    }

    [Fact]
    public void Parse_resolves_aliases_and_keeps_prefixes()
    {
        var parsed = CommandPolicy.Parse("_.-la");
        Assert.Equal("_.", parsed.Prefix);
        Assert.True(parsed.Dash);
        Assert.Equal("LA", parsed.Name);
        Assert.Equal("LAYER", parsed.Canonical);
        Assert.True(parsed.CanonicalDash);
        Assert.False(parsed.Transparent);
        Assert.True(CommandPolicy.Parse("'ZOOM").Transparent);
        Assert.Equal("CHANGE", CommandPolicy.Parse("-CH").Canonical);
        Assert.False(CommandPolicy.Parse("-CH").CanonicalDash);
    }

    [Fact]
    public void Pgp_aliases_and_external_commands_are_honoured()
    {
        const string pgp = """
            ; comment line
            ;  -- external commands --
            SHELL,       ,         1,*OS Command: ,
            DIR,       DIR,            8,File specification: ,
            MYCALC,    START CALC,     1,,
            ; aliases
            A,         *ARC
            XX,        *SHELL
            ZZ,        *APPLOAD
            KK,        *-LAYER
            BAD LINE WITHOUT COMMA
            """;
        var aliases = CommandAliases.FromPgp(pgp);
        Assert.Equal(3, aliases.ExternalCommandCount);
        Assert.True(CommandPolicy.ClassifyCommand("MYCALC", aliases).IsBlocked);
        Assert.Contains("acad.pgp", CommandPolicy.ClassifyCommand("MYCALC", aliases).Reason);
        Assert.True(CommandPolicy.ClassifyCommand("XX", aliases).IsBlocked);
        Assert.True(CommandPolicy.ClassifyCommand("ZZ", aliases).IsBlocked);
        Assert.True(CommandPolicy.ClassifyCommand("A", aliases).IsAllowed);
        Assert.True(CommandPolicy.ClassifyCommand("KK", aliases).IsAllowed);
        Assert.Null(CommandPolicy.CommandLineVersion("KK", aliases));
        Assert.True(CommandPolicy.ClassifyCommand("MYCALC").IsAllowed, "unknown names are allowed without the pgp (AutoCAD reports them as unknown)");
        Assert.True(CommandPolicy.ClassifyCommand("LA", aliases).IsBlocked, "default aliases stay in effect");
    }

    [Fact]
    public void Empty_pgp_gives_the_defaults()
    {
        var aliases = CommandAliases.FromPgp(null);
        Assert.Equal(CommandAliases.Default.AliasCount, aliases.AliasCount);
        Assert.Equal(0, aliases.ExternalCommandCount);
        Assert.Equal("-LAYER", aliases.Resolve("-LA"));
        Assert.Equal("-INSERT", aliases.Resolve("-I"));
        Assert.Null(aliases.Resolve("LINE"));
    }

    [Theory]
    [InlineData("SHELL", CommandDecision.Blocked)]
    [InlineData("#APPLOAD", CommandDecision.Blocked)]
    [InlineData("VBARUN", CommandDecision.Blocked)]
    [InlineData("OPEN", CommandDecision.Blocked)]
    [InlineData("QSAVE", CommandDecision.NeedsApproval)]
    [InlineData("#SAVEAS", CommandDecision.NeedsApproval)]
    [InlineData("UNDO", CommandDecision.Allowed)]
    [InlineData("U", CommandDecision.Allowed)]
    [InlineData("LAYER", CommandDecision.Allowed)]
    [InlineData("OPTIONS", CommandDecision.Allowed)]
    [InlineData("LINE", CommandDecision.Allowed)]
    [InlineData("", CommandDecision.Allowed)]
    [InlineData("#", CommandDecision.Allowed)]
    [InlineData(null, CommandDecision.Allowed)]
    public void Running_commands_are_judged_on_security_rules_only(string? globalName, CommandDecision expected)
    {
        Assert.Equal(expected, CommandPolicy.ClassifyRunningCommand(globalName).Decision);
    }

    [Fact]
    public void Running_external_pgp_commands_are_blocked()
    {
        var aliases = CommandAliases.FromPgp("MYCALC, START CALC, 1,,\n");
        Assert.True(CommandPolicy.ClassifyRunningCommand("MYCALC", aliases).IsBlocked);
    }

    [Fact]
    public void Verdict_describe_includes_reason()
    {
        var verdict = CommandPolicy.ClassifyCommand("SHELL");
        Assert.StartsWith("SHELL: ", verdict.Describe());
        Assert.Equal("LINE", CommandPolicy.ClassifyCommand("LINE").Describe());
    }
}
