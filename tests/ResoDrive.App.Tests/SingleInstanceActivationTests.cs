namespace ResoDrive.App.Tests;

public sealed class SingleInstanceActivationTests
{
    [Fact]
    public Task ScopeClient_WaitsForPrimaryReadinessWithoutTakingInstanceMutex() => RunOnDedicatedThreadAsync(() =>
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceActivation(scope);
        using var received = new ManualResetEventSlim();
        primary.Listen(request =>
        {
            received.Set();
            request.Acknowledge();
        });

        var acknowledged = SingleInstanceActivation.RequestShow(
            scope,
            TimeSpan.FromSeconds(5));

        Assert.True(received.IsSet);
        Assert.True(acknowledged);
    });

    [Fact]
    public Task SecondaryRequest_WaitsUntilPrimaryAcknowledgesVisibleWindow() => RunOnDedicatedThreadAsync(() =>
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceActivation(scope);
        Assert.True(primary.IsFirstInstance);

        using var requestReceived = new ManualResetEventSlim();
        using var secondaryDone = new ManualResetEventSlim();
        SingleInstanceActivation.ActivationRequest? request = null;
        primary.Listen(received =>
        {
            request = received;
            requestReceived.Set();
        });
        var acknowledged = false;
        var secondaryWasFirst = true;
        var secondaryThread = new Thread(() =>
        {
            using var secondary = new SingleInstanceActivation(scope);
            secondaryWasFirst = secondary.IsFirstInstance;
            acknowledged = secondary.RequestShow(TimeSpan.FromSeconds(5));
            secondaryDone.Set();
        });
        secondaryThread.Start();

        Assert.True(requestReceived.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(secondaryDone.IsSet);
        Assert.True(request!.Acknowledge());
        Assert.True(secondaryDone.Wait(TimeSpan.FromSeconds(5)));
        secondaryThread.Join();
        Assert.False(secondaryWasFirst);
        Assert.True(acknowledged);
    });

    [Fact]
    public Task SecondaryRequest_TimesOutWhenPrimaryCannotShowWindow() => RunOnDedicatedThreadAsync(() =>
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceActivation(scope);
        primary.Listen(_ => { });

        using var secondaryDone = new ManualResetEventSlim();
        var acknowledged = true;
        var secondaryThread = new Thread(() =>
        {
            using var secondary = new SingleInstanceActivation(scope);
            acknowledged = secondary.RequestShow(TimeSpan.FromMilliseconds(100));
            secondaryDone.Set();
        });
        secondaryThread.Start();

        Assert.True(secondaryDone.Wait(TimeSpan.FromSeconds(5)));
        secondaryThread.Join();
        Assert.False(acknowledged);
    });

    [Fact]
    public Task RequestMadeBeforeListenerRegistration_IsDelivered() => RunOnDedicatedThreadAsync(() =>
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceActivation(scope);
        using var secondaryStarted = new ManualResetEventSlim();
        using var secondaryDone = new ManualResetEventSlim();
        var acknowledged = false;
        var secondaryThread = new Thread(() =>
        {
            using var secondary = new SingleInstanceActivation(scope);
            secondaryStarted.Set();
            acknowledged = secondary.RequestShow(TimeSpan.FromSeconds(5));
            secondaryDone.Set();
        });
        secondaryThread.Start();

        Assert.True(secondaryStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(secondaryDone.IsSet);
        using var received = new ManualResetEventSlim();
        primary.Listen(request =>
        {
            received.Set();
            request.Acknowledge();
        });

        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(secondaryDone.Wait(TimeSpan.FromSeconds(5)));
        secondaryThread.Join();
        Assert.True(acknowledged);
    });

    [Fact]
    public Task ConcurrentSecondaries_ReceiveOnlyTheirOwnAcknowledgements() => RunOnDedicatedThreadAsync(() =>
    {
        const int secondaryCount = 8;
        var scope = $"Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceActivation(scope);
        using var allReceived = new CountdownEvent(secondaryCount);
        var requests = new System.Collections.Concurrent.ConcurrentBag<
            SingleInstanceActivation.ActivationRequest>();
        primary.Listen(request =>
        {
            requests.Add(request);
            allReceived.Signal();
        });

        var results = new bool[secondaryCount];
        var threads = Enumerable.Range(0, secondaryCount).Select(index =>
            new Thread(() =>
            {
                using var secondary = new SingleInstanceActivation(scope);
                results[index] = secondary.RequestShow(TimeSpan.FromSeconds(10));
            })).ToArray();
        foreach (var thread in threads)
            thread.Start();

        Assert.True(allReceived.Wait(TimeSpan.FromSeconds(10)));
        Assert.All(results, result => Assert.False(result));
        foreach (var request in requests)
            Assert.True(request.Acknowledge());
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.All(results, result => Assert.True(result));
    });

    // Keep mutex acquisition/disposal on one thread without blocking xUnit's
    // worker pool, which also runs the asynchronous pipe listener and replies.
    private static Task RunOnDedicatedThreadAsync(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                test();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
