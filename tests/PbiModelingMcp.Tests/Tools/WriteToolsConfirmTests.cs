using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using PbiModelingMcp.Tools;
using Xunit;

namespace PbiModelingMcp.Tests.Tools;

public class WriteToolsConfirmTests
{
    [Fact]
    public async Task ElicitConfirm_Accepted_Completes()
    {
        ElicitResult Respond(ElicitRequestParams req) => new()
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonDocument.Parse("true").RootElement,
            },
        };

        await WriteTools.ElicitConfirmAsync(MakeElicit(Respond), "Sales", "Revenue", CancellationToken.None);
    }

    [Fact]
    public async Task ElicitConfirm_AcceptedButFalse_ThrowsCancel()
    {
        ElicitResult Respond(ElicitRequestParams req) => new()
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonDocument.Parse("false").RootElement,
            },
        };

        var act = () => WriteTools.ElicitConfirmAsync(MakeElicit(Respond), "Sales", "Revenue", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("*declined*");
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("reject")]
    public async Task ElicitConfirm_NonAcceptAction_ThrowsCancel(string action)
    {
        ElicitResult Respond(ElicitRequestParams req) => new() { Action = action };

        var act = () => WriteTools.ElicitConfirmAsync(MakeElicit(Respond), "Sales", "Revenue", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage($"*action='{action}'*");
    }

    [Fact]
    public async Task ElicitConfirm_ClientDoesNotSupportElicit_ThrowsHelpfulError()
    {
        Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>> elicit =
            (_, _) => throw new InvalidOperationException("Client does not support elicitation.");

        var act = () => WriteTools.ElicitConfirmAsync(elicit, "Sales", "Revenue", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PBI_MCP__RequireConfirmDelete=false*");
    }

    [Fact]
    public async Task ElicitConfirm_NoConfirmKeyInContent_ThrowsCancel()
    {
        ElicitResult Respond(ElicitRequestParams req) => new()
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>(),
        };

        var act = () => WriteTools.ElicitConfirmAsync(MakeElicit(Respond), "Sales", "Revenue", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("*declined*");
    }

    [Fact]
    public async Task ElicitConfirm_PassesTableAndNameInPrompt()
    {
        ElicitRequestParams? captured = null;
        ElicitResult Respond(ElicitRequestParams req)
        {
            captured = req;
            return new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>
                {
                    ["confirm"] = JsonDocument.Parse("true").RootElement,
                },
            };
        }

        await WriteTools.ElicitConfirmAsync(MakeElicit(Respond), "Sales", "RevenueYTD", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("'RevenueYTD'").And.Contain("'Sales'");
        captured.RequestedSchema!.Required.Should().Contain("confirm");
        captured.RequestedSchema.Properties.Should().ContainKey("confirm");
    }

    [Fact]
    public async Task ElicitConfirm_NullDelegate_Throws()
    {
        var act = () => WriteTools.ElicitConfirmAsync(null!, "t", "n", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>> MakeElicit(
        Func<ElicitRequestParams, ElicitResult> respond)
        => (req, _) => ValueTask.FromResult(respond(req));
}
