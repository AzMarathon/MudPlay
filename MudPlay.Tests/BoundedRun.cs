using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace MudPlay.Tests;

// Runs work that never returns if a chain guard is lost. The work has its own
// background thread: a walk that spins fails its test at the limit and dies with
// the test process, where run on the test's thread it would hang the suite.
internal static class BoundedRun
{
    public static T Finish<T>(Func<T> work, int seconds = 10)
    {
        T result = default!;
        Exception? thrown = null;
        Thread thread = new(() =>
        {
            try { result = work(); }
            catch (Exception ex) { thrown = ex; }
        })
        { IsBackground = true };
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)),
            $"did not finish in {seconds} s: a chain is being followed without end");
        if (thrown is not null) ExceptionDispatchInfo.Capture(thrown).Throw();
        return result;
    }
}
