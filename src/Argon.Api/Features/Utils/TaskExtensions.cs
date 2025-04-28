namespace Argon.Api.Features.Utils;

public static class TaskExtensions
{
    public async static Task<(T1, T2)> WhenAll<T1, T2>(this (Task<T1> task1, Task<T2> task2) tuple)
    {
        var (task1, task2) = tuple;
        await Task.WhenAll(task1, task2);
        return (task1.GetAwaiter().GetResult(), task2.GetAwaiter().GetResult());
    }
}