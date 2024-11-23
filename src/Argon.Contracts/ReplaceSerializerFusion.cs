namespace Argon.Contracts;

using System.Runtime.CompilerServices;
using ActualLab.Rpc;
using ActualLab.Serialization;
using MessagePack.Resolvers;

public static class ReplaceSerializerFusion
{
    [ModuleInitializer]
    internal static void Init()
    {
        MessagePackByteSerializer.DefaultOptions = MessagePackByteSerializer.Default.Options.WithResolver(ContractlessStandardResolver.Instance);
        ByteSerializer.Default                   = MessagePackByteSerializer.Default;
        RpcSerializationFormatResolver.Default = RpcSerializationFormatResolver.Default with
        {
            DefaultClientFormatKey = RpcSerializationFormat.MessagePackV2.Key
        };
    }
}