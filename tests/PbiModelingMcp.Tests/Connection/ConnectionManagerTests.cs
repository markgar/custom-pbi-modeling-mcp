using FluentAssertions;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging.Abstractions;
using PbiModelingMcp.Connection;
using Xunit;

namespace PbiModelingMcp.Tests.Connection;

public class ConnectionManagerTests
{
    [Fact]
    public async Task WithServerAsync_NullDescriptor_Throws()
    {
        var mgr = MakeManager(new ThrowingFactory());

        var act = () => mgr.WithServerAsync<int>(
            descriptor: null!,
            (_, _, _) => Task.FromResult(0),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WithServerAsync_NullBody_Throws()
    {
        var mgr = MakeManager(new ThrowingFactory());
        var act = () => mgr.WithServerAsync<int>(
            new ConnectionDescriptor("ws", "ds"),
            body: null!,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WithServerAsync_DatasetNotFoundOnEmptyServer_Throws()
    {
        // Fresh new Server() has no Databases — the lookup inside
        // WithServerAsync should fail with our diagnostic, not a TOM exception.
        var mgr = MakeManager(new EmptyServerFactory());

        var act = () => mgr.WithServerAsync<int>(
            new ConnectionDescriptor("ws", "missing"),
            (_, _, _) => Task.FromResult(1),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'missing'*ws*");
    }

    [Fact]
    public async Task WithServerAsync_SameDescriptor_SerializesCalls()
    {
        // Two parallel calls to the same descriptor must not run their
        // factory.OpenAsync overlap — the per-descriptor semaphore guards it.
        var factory = new TimingFactory(delayMs: 100);
        var mgr = MakeManager(factory);

        var d = new ConnectionDescriptor("ws", "ds");
        var t1 = Swallow(() => mgr.WithServerAsync<int>(d, (_, _, _) => Task.FromResult(0), CancellationToken.None));
        var t2 = Swallow(() => mgr.WithServerAsync<int>(d, (_, _, _) => Task.FromResult(0), CancellationToken.None));

        await Task.WhenAll(t1, t2);

        factory.MaxConcurrent.Should().Be(1, "the same descriptor must serialize");
        factory.TotalCalls.Should().Be(2);
    }

    [Fact]
    public async Task WithServerAsync_DifferentDescriptors_RunInParallel()
    {
        var factory = new TimingFactory(delayMs: 100);
        var mgr = MakeManager(factory);

        var t1 = Swallow(() => mgr.WithServerAsync<int>(
            new ConnectionDescriptor("ws", "a"),
            (_, _, _) => Task.FromResult(0), CancellationToken.None));
        var t2 = Swallow(() => mgr.WithServerAsync<int>(
            new ConnectionDescriptor("ws", "b"),
            (_, _, _) => Task.FromResult(0), CancellationToken.None));

        await Task.WhenAll(t1, t2);

        factory.MaxConcurrent.Should().Be(2, "different descriptors should not block each other");
    }

    private static ConnectionManager MakeManager(ITomServerFactory factory)
        => new(factory, NullLogger<ConnectionManager>.Instance);

    private static async Task Swallow(Func<Task> act)
    {
        try { await act(); }
        catch { /* lock test only cares about timing */ }
    }

    private sealed class ThrowingFactory : ITomServerFactory
    {
        public Task<Server> OpenAsync(ConnectionDescriptor descriptor, CancellationToken ct)
            => throw new InvalidOperationException("factory should not be called");
    }

    private sealed class EmptyServerFactory : ITomServerFactory
    {
        public Task<Server> OpenAsync(ConnectionDescriptor descriptor, CancellationToken ct)
            => Task.FromResult(new Server());
    }

    private sealed class TimingFactory : ITomServerFactory
    {
        private readonly int _delayMs;
        private int _current;
        private int _max;
        private int _total;

        public TimingFactory(int delayMs) => _delayMs = delayMs;

        public int MaxConcurrent => _max;
        public int TotalCalls => _total;

        public async Task<Server> OpenAsync(ConnectionDescriptor descriptor, CancellationToken ct)
        {
            var v = Interlocked.Increment(ref _current);
            InterlockedMax(ref _max, v);
            Interlocked.Increment(ref _total);
            try
            {
                await Task.Delay(_delayMs, ct).ConfigureAwait(false);
                return new Server();
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int initial;
            do
            {
                initial = target;
                if (initial >= value)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
        }
    }
}
