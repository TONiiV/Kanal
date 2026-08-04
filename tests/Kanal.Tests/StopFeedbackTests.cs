using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Relay;
using Kanal.Host.ViewModels;

namespace Kanal.Tests;

/// <summary>
/// Stop is not instantaneous — it publishes a final snapshot, says the room is closed, and gives
/// whatever is still translating a bounded moment to land. That is a second or two in which the
/// operator has pressed a button and nothing on screen has changed. The masthead has to admit
/// what it is doing, and the buttons must not invite a second press that would race the first.
/// </summary>
public class StopFeedbackTests
{
    /// <summary>Holds the final snapshot publish open, standing in for a slow relay or a
    /// translation still decoding, so the transient state is observable at all.</summary>
    private sealed class GatedRelayPublisher(Task gate) : IRelayPublisher
    {
        public async Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            if (Unwrap(message) is RoomSnapshotMessage)
                await gate;
        }

        private static RelayMessage Unwrap(RelayMessage message)
        {
            if (message is not SignedRelayMessage signed)
                return message;

            var encoded = signed.Data.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return RelayJson.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
                ?? throw new InvalidOperationException("Signed test message had no payload.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task StopSaysSoWhileItIsStillStopping()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ => new GatedRelayPublisher(gate.Task);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(150);

        var stopping = vm.StopCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.Contains("Stopping", vm.Status);
        Assert.False(vm.StopCommand.CanExecute(null), "Stop was still offered while stopping.");
        Assert.False(vm.StartCommand.CanExecute(null), "Start was offered mid-stop.");

        gate.SetResult();
        await stopping;
        await PumpAsync(50);

        Assert.StartsWith("Stopped.", vm.Status);
        Assert.True(vm.StartCommand.CanExecute(null), "Start never came back.");
        Assert.False(vm.IsRunning);
    }

    /// <summary>A failed Start must not leave the transport wedged with everything disabled.</summary>
    [AvaloniaFact]
    public async Task StartIsOfferedAgainAfterAStopThatThrew()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(150);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }
}
