using System.Threading;
using K2D2.Transfer;
using NUnit.Framework;

// The pilots' Fly() waits only while a job runs (job.Running). The job object itself is never null
// (a readonly field since b9bf725): a null check on it stopped both pilots on every frame.
public class PilotJobTests
{
    [Test]
    public void AFreshJob_IsNotRunning_SoTheFlightProceeds()
    {
        var job = new PilotJob();
        Assert.IsFalse(job.Running);
    }

    [Test]
    public void AJob_RunsUntilPolledAfterItsEnd_ThenTheFlightProceeds()
    {
        var job = new PilotJob();
        using (var gate = new ManualResetEventSlim(false))
        {
            int result = 0;
            job.Run(token => { gate.Wait(5000); return 42; }, t => result = t.Result);
            Assert.IsTrue(job.Running);
            job.Poll(e => Assert.Fail(e.Message));
            Assert.IsTrue(job.Running, "still working: Poll leaves it running");
            gate.Set();
            for (int i = 0; i < 500 && job.Running; i++)
            {
                Thread.Sleep(10);
                job.Poll(e => Assert.Fail(e.Message));
            }
            Assert.IsFalse(job.Running);
            Assert.AreEqual(42, result);
        }
    }

    [Test]
    public void ACancelledJob_IsNotRunning()
    {
        var job = new PilotJob();
        job.Run(token => { token.WaitHandle.WaitOne(5000); return 0; }, t => { });
        job.Cancel();
        Assert.IsFalse(job.Running);
    }
}
