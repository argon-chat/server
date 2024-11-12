namespace Argon.Api.Grains;

using Interfaces;

#if DEBUG

public class TestGrain([PersistentState("input", "RedisStorage")] IPersistentState<SomeInput> inputStore) : Grain, ITestGrain
{
#region Implementation of ITestGrain

    public async Task<SomeInput> CreateSomeInput(SomeInput input)
    {
        inputStore.State = input;
        await inputStore.WriteStateAsync();
        return inputStore.State;
    }

    public async Task<SomeInput> UpdateSomeInput(SomeInput input)
    {
        inputStore.State = input;
        await inputStore.WriteStateAsync();
        return inputStore.State;
    }

    public async Task<SomeInput> DeleteSomeInput()
    {
        var obj = inputStore.State;
        await inputStore.ClearStateAsync();
        return obj;
    }

    public async Task<SomeInput> GetSomeInput()
    {
        await inputStore.ReadStateAsync();
        return inputStore.State;
    }

#endregion
}

#endif