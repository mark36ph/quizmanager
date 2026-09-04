using System.Windows;

namespace QuizManager.Rendering;

/// <summary>
/// Runs WPF rendering on a dedicated STA thread so the desktop UI never performs
/// heavy rendering work and never blocks its dispatcher.
/// </summary>
public sealed class StaRenderWorker
{
    public Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application();
                work();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "QuizManager Rendering STA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application();
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "QuizManager Rendering STA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
