using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Modeling;
using Xunit;

namespace PbiModelingMcp.Tests.Modeling;

/// <summary>
/// Argument-validation tests for write requests. The full safety pipeline
/// is exercised live in the Phase 6 round-trip; here we just lock the
/// boundary checks the LLM is most likely to hit.
/// </summary>
public class ModelingServiceWriteValidationTests
{
    private static readonly ConnectionDescriptor Descriptor = new("ws", "ds");

    [Theory]
    [InlineData("", "n", "1")]
    [InlineData(" ", "n", "1")]
    [InlineData("t", "", "1")]
    [InlineData("t", "n", "")]
    public async Task AddMeasure_RequiresAllStrings(string table, string name, string dax)
    {
        var svc = TestModelingService.Create();
        var act = () => svc.AddMeasureAsync(new AddMeasureRequest(table, name, dax), Descriptor, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateMeasure_NoFields_Throws()
    {
        var svc = TestModelingService.Create();
        var act = () => svc.UpdateMeasureAsync(
            new UpdateMeasureRequest("t", "n"), Descriptor, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*at least one field*");
    }

    [Theory]
    [InlineData("", "n")]
    [InlineData("t", "")]
    public async Task DeleteMeasure_RequiresTableAndName(string table, string name)
    {
        var svc = TestModelingService.Create();
        var act = () => svc.DeleteMeasureAsync(
            new DeleteMeasureRequest(table, name), Descriptor, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
